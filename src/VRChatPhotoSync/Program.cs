using System.Reflection;

namespace VRChatPhotoSync;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        var options = CommandLineOptions.Parse(args);

        var store = new ConfigStore();
        using var logger = new FileLogger(FileLogger.ForConfigDirectory(store.Directory));

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

        var stopReason = "exit";
        Application.ThreadException += (_, e) => logger.Error("Unhandled UI exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            stopReason = "crash";
            logger.Error("Unhandled exception.", e.ExceptionObject as Exception);
        };

        try
        {
            Application.Run(new MainForm(new StartupContext(options, store, loaded, savedConfig, runConfig, vrchat, logger)));
        }
        finally
        {
            logger.LogStop(stopReason, vrchat);
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
