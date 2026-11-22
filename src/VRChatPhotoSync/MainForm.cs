using System.Diagnostics;

namespace VRChatPhotoSync;

/// <summary>
/// The small main window: status line, session counters, last file, queue length, the Skip prints and Upload photo
/// layers checkboxes, and the Immich server / Open folder / ⋯ buttons. All presentation logic is in
/// <see cref="MainViewModel"/>; this form only feeds it and shows its <see cref="MainViewModel.Snapshot"/>. Every update
/// from another thread goes through <see cref="Control.BeginInvoke(Delegate)"/>.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ToolTip _toolTip = new();
    private readonly Label _statusLabel = new();
    private readonly Label _countersLabel = new();
    private readonly Label _lastLabel = new();
    private readonly Label _queueLabel = new();
    private readonly CheckBox _skipPrintsCheck = new();
    private readonly CheckBox _uploadLayersCheck = new();
    private readonly Button _serverButton = new();
    private readonly Button _folderButton = new();
    private readonly Button _moreButton = new();
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _uploadExistingItem = new("Upload existing photos…");
    private readonly ToolStripMenuItem _behaviourItem = new("Window behaviour");
    private readonly ToolStripMenuItem _closeExitItem = new("Close button exits");
    private readonly ToolStripMenuItem _closeTrayItem = new("Close button hides to tray");
    private readonly ToolStripMenuItem _minimizeTaskbarItem = new("Minimize to taskbar");
    private readonly ToolStripMenuItem _minimizeTrayItem = new("Minimize to tray");
    private readonly ToolStripMenuItem _startMinimizedItem = new("Start minimized");
    private readonly ToolStripMenuItem _openLogItem = new("Open log");
    private readonly ToolStripMenuItem _openImmichItem = new("Open Immich");
    private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 1000 };
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _trayShowItem = new("Show");
    private readonly ToolStripMenuItem _trayExitItem = new("Exit");

    internal const int WM_CLOSE = 0x0010;
    internal const int WM_SYSCOMMAND = 0x0112;
    internal const int SC_MINIMIZE = 0xF020;
    internal const int SC_CLOSE = 0xF060;

    /// <summary>The longest tooltip a <see cref="NotifyIcon"/> accepts.</summary>
    internal const int MaxTrayTextLength = 127;

    /// <summary>True until the first show when the app starts minimized to the tray.</summary>
    private bool _startInTray;
    /// <summary>The window's last position in the normal (not minimized) state.</summary>
    private Point _normalLocation;
    private bool _hasNormalLocation;

    private int _renderPending;
    private bool _rendering;
    private CatchUpScanner? _scanner;
    private UploadWorker? _worker;
    private ImmichClientHost? _clientHost;
    private Action? _openServerSettings;

    /// <summary>A window with default settings (for the designer and tests).</summary>
    public MainForm()
        : this(new LiveConfig(new AppConfig(), new AppConfig(), _ => { }), new MainViewModel(new AppConfig()), null, null)
    {
    }

    /// <summary>The app's main window for the startup settings.</summary>
    public MainForm(StartupContext startup)
        : this(
            new LiveConfig(startup.SavedConfig, startup.RunConfig, startup.Store.Save),
            new MainViewModel(startup.RunConfig, startup.VRChat.PictureFolder),
            startup.VRChat.PictureFolder,
            startup.Logger)
    {
        Startup = startup;
        OpenServerSettings = ShowServerSettings;
        ViewModel.SetFolderState(Directory.Exists(startup.VRChat.PictureFolder) ? FolderWatchState.Watching : FolderWatchState.WaitingForFolder);
    }

    /// <param name="config">The live settings; the checkboxes and window-behaviour items change and save them.</param>
    /// <param name="viewModel">What the window shows.</param>
    /// <param name="pictureFolder">The camera roll root (for Open folder).</param>
    /// <param name="logger">The log (for Open log and for reporting errors), or null.</param>
    public MainForm(LiveConfig config, MainViewModel viewModel, string? pictureFolder, FileLogger? logger)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        PictureFolder = pictureFolder;
        Logger = logger;

        InitializeComponent();
        Icon = AppIcon.Icon;
        _trayIcon.Icon = AppIcon.Icon;
        _trayIcon.Visible = true; // always present, so both "to tray" options work
        ApplyStartupWindowState(Config.Current);

        ViewModel.Changed += RequestRender;
        Config.Changed += ViewModel.ApplyConfig;
        _tickTimer.Tick += (_, _) => ViewModel.Tick();
        HandleCreated += (_, _) => Render();
        Render();
    }

    /// <summary>The startup settings; null when built without them (tests).</summary>
    internal StartupContext? Startup { get; }

    /// <summary>The live settings.</summary>
    public LiveConfig Config { get; }

    /// <summary>The presentation logic.</summary>
    public MainViewModel ViewModel { get; }

    /// <summary>The camera roll root (for Open folder).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? PictureFolder { get; set; }

    private FileLogger? Logger { get; }

    /// <summary>Opens the Immich server window; the button is disabled while this is null.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OpenServerSettings
    {
        get => _openServerSettings;
        set
        {
            _openServerSettings = value;
            _serverButton.Enabled = value is not null;
        }
    }

    // ---- Wiring to the pipeline (all events may arrive on other threads; the view model is thread-safe) ----

    /// <summary>Shows the worker's status and outcomes.</summary>
    public void Attach(UploadWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
        worker.StatusChanged += ViewModel.SetWorkerStatus;
        worker.Uploaded += ViewModel.Record;
        worker.Duplicate += ViewModel.Record;
        worker.Skipped += ViewModel.Record;
        worker.Failed += ViewModel.Record;
        worker.Updated += ViewModel.Record;
        ViewModel.SetWorkerStatus(worker.Status);
    }

    /// <summary>Shows the queue length.</summary>
    public void Attach(UploadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        queue.CountChanged += ViewModel.SetQueueLength;
        ViewModel.SetQueueLength(queue.Count);
    }

    /// <summary>Shows "waiting for folder" and follows renames of the last file.</summary>
    public void Attach(FolderWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        watcher.StateChanged += (_, _) => ViewModel.SetFolderState(watcher.State, watcher.Root);
        watcher.FileRenamed += ViewModel.NotifyRenamed;
        PictureFolder = watcher.Root;
        ViewModel.SetFolderState(watcher.State, watcher.Root);
    }

    /// <summary>Enables "Upload existing photos…".</summary>
    public void Attach(CatchUpScanner scanner)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _uploadExistingItem.Enabled = true;
    }

    /// <summary>The Immich client holder, restarted when the server settings are saved.</summary>
    public void Attach(ImmichClientHost clientHost)
    {
        _clientHost = clientHost ?? throw new ArgumentNullException(nameof(clientHost));
    }

    // ---- Immich server window ----

    /// <summary>Opens the Immich server window (modal).</summary>
    public void ShowServerSettings()
    {
        var vrchat = Startup?.VRChat
                     ?? new VRChatSettings(PictureFolder ?? "", true, VRChatSettings.SourceDefault, null, []);
        var scanner = _scanner;
        Func<CancellationToken, Task<int>>? count = scanner is null ? null : ct => scanner.CountExistingAsync(ct);
        using var form = new ServerSettingsForm(new ServerSettingsModel(Config.Current), vrchat, ApplyServerSettings, count, Logger);
        if (Visible)
            form.ShowDialog(this);
        else
            form.ShowDialog();
    }

    /// <summary>
    /// Save in the Immich server window: writes the config, restarts the client, resumes uploads paused by a rejected
    /// key (or cuts a retry wait short), and starts the backfill if "Also upload my existing photos" was ticked. If the
    /// file can't be written, the settings still apply to this run and the error is passed on for the window to show.
    /// </summary>
    internal void ApplyServerSettings(ServerSettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Exception? saveError = null;
        try
        {
            Config.Update(model.ApplyTo);
        }
        catch (Exception ex)
        {
            saveError = ex;
        }

        var config = Config.Current;
        Logger?.AddSecret(SecretProtector.Unprotect(config.ApiKeyProtected));
        Logger?.Info($"Server settings saved: {config.ServerUrl}; API key {(model.KeyChanged ? "changed" : "kept")}; " +
                     $"album {(config.HasAlbum ? $"\"{config.AlbumName}\"" : "none")}; visibility {config.Visibility.ToApiValue()}; " +
                     $"VRCX wait {config.VrcxWaitSeconds} s; exact names {(config.StrictFileNames ? "on" : "off")}.");
        var hasClient = _clientHost?.Restart(config);
        if (hasClient == false)
            Logger?.Warn("No Immich client: the server URL or API key isn't usable.");
        _worker?.Resume();

        if (model.UploadExisting && _scanner is not null)
        {
            Logger?.Info("Uploading existing photos (chosen on first setup).");
            if (IsHandleCreated)
                BeginInvoke(() => _ = RunBackfillAsync(showResult: false));
            else
                _ = RunBackfillAsync(showResult: false);
        }

        if (saveError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(saveError);
    }

    // ---- Tray and window behaviour ----

    /// <summary>
    /// Places the window at its saved position (if that is still on a screen) and, when starting minimized
    /// (<c>--minimized</c>, <see cref="AppConfig.StartMinimized"/>, or still minimized when the last run was killed),
    /// minimizes it to the taskbar or the tray per <see cref="AppConfig.MinimizeButtonAction"/>.
    /// </summary>
    private void ApplyStartupWindowState(AppConfig config)
    {
        if (config.WindowLeft is int left && config.WindowTop is int top && IsOnScreen(new Rectangle(left, top, Width, Height)))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(left, top);
            _normalLocation = Location;
            _hasNormalLocation = true;
        }

        if (config.StartMinimized || config.WindowMinimized)
        {
            if (config.MinimizeButtonAction == MinimizeButtonAction.Tray)
                _startInTray = true;
            else
                WindowState = FormWindowState.Minimized;
        }
    }

    /// <summary>True when enough of the window's title bar would be on a screen's working area to drag it.</summary>
    internal static bool IsOnScreen(Rectangle bounds)
    {
        var titleBar = new Rectangle(bounds.Left, bounds.Top, Math.Max(bounds.Width, 1), 24);
        return Screen.AllScreens.Any(s =>
        {
            var visible = Rectangle.Intersect(s.WorkingArea, titleBar);
            return visible.Width >= Math.Min(60, titleBar.Width) && visible.Height >= 12;
        });
    }

    /// <summary>The tray icon's tooltip: the title (with the count) and the status line, cut to what Windows allows.</summary>
    internal static string TrayText(MainView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var text = $"{view.Title}\n{view.StatusText}";
        return text.Length <= MaxTrayTextLength ? text : text[..(MaxTrayTextLength - 1)] + "…";
    }

    /// <summary>The tray icon (for tests).</summary>
    internal NotifyIcon TrayIcon => _trayIcon;

    /// <summary>True while the window is hidden in the tray (not just minimized to the taskbar).</summary>
    public bool IsInTray => IsHandleCreated && !Visible && !IsDisposed;

    /// <summary>Shows the window again from the tray or the taskbar and brings it to the front.</summary>
    public void RestoreWindow()
    {
        if (IsDisposed)
            return;
        _startInTray = false;
        if (!Visible)
            Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        SaveWindowConfig(c => c with { WindowMinimized = false });
    }

    /// <summary>Hides the window to the tray (the app keeps running).</summary>
    public void HideToTray()
    {
        if (IsDisposed)
            return;
        RememberLocation();
        Hide();
        SaveWindowConfig(c => WithLocation(c) with { WindowMinimized = true });
    }

    /// <summary>
    /// Only the title-bar X / Alt+F4 (<c>WM_SYSCOMMAND</c> + <c>SC_CLOSE</c>) follows
    /// <see cref="AppConfig.CloseButtonAction"/>, and the minimize button (<c>SC_MINIMIZE</c>) follows
    /// <see cref="AppConfig.MinimizeButtonAction"/>. A plain <c>WM_CLOSE</c> (taskkill, the tray's Exit) and the end of
    /// the Windows session always exit, since nothing cancels <see cref="Form.FormClosing"/>.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND)
        {
            var command = (int)(m.WParam.ToInt64() & 0xFFF0);
            var config = Config.Current;
            if ((command == SC_CLOSE && config.CloseButtonAction == CloseButtonAction.HideToTray)
                || (command == SC_MINIMIZE && config.MinimizeButtonAction == MinimizeButtonAction.Tray))
            {
                HideToTray();
                m.Result = IntPtr.Zero;
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Starting minimized to the tray: create the window (so updates and messages work) but don't show it.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && _startInTray)
        {
            _startInTray = false;
            if (!IsHandleCreated)
                CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        RememberLocation();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e); // also raised at the end of a drag
        RememberLocation();
        SaveWindowConfig(WithLocation);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated || !Visible)
            return;
        var minimized = WindowState == FormWindowState.Minimized;
        SaveWindowConfig(c => (minimized ? c : WithLocation(c)) with { WindowMinimized = minimized });
    }

    /// <summary>A normal exit: save the position and clear the minimized flag, so the next normal start follows the settings.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;
        RememberLocation();
        SaveWindowConfig(c => WithLocation(c) with { WindowMinimized = false });
    }

    private void RememberLocation()
    {
        if (!IsHandleCreated || !Visible || WindowState != FormWindowState.Normal)
            return;
        _normalLocation = Location;
        _hasNormalLocation = true;
    }

    private AppConfig WithLocation(AppConfig config) =>
        _hasNormalLocation ? config with { WindowLeft = _normalLocation.X, WindowTop = _normalLocation.Y } : config;

    /// <summary>Saves a window-state change, only when it changes anything. A failed save is logged, not shown.</summary>
    private void SaveWindowConfig(Func<AppConfig, AppConfig> change)
    {
        var current = Config.Current;
        if (change(current) == current)
            return;
        try
        {
            Config.Update(change);
        }
        catch (Exception ex)
        {
            Logger?.Warn($"Could not save the window state: {ex.Message}");
        }
    }

    // ---- Rendering ----

    /// <summary>Queues one render on the UI thread; several changes in a row are shown together.</summary>
    private void RequestRender()
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
            return; // HandleCreated renders
        if (Interlocked.Exchange(ref _renderPending, 1) == 1)
            return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _renderPending, 0);
                Render();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _renderPending, 0); // the window is closing
        }
    }

    private void Render()
    {
        if (IsDisposed)
            return;
        var view = ViewModel.Snapshot();
        _rendering = true;
        try
        {
            Text = view.Title;
            _statusLabel.Text = view.StatusText;
            _toolTip.SetToolTip(_statusLabel, view.StatusToolTip ?? view.StatusText);
            _countersLabel.Text = view.CountersText;
            _lastLabel.Text = view.LastText;
            _lastLabel.Cursor = view.LastFilePath is null ? Cursors.Default : Cursors.Hand;
            _toolTip.SetToolTip(_lastLabel, view.LastFilePath is null ? null : $"{view.LastFilePath}\nClick to show it in Explorer.");
            _queueLabel.Text = view.QueueText ?? "";
            _queueLabel.Visible = view.QueueText is not null;
            _skipPrintsCheck.Text = view.SkipPrintsText;
            _skipPrintsCheck.Checked = view.ExcludePrints;
            _uploadLayersCheck.Text = view.UploadLayersText;
            _uploadLayersCheck.Checked = view.UploadLayers;
            _tickTimer.Enabled = ViewModel.NeedsTick;
            _trayIcon.Text = TrayText(view);
        }
        finally
        {
            _rendering = false;
        }
    }

    // ---- Actions ----

    private void ChangeConfig(Func<AppConfig, AppConfig> change)
    {
        try
        {
            Config.Update(change);
        }
        catch (Exception ex)
        {
            Logger?.Error("Could not save the settings.", ex);
            MessageBox.Show(this, $"The setting applies now but could not be saved:\n{ex.Message}", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private const string AppTitle = "VRChat PhotoSync";

    private void OnSkipPrintsChanged(object? sender, EventArgs e)
    {
        if (_rendering)
            return;
        var value = _skipPrintsCheck.Checked;
        ChangeConfig(c => c with { ExcludePrints = value });
        Logger?.Info($"Skip prints {(value ? "on" : "off")}.");
    }

    private void OnUploadLayersChanged(object? sender, EventArgs e)
    {
        if (_rendering)
            return;
        var value = _uploadLayersCheck.Checked;
        ChangeConfig(c => c with { UploadLayers = value });
        Logger?.Info($"Upload photo layers {(value ? "on" : "off")}.");
    }

    private void OnLastClicked(object? sender, EventArgs e)
    {
        var path = ViewModel.Snapshot().LastFilePath;
        if (path is null)
            return;
        if (File.Exists(path))
            StartProcess("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            StartProcess("explorer.exe", $"\"{dir}\"");
    }

    private void OnOpenFolder(object? sender, EventArgs e)
    {
        var folder = PictureFolder;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            StartProcess("explorer.exe", $"\"{folder}\"");
        else
            MessageBox.Show(this, $"The photo folder doesn't exist yet:\n{folder}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnOpenLog(object? sender, EventArgs e)
    {
        if (Logger is null)
            return;
        if (File.Exists(Logger.CurrentPath))
            OpenWithShell(Logger.CurrentPath);
        else if (Directory.Exists(Logger.Directory))
            StartProcess("explorer.exe", $"\"{Logger.Directory}\"");
    }

    private void OnOpenImmich(object? sender, EventArgs e)
    {
        if (MainViewModel.ServerRoot(Config.Current.ServerUrl) is { } url)
            OpenWithShell(url);
    }

    private async void OnUploadExisting(object? sender, EventArgs e)
    {
        if (_scanner is null)
            return;
        var answer = MessageBox.Show(this,
            "Upload every existing photo in the camera roll that the server doesn't have yet?\n\n" +
            "Prints and layers follow the checkboxes. Photos the server already has are skipped.",
            AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK)
            return;
        await RunBackfillAsync(showResult: true);
    }

    /// <summary>Runs "Upload existing photos" with progress in the status line. Failures are always shown.</summary>
    private async Task RunBackfillAsync(bool showResult)
    {
        if (_scanner is null || IsDisposed)
            return;
        _uploadExistingItem.Enabled = false;
        try
        {
            var progress = new Progress<BackfillProgress>(ViewModel.SetBackfill);
            var result = await _scanner.BackfillAsync(progress);
            ViewModel.SetBackfill(null);
            if (!showResult && result.Status == BackfillStatus.Ok)
                return;
            var text = result.Status switch
            {
                BackfillStatus.NotSetUp => "Set up the Immich server first.",
                BackfillStatus.CheckFailed => $"Could not check with the server: {result.Message}",
                _ => $"{result.Enqueued} photos queued for upload; {result.AlreadyPresent} already on the server.",
            };
            MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK,
                result.Status == BackfillStatus.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.SetBackfill(null);
            Logger?.Error("Upload existing photos failed.", ex);
            MessageBox.Show(this, $"Upload existing photos failed:\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _uploadExistingItem.Enabled = true;
        }
    }

    private void OnMoreMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var config = Config.Current;
        _closeExitItem.Checked = config.CloseButtonAction == CloseButtonAction.Exit;
        _closeTrayItem.Checked = config.CloseButtonAction == CloseButtonAction.HideToTray;
        _minimizeTaskbarItem.Checked = config.MinimizeButtonAction == MinimizeButtonAction.Taskbar;
        _minimizeTrayItem.Checked = config.MinimizeButtonAction == MinimizeButtonAction.Tray;
        _startMinimizedItem.Checked = config.StartMinimized;
        _openImmichItem.Enabled = MainViewModel.ServerRoot(config.ServerUrl) is not null;
        _openLogItem.Enabled = Logger is not null;
    }

    private void StartProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger?.Warn($"Could not start {fileName}: {ex.Message}");
        }
    }

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger?.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.Changed -= RequestRender;
            Config.Changed -= ViewModel.ApplyConfig;
            _tickTimer.Dispose();
            _toolTip.Dispose();
            _moreMenu.Dispose();
            _trayIcon.Visible = false; // remove it from the tray straight away
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Layout ----

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(380, 164);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = MainViewModel.AppTitle;

        const int left = 10, width = 360;

        _statusLabel.SetBounds(left, 8, width, 18);
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Font = new Font(Font, FontStyle.Bold);

        _countersLabel.SetBounds(left, 28, width - 92, 18);
        _countersLabel.AutoEllipsis = true;

        _lastLabel.SetBounds(left, 48, width, 18);
        _lastLabel.AutoEllipsis = true;
        _lastLabel.Click += OnLastClicked;

        _queueLabel.SetBounds(left + width - 88, 28, 88, 18); // right end of the counters line, so "Last" can use the full width
        _queueLabel.TextAlign = ContentAlignment.TopRight;
        _queueLabel.AutoEllipsis = true;
        _queueLabel.Visible = false;

        _skipPrintsCheck.SetBounds(left, 72, width, 22);
        _skipPrintsCheck.AutoEllipsis = true;
        _skipPrintsCheck.CheckedChanged += OnSkipPrintsChanged;
        _toolTip.SetToolTip(_skipPrintsCheck, "Don't upload VRChat prints (2048×1440 polaroids). Applies to new photos.");

        _uploadLayersCheck.SetBounds(left, 94, width, 22);
        _uploadLayersCheck.AutoEllipsis = true;
        _uploadLayersCheck.CheckedChanged += OnUploadLayersChanged;
        _toolTip.SetToolTip(_uploadLayersCheck, "Also upload the _Environment, _Player and _UI files of multi-layer photos.");

        _serverButton.SetBounds(left, 126, 130, 28);
        _serverButton.Text = "Immich server…";
        _serverButton.Enabled = false;
        _serverButton.Click += (_, _) => OpenServerSettings?.Invoke();

        _folderButton.SetBounds(146, 126, 110, 28);
        _folderButton.Text = "Open folder";
        _folderButton.Click += OnOpenFolder;

        _moreButton.SetBounds(330, 126, 40, 28);
        _moreButton.Text = "⋯";
        _moreButton.Click += (_, _) => _moreMenu.Show(_moreButton, new Point(0, _moreButton.Height));
        _toolTip.SetToolTip(_moreButton, "More");

        _uploadExistingItem.Enabled = false;
        _uploadExistingItem.Click += OnUploadExisting;
        _closeExitItem.Click += (_, _) => ChangeConfig(c => c with { CloseButtonAction = CloseButtonAction.Exit });
        _closeTrayItem.Click += (_, _) => ChangeConfig(c => c with { CloseButtonAction = CloseButtonAction.HideToTray });
        _minimizeTaskbarItem.Click += (_, _) => ChangeConfig(c => c with { MinimizeButtonAction = MinimizeButtonAction.Taskbar });
        _minimizeTrayItem.Click += (_, _) => ChangeConfig(c => c with { MinimizeButtonAction = MinimizeButtonAction.Tray });
        _startMinimizedItem.Click += (_, _) =>
        {
            var value = !Config.Saved.StartMinimized; // the saved value, not one forced by --minimized
            ChangeConfig(c => c with { StartMinimized = value });
        };
        _behaviourItem.DropDownItems.AddRange(
        [
            _closeExitItem, _closeTrayItem, new ToolStripSeparator(),
            _minimizeTaskbarItem, _minimizeTrayItem, new ToolStripSeparator(),
            _startMinimizedItem,
        ]);
        _openLogItem.Click += OnOpenLog;
        _openImmichItem.Click += OnOpenImmich;
        _moreMenu.Items.AddRange([_uploadExistingItem, _behaviourItem, new ToolStripSeparator(), _openLogItem, _openImmichItem]);
        _moreMenu.Opening += OnMoreMenuOpening;

        _trayShowItem.Font = new Font(_trayMenu.Font, FontStyle.Bold); // the double-click action
        _trayShowItem.Click += (_, _) => RestoreWindow();
        _trayExitItem.Click += (_, _) => Close();
        _trayMenu.Items.AddRange([_trayShowItem, new ToolStripSeparator(), _trayExitItem]);
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Text = MainViewModel.AppTitle;
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        Controls.AddRange([_statusLabel, _countersLabel, _lastLabel, _queueLabel, _skipPrintsCheck, _uploadLayersCheck,
            _serverButton, _folderButton, _moreButton]);
        ResumeLayout(false);
    }
}
