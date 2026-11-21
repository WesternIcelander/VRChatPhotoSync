using System.Reflection;

namespace VRChatPhotoSync;

static class Program
{
    /// <summary>How long an error stays in the status line before the normal status comes back.</summary>
    private static readonly TimeSpan ErrorNoticeTime = TimeSpan.FromSeconds(60);

    /// <summary>
    ///  The main entry point for the application. The order matters (see PLAN.md, "App lifecycle wiring"):
    ///  single instance, exception handlers, config/VRChat settings/state, the server window if not set up, the
    ///  pipeline and heartbeat, then the message loop; a normal exit stops gracefully.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        // 1. Single instance. A killed copy leaves the mutex abandoned, which counts as acquired; a copy that is
        //    still exiting (VRCX kill-then-relaunch) gets a few seconds. Otherwise show the running copy and exit.
        using var instance = SingleInstance.TryAcquire(SingleInstance.DefaultName, SingleInstance.DefaultWait);
        if (instance is null)
        {
            SingleInstance.SignalRunning(SingleInstance.DefaultName);
            return;
        }

        var options = CommandLineOptions.Parse(args);
        var store = new ConfigStore();
        using var logger = new FileLogger(FileLogger.ForConfigDirectory(store.Directory));
        if (instance.WasAbandoned)
            logger.Info("The previous copy was killed (abandoned single-instance mutex).");

        // 2. Global exception handlers: log, and show the error in the status line for a while.
        MainForm? form = null;
        System.Threading.Timer? noticeTimer = null;
        var stopReason = "exit";
        void ReportError(string message, Exception? ex)
        {
            logger.Error(message, ex);
            var viewModel = form?.ViewModel;
            if (viewModel is null)
                return;
            viewModel.SetNotice($"Error: {ex?.Message ?? message} (see the log)");
            noticeTimer?.Dispose();
            noticeTimer = new System.Threading.Timer(_ => viewModel.SetNotice(null), null, ErrorNoticeTime, Timeout.InfiniteTimeSpan);
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError("Unhandled UI exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            stopReason = "crash";
            logger.Error("Unhandled exception.", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ReportError("Unobserved background task exception.", e.Exception.GetBaseException());
        };

        if (options.Errors.Count > 0)
        {
            foreach (var error in options.Errors)
                logger.Warn($"Command line: {error}");
            MessageBox.Show(
                string.Join(Environment.NewLine, options.Errors),
                "VRChat PhotoSync: command-line options",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // 3. Config, VRChat settings and state (the session is resolved when the state loads).
        var loaded = store.Load();
        if (loaded.Error is not null)
            logger.Warn($"Config: {loaded.Error} Using the defaults.");
        // The command line overrides the file for this run only: savedConfig is what gets
        // written back when settings change, runConfig is what this run uses.
        var savedConfig = loaded.Config;
        var runConfig = options.ApplyTo(savedConfig);
        // The key is never logged; this is only a safety net in case an error message ever echoes it.
        logger.AddSecret(SecretProtector.Unprotect(runConfig.ApiKeyProtected));

        var vrchat = VRChatSettings.Load(options.Folder, log: logger.Warn);
        logger.LogStart(AppVersion, store.Directory, vrchat);
        logger.Info($"Server: {(string.IsNullOrWhiteSpace(runConfig.ServerUrl) ? "(not set)" : runConfig.ServerUrl)}" +
                    $"; API key {(string.IsNullOrWhiteSpace(runConfig.ApiKeyProtected) ? "not set" : "set")}.");

        using var clientHost = new ImmichClientHost();
        var startup = new StartupContext(options, store, loaded, savedConfig, runConfig, vrchat, logger);
        form = new MainForm(startup);
        var mainForm = form;
        using var runtime = new AppRuntime(
            new UploadStateStore(store.Directory),
            vrchat.PictureFolder,
            () => mainForm.Config.Current,
            () => clientHost.Current,
            log: logger.Info,
            warn: logger.Warn);
        runtime.Worker.AttachLog(logger);
        runtime.Scanner.AttachLog(logger);
        lock (runtime.Worker.StateLock)
            mainForm.ViewModel.LoadSession(runtime.State.Session);

        mainForm.Attach(clientHost);
        mainForm.Attach(runtime.Queue);
        mainForm.Attach(runtime.Worker);
        mainForm.Attach(runtime.Scanner);
        runtime.WatcherChanged += mainForm.Attach;
        instance.ListenForShow(() => InvokeOnForm(mainForm, mainForm.RestoreWindow));

        // 4. The Immich server window, if not set up (or --settings). It opens as soon as the main window's handle
        //    exists (also when starting in the tray), so it can count existing photos and its Save can restart the
        //    client and resume the worker. Until then the worker waits in "not set up" and new photos queue up.
        if (startup.ShouldOpenSettings)
        {
            var opened = false;
            mainForm.HandleCreated += (_, _) =>
            {
                if (opened)
                    return;
                opened = true;
                mainForm.BeginInvoke(mainForm.ShowServerSettings);
            };
        }

        // 5. The client, the watchers, the startup catch-up and the worker; 6. the heartbeat (started by Start).
        if (!clientHost.Restart(runConfig))
            logger.Warn("No Immich client: the server URL or API key isn't set up.");
        VRChatConfigWatcher? configWatcher = null;
        if (options.Folder is null && vrchat.ConfigPath is not null)
            configWatcher = new VRChatConfigWatcher(vrchat.ConfigPath, vrchat, () => VRChatSettings.Load(log: logger.Warn), log: logger.Info);
        runtime.Start(configWatcher);
        if (runtime.Watcher is { } watcher)
            mainForm.Attach(watcher);

        try
        {
            // 7. The message loop.
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            stopReason = "crash";
            logger.Error("The message loop failed.", ex);
        }
        finally
        {
            // A normal exit (the user, or Windows shutting down): stop intake, give the current upload up to
            // about 10 s, save the state. A VRCX kill never gets here, and nothing depends on it.
            try
            {
                Task.Run(() => runtime.StopAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Error("Stopping failed.", ex);
            }
            noticeTimer?.Dispose();
            logger.LogStop(stopReason, vrchat);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the form's UI thread, if the form is still open.</summary>
    private static void InvokeOnForm(Form form, Action action)
    {
        try
        {
            if (form.IsHandleCreated && !form.IsDisposed)
                form.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing.
        }
    }

    /// <summary>The informational version (without any source-revision suffix).</summary>
    internal static string AppVersion
    {
        get
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? typeof(Program).Assembly.GetName().Version?.ToString()
                          ?? "?";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }
}

/// <summary>Everything worked out at startup, handed to the main window.</summary>
/// <param name="Options">The parsed command line.</param>
/// <param name="Store">Where the config is loaded from and saved to.</param>
/// <param name="ConfigLoad">The result of loading <c>config.json</c>.</param>
/// <param name="SavedConfig">The config as stored in the file (no command-line overrides). Save changes on top of this.</param>
/// <param name="RunConfig">The config for this run: <paramref name="SavedConfig"/> with the command-line overrides applied. Never saved.</param>
/// <param name="VRChat">The photo folder (from <c>--folder</c>, VRChat's config, or the default).</param>
/// <param name="Logger">The app's log (<c>logs\photosync-YYYYMMDD.log</c> next to the config); null only in tests.</param>
public sealed record StartupContext(
    CommandLineOptions Options,
    ConfigStore Store,
    ConfigLoadResult ConfigLoad,
    AppConfig SavedConfig,
    AppConfig RunConfig,
    VRChatSettings VRChat,
    FileLogger? Logger = null)
{
    /// <summary>
    /// True when the Immich server window should open at startup: <c>--settings</c> was
    /// given, or no usable server URL or API key is set.
    /// </summary>
    public bool ShouldOpenSettings =>
        Options.ForceSettings
        || !ConfigStore.IsValidServerUrl(RunConfig.ServerUrl)
        || string.IsNullOrWhiteSpace(RunConfig.ApiKeyProtected);
}
