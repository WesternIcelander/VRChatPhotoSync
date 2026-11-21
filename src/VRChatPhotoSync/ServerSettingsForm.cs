using System.Diagnostics;

namespace VRChatPhotoSync;

/// <summary>
/// The Immich server window: server URL, API key (with Show), the help link and required permissions, Test connection,
/// album, the first-setup "Also upload my N existing photos" option, Advanced (visibility, VRCX wait, exact names), and
/// the read-only photo folder. All logic is in <see cref="ServerSettingsModel"/>; this form binds to it.
/// <para>
/// Save validates, tests the connection (asking "Save anyway?" if the test fails), then calls the host's save action,
/// which writes the config, restarts the client, resumes paused uploads and starts the backfill if it was ticked.
/// </para>
/// </summary>
public sealed class ServerSettingsForm : Form
{
    private const string AppTitle = "VRChat PhotoSync";
    private const string AdvancedCollapsed = "▸ Advanced (visibility, VRCX wait, exact names)";
    private const string AdvancedExpanded = "▾ Advanced";

    private readonly ServerSettingsModel _model;
    private readonly VRChatSettings _vrchat;
    private readonly Func<CancellationToken, Task<int>>? _countExisting;
    private readonly Action<ServerSettingsModel> _save;
    private readonly FileLogger? _logger;
    private readonly CancellationTokenSource _closing = new();

    private readonly ToolTip _toolTip = new();
    private readonly Label _urlLabel = new();
    private readonly TextBox _urlBox = new();
    private readonly Label _keyLabel = new();
    private readonly TextBox _keyBox = new();
    private readonly CheckBox _showKey = new();
    private readonly LinkLabel _helpLink = new();
    private readonly Label _permissionsLabel = new();
    private readonly Button _testButton = new();
    private readonly Label _testResult = new();
    private readonly Label _albumLabel = new();
    private readonly TextBox _albumBox = new();
    private readonly Label _albumHint = new();
    private readonly CheckBox _uploadExisting = new();
    private readonly LinkLabel _advancedLink = new();
    private readonly Panel _advancedPanel = new();
    private readonly Label _visibilityLabel = new();
    private readonly ComboBox _visibilityBox = new();
    private readonly Label _waitLabel = new();
    private readonly NumericUpDown _waitBox = new();
    private readonly Label _waitHint = new();
    private readonly CheckBox _strictNames = new();
    private readonly Label _folderLabel = new();
    private readonly LinkLabel _vrchatConfigLink = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();

    private bool _busy;
    private bool _advancedOpen;
    private int _testVersion;

    /// <param name="model">The settings and logic.</param>
    /// <param name="vrchat">The photo folder and where it came from (shown read-only).</param>
    /// <param name="save">Applies the saved settings (write config, restart the client, resume, start the backfill). May throw; the error is shown.</param>
    /// <param name="countExisting">Counts the existing photos for the first-setup option; null hides the option.</param>
    /// <param name="logger">The log, or null.</param>
    public ServerSettingsForm(
        ServerSettingsModel model,
        VRChatSettings vrchat,
        Action<ServerSettingsModel> save,
        Func<CancellationToken, Task<int>>? countExisting = null,
        FileLogger? logger = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _vrchat = vrchat ?? throw new ArgumentNullException(nameof(vrchat));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _countExisting = countExisting;
        _logger = logger;

        InitializeComponent();
        Icon = AppIcon.Icon;
        LoadFields();
        Relayout();
        Shown += (_, _) => StartCounting();
    }

    /// <summary>True when the existing-photos option is shown (first setup with a counter available).</summary>
    internal bool OffersUploadExisting => _model.IsFirstSetup && _countExisting is not null;

    /// <summary>The model (for tests).</summary>
    internal ServerSettingsModel Model => _model;

    private void LoadFields()
    {
        _urlBox.Text = _model.ServerUrl;
        _keyBox.Text = "";
        _keyBox.PlaceholderText = _model.ApiKeyPlaceholder;
        _albumBox.Text = _model.AlbumName;
        _visibilityBox.SelectedItem = _model.Visibility;
        _waitBox.Value = Math.Clamp(_model.VrcxWaitSeconds, 0, ServerSettingsModel.MaxVrcxWaitSeconds);
        _strictNames.Checked = _model.StrictFileNames;
        _uploadExisting.Checked = false;
        _uploadExisting.Visible = OffersUploadExisting;
        _uploadExisting.Text = ServerSettingsModel.UploadExistingText(null);
        _folderLabel.Text = "Photo folder: " + ServerSettingsModel.PhotoFolderText(_vrchat);
        _toolTip.SetToolTip(_folderLabel, $"{_vrchat.PictureFolder}\nSet in VRChat's own settings; PhotoSync only reads it.");
        _vrchatConfigLink.Enabled = ServerSettingsModel.VRChatConfigFolder(_vrchat) is { } dir && Directory.Exists(dir);
        UpdateHelpLink();
    }

    /// <summary>Copies the fields into the model.</summary>
    private void ReadFields()
    {
        _model.ServerUrl = _urlBox.Text;
        _model.ApiKey = _keyBox.Text;
        _model.AlbumName = _albumBox.Text;
        _model.Visibility = _visibilityBox.SelectedItem is AssetVisibility v ? v : AssetVisibility.Timeline;
        _model.VrcxWaitSeconds = (int)_waitBox.Value;
        _model.StrictFileNames = _strictNames.Checked;
        _model.UploadExisting = OffersUploadExisting && _uploadExisting.Checked;
    }

    private void UpdateHelpLink()
    {
        var url = ServerSettingsModel.GetApiKeyHelpUrl(_urlBox.Text);
        _toolTip.SetToolTip(_helpLink, $"{ServerSettingsModel.ApiKeyManualRoute}\n{url}");
    }

    private async void StartCounting()
    {
        if (!OffersUploadExisting)
            return;
        try
        {
            var count = await _countExisting!(_closing.Token);
            if (IsDisposed)
                return;
            _uploadExisting.Text = ServerSettingsModel.UploadExistingText(count);
            _uploadExisting.Enabled = count > 0;
            if (count == 0)
                _uploadExisting.Checked = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Could not count the existing photos: {ex.Message}");
            if (!IsDisposed)
                _uploadExisting.Text = "Also upload my existing photos";
        }
    }

    // ---- Test and save ----

    private async Task<ConnectionTestResult?> RunTestAsync()
    {
        ReadFields();
        var version = ++_testVersion;
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = "Testing…";
        try
        {
            var result = await _model.TestAsync(_closing.Token);
            if (IsDisposed || version != _testVersion)
                return result;
            _testResult.ForeColor = result.Ok ? Color.DarkGreen : Color.Firebrick;
            _testResult.Text = result.Text;
            _toolTip.SetToolTip(_testResult, result.Text);
            _logger?.Info($"Test connection: {result.Outcome}{(result.ServerVersion is { } v ? $", Immich {v}" : "")}.");
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            await RunTestAsync();
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false);
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_busy)
            return;
        ReadFields();
        var errors = _model.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await RunTestAsync();
            if (result is null || IsDisposed)
                return;
            if (!result.Ok)
            {
                var answer = MessageBox.Show(this,
                    $"{result.Text}\n\nSave anyway?", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                    return;
            }

            try
            {
                _save(_model);
            }
            catch (Exception ex)
            {
                _logger?.Error("Could not save the server settings.", ex);
                MessageBox.Show(this, $"The settings could not be saved:\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _testButton.Enabled = !busy;
        _saveButton.Enabled = !busy;
    }

    // ---- Links ----

    private void OnHelpClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
        OpenWithShell(ServerSettingsModel.GetApiKeyHelpUrl(_urlBox.Text));

    private void OnVRChatConfigClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        if (ServerSettingsModel.VRChatConfigFolder(_vrchat) is { } dir && Directory.Exists(dir))
            OpenWithShell(dir); // opens the folder only; the file belongs to VRChat
    }

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    private void OnAdvancedClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        _advancedOpen = !_advancedOpen;
        _advancedPanel.Visible = _advancedOpen;
        Relayout();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CancelPending();
        base.OnFormClosed(e);
    }

    // The form can be disposed before or while the dialog closes (e.g. taskkill sends WM_CLOSE to every window and
    // the main form exits first), and then Dispose runs again from the caller's using: cancel and dispose only once.
    private bool _closingDisposed;

    private void CancelPending()
    {
        if (!_closingDisposed)
            _closing.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closingDisposed)
        {
            _closing.Cancel();
            _closing.Dispose();
            _closingDisposed = true;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Layout ----

    private const int LeftX = 12, FieldX = 110, RightX = 468, FieldWidth = RightX - FieldX;

    /// <summary>Stacks the rows below the test result, depending on which optional rows are shown.</summary>
    private void Relayout()
    {
        SuspendLayout();
        var y = _albumBox.Bottom + 10;
        if (OffersUploadExisting)
        {
            _uploadExisting.SetBounds(LeftX, y, RightX - LeftX, 22);
            y += 28;
        }
        _advancedLink.SetBounds(LeftX, y, RightX - LeftX, 18);
        _advancedLink.Text = _advancedOpen ? AdvancedExpanded : AdvancedCollapsed;
        _advancedLink.LinkArea = new LinkArea(0, _advancedLink.Text.Length);
        y += 24;
        if (_advancedOpen)
        {
            _advancedPanel.SetBounds(LeftX, y, RightX - LeftX, 88);
            y += 94;
        }
        _folderLabel.SetBounds(LeftX, y, RightX - LeftX, 18);
        y += 20;
        _vrchatConfigLink.SetBounds(LeftX, y, 200, 18);
        y += 30;
        _saveButton.SetBounds(RightX - 170, y, 80, 28);
        _cancelButton.SetBounds(RightX - 80, y, 80, 28);
        ClientSize = new Size(RightX + LeftX, y + 40);
        ResumeLayout(false);
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Immich server";

        _urlLabel.SetBounds(LeftX, 15, 95, 18);
        _urlLabel.Text = "Server URL:";
        _urlBox.SetBounds(FieldX, 12, FieldWidth, 23);
        _urlBox.PlaceholderText = "https://immich.example.com";
        _urlBox.TextChanged += (_, _) => UpdateHelpLink();
        _toolTip.SetToolTip(_urlBox, "The address you open Immich at. /api is added if it's missing.");

        _keyLabel.SetBounds(LeftX, 45, 95, 18);
        _keyLabel.Text = "API key:";
        _keyBox.SetBounds(FieldX, 42, FieldWidth - 70, 23);
        _keyBox.UseSystemPasswordChar = true;
        _showKey.SetBounds(RightX - 64, 41, 64, 25);
        _showKey.Appearance = Appearance.Button;
        _showKey.TextAlign = ContentAlignment.MiddleCenter;
        _showKey.Text = "Show";
        _showKey.CheckedChanged += (_, _) => _keyBox.UseSystemPasswordChar = !_showKey.Checked;
        _toolTip.SetToolTip(_showKey, "Show the key you are typing. A saved key is never shown.");

        _helpLink.SetBounds(FieldX, 70, FieldWidth, 18);
        _helpLink.Text = "How do I get an API key?";
        _helpLink.LinkClicked += OnHelpClicked;

        _permissionsLabel.SetBounds(FieldX, 90, FieldWidth, 62);
        _permissionsLabel.Text = ServerSettingsModel.RequiredPermissionsText;
        _permissionsLabel.ForeColor = SystemColors.GrayText;

        _testButton.SetBounds(FieldX, 156, 120, 28);
        _testButton.Text = "Test connection";
        _testButton.Click += OnTestClicked;
        _testResult.SetBounds(FieldX, 190, FieldWidth, 64);
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.AutoEllipsis = true;

        _albumLabel.SetBounds(LeftX, 265, 95, 18);
        _albumLabel.Text = "Album:";
        _albumBox.SetBounds(FieldX, 262, 240, 23);
        _albumBox.PlaceholderText = "VRChat";
        _albumHint.SetBounds(FieldX + 248, 265, FieldWidth - 248, 18);
        _albumHint.Text = "(optional)";
        _albumHint.ForeColor = SystemColors.GrayText;
        _toolTip.SetToolTip(_albumBox, "Uploaded photos are added to this album, which is created if needed. Leave blank for no album.");

        _uploadExisting.AutoEllipsis = true;
        _toolTip.SetToolTip(_uploadExisting,
            "Upload the photos already in the camera roll that the server doesn't have yet. Only offered on first setup; " +
            "later use ⋯ → Upload existing photos….");

        _advancedLink.LinkClicked += OnAdvancedClicked;
        _advancedPanel.Visible = false;

        _visibilityLabel.SetBounds(0, 5, 95, 18);
        _visibilityLabel.Text = "Visibility:";
        _visibilityBox.SetBounds(FieldX - LeftX, 2, 140, 23);
        _visibilityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _visibilityBox.Items.AddRange([AssetVisibility.Timeline, AssetVisibility.Archive, AssetVisibility.Hidden, AssetVisibility.Locked]);
        _toolTip.SetToolTip(_visibilityBox, "Where uploaded photos appear in Immich. Timeline is the normal choice.");
        _waitLabel.SetBounds(0, 35, 95, 18);
        _waitLabel.Text = "VRCX wait:";
        _waitBox.SetBounds(FieldX - LeftX, 32, 60, 23);
        _waitBox.Minimum = 0;
        _waitBox.Maximum = ServerSettingsModel.MaxVrcxWaitSeconds;
        _waitHint.SetBounds(FieldX - LeftX + 66, 35, 200, 18);
        _waitHint.Text = "seconds (0 = don't wait)";
        _waitHint.ForeColor = SystemColors.GrayText;
        _toolTip.SetToolTip(_waitBox, "How long to wait for VRCX to add its metadata to a new photo before uploading it.");
        _strictNames.SetBounds(0, 62, RightX - LeftX, 22);
        _strictNames.Text = "Only exact VRChat file names (skip copies like \"… - Copy.png\")";
        _advancedPanel.Controls.AddRange([_visibilityLabel, _visibilityBox, _waitLabel, _waitBox, _waitHint, _strictNames]);

        _folderLabel.AutoEllipsis = true;
        _vrchatConfigLink.Text = "Open VRChat config folder";
        _vrchatConfigLink.LinkClicked += OnVRChatConfigClicked;
        _toolTip.SetToolTip(_vrchatConfigLink, "The photo folder is set in VRChat. This opens the folder with VRChat's config.json.");

        _saveButton.Text = "Save";
        _saveButton.Click += OnSaveClicked;
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        Controls.AddRange([_urlLabel, _urlBox, _keyLabel, _keyBox, _showKey, _helpLink, _permissionsLabel, _testButton, _testResult,
            _albumLabel, _albumBox, _albumHint, _uploadExisting, _advancedLink, _advancedPanel, _folderLabel, _vrchatConfigLink,
            _saveButton, _cancelButton]);
        ResumeLayout(false);
    }
}
