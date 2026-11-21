namespace VRChatPhotoSync;

/// <summary>The two calls "Test connection" makes. <see cref="ImmichClient"/> is the real one; tests use a fake.</summary>
public interface IServerInfoClient : IDisposable
{
    /// <summary><c>GET server/version</c> (no permission needed).</summary>
    Task<ApiResult<Version>> GetServerVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary><c>GET api-keys/me</c>: the key's name and permissions.</summary>
    Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>What a connection test found.</summary>
public enum ConnectionTestOutcome
{
    /// <summary>Immich answered and the key can upload.</summary>
    Success,
    /// <summary>The URL or key in the window isn't usable, so nothing was sent.</summary>
    InvalidInput,
    /// <summary>No answer: wrong address, DNS, offline, firewall or timeout.</summary>
    Unreachable,
    /// <summary>The server answered with a 5xx/408/429, or an unexpected answer to the key check.</summary>
    ServerError,
    /// <summary>Something answered, but not like Immich (wrong path, another website, a login page).</summary>
    NotImmich,
    /// <summary>Immich answered 401/403 to the key.</summary>
    KeyRejected,
    /// <summary>The key is valid but lacks <c>asset.upload</c>.</summary>
    MissingUploadPermission,
}

/// <summary>Result of <see cref="ServerSettingsModel.TestAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Summary">One line for the window, e.g. <c>✔ Connected — Immich 3.4.2, key "PhotoSync" can upload</c>.</param>
/// <param name="Notes">Extra lines: album permissions, the optional copy/delete permissions.</param>
/// <param name="ServerVersion">The Immich version, when it was read.</param>
/// <param name="KeyName">The key's name, when it was read.</param>
public sealed record ConnectionTestResult(
    ConnectionTestOutcome Outcome, string Summary, IReadOnlyList<string> Notes, Version? ServerVersion = null, string? KeyName = null)
{
    /// <summary>True when uploads will work.</summary>
    public bool Ok => Outcome == ConnectionTestOutcome.Success;

    /// <summary>The summary and the notes, one per line.</summary>
    public string Text => Notes.Count == 0 ? Summary : Summary + Environment.NewLine + string.Join(Environment.NewLine, Notes);
}

/// <summary>
/// The logic behind the Immich server window (<see cref="ServerSettingsForm"/>): the field values, validation, the
/// connection test and the resulting config. The form only binds to it.
/// <para>
/// The API key box starts empty even when a key is saved: a saved key is never shown again, and leaving the box blank
/// keeps it. A typed key is trimmed. The "Also upload my N existing photos" option is offered only while no key is saved
/// (first setup).
/// </para>
/// </summary>
public sealed class ServerSettingsModel
{
    /// <summary>Largest allowed VRCX wait.</summary>
    public const int MaxVrcxWaitSeconds = 300;

    /// <summary>Immich's documentation on API keys, for when no server URL has been entered yet.</summary>
    public const string ApiKeyDocsUrl = "https://docs.immich.app/features/command-line-interface#obtain-the-api-key";

    /// <summary>The page in Immich's web UI where keys are created, relative to the server root.</summary>
    public const string ApiKeyPagePath = "user-settings?isOpen=api-keys";

    /// <summary>How to get to the API key page by hand (tooltip of the help link).</summary>
    public const string ApiKeyManualRoute =
        "In Immich: click your profile picture (top right) → Account Settings → API Keys → New API Key.";

    /// <summary>The permissions the key needs, shown under the help link.</summary>
    public static readonly string RequiredPermissionsText =
        $"Required permission: {ApiKeyInfo.PermissionAssetUpload}. " +
        $"For an album also {string.Join(", ", ApiKeyInfo.AlbumPermissions)}. " +
        $"Optional: {ApiKeyInfo.PermissionAssetCopy}, {ApiKeyInfo.PermissionAssetDelete} (to replace photos VRCX edits late).";

    /// <summary>Time limit for each of the test's requests.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly AppConfig _original;
    private readonly Func<string, string, IServerInfoClient> _connect;
    private readonly Func<string?, string?> _unprotect;
    private readonly Func<string, string> _protect;

    /// <param name="config">The settings the window starts from (normally <see cref="LiveConfig.Current"/>).</param>
    /// <param name="connect">Creates a client for (server URL, API key); normally a new <see cref="ImmichClient"/>.</param>
    /// <param name="unprotect">Decrypts the saved key; defaults to <see cref="SecretProtector.Unprotect"/>.</param>
    /// <param name="protect">Encrypts a new key; defaults to <see cref="SecretProtector.Protect"/>.</param>
    public ServerSettingsModel(
        AppConfig config,
        Func<string, string, IServerInfoClient>? connect = null,
        Func<string?, string?>? unprotect = null,
        Func<string, string>? protect = null)
    {
        _original = config ?? throw new ArgumentNullException(nameof(config));
        _connect = connect ?? ((url, key) => new ImmichClient(url, key));
        _unprotect = unprotect ?? SecretProtector.Unprotect;
        _protect = protect ?? SecretProtector.Protect;

        ServerUrl = config.ServerUrl ?? "";
        AlbumName = config.AlbumName ?? "";
        Visibility = config.Visibility;
        VrcxWaitSeconds = config.VrcxWaitSeconds;
        StrictFileNames = config.StrictFileNames;
    }

    // ---- Fields ----

    /// <summary>The server URL as typed (with or without <c>/api</c>).</summary>
    public string ServerUrl { get; set; }

    /// <summary>A newly typed API key; blank keeps the saved one. Never pre-filled.</summary>
    public string ApiKey { get; set; } = "";

    public string AlbumName { get; set; }
    public AssetVisibility Visibility { get; set; }
    public int VrcxWaitSeconds { get; set; }
    public bool StrictFileNames { get; set; }

    /// <summary>"Also upload my N existing photos" (first setup only; unticked by default).</summary>
    public bool UploadExisting { get; set; }

    /// <summary>True when a key is already saved.</summary>
    public bool HasSavedKey => !string.IsNullOrWhiteSpace(_original.ApiKeyProtected);

    /// <summary>True while no key has been saved yet, so the existing-photos option is offered.</summary>
    public bool IsFirstSetup => !HasSavedKey;

    /// <summary>Placeholder for the key box.</summary>
    public string ApiKeyPlaceholder => HasSavedKey ? "Saved (leave blank to keep it)" : "Paste the API key";

    /// <summary>True when the key box holds a new key.</summary>
    public bool KeyChanged => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The URL the help link opens: the server's API key page, or Immich's docs while the URL isn't valid.</summary>
    public string ApiKeyHelpUrl => GetApiKeyHelpUrl(ServerUrl);

    /// <summary>
    /// The page in Immich's web UI where keys are created (<c>&lt;server root&gt;/user-settings?isOpen=api-keys</c>), or
    /// <see cref="ApiKeyDocsUrl"/> when <paramref name="serverUrl"/> isn't a valid URL.
    /// </summary>
    public static string GetApiKeyHelpUrl(string? serverUrl) =>
        MainViewModel.ServerRoot(serverUrl) is { } root ? root + ApiKeyPagePath : ApiKeyDocsUrl;

    /// <summary>The text of the existing-photos checkbox; <paramref name="count"/> is null while counting.</summary>
    public static string UploadExistingText(int? count) => count switch
    {
        null => "Also upload my existing photos (counting…)",
        1 => "Also upload my 1 existing photo",
        _ => $"Also upload my {count.Value:N0} existing photos",
    };

    /// <summary>The photo folder line, e.g. <c>D:\Photos\VRChat (from VRChat)</c>.</summary>
    public static string PhotoFolderText(VRChatSettings vrchat)
    {
        ArgumentNullException.ThrowIfNull(vrchat);
        var source = vrchat.Source switch
        {
            VRChatSettings.SourceVRChatConfig => "from VRChat",
            VRChatSettings.SourceCommandLine => "from --folder, this run only",
            _ => "VRChat's default",
        };
        return $"{vrchat.PictureFolder} ({source})";
    }

    /// <summary>The folder holding VRChat's <c>config.json</c>, or null when it isn't known.</summary>
    public static string? VRChatConfigFolder(VRChatSettings vrchat)
    {
        ArgumentNullException.ThrowIfNull(vrchat);
        var path = vrchat.ConfigPath ?? VRChatSettings.FindDefaultConfigPath();
        return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
    }

    // ---- Validation ----

    /// <summary>Problems that stop the settings from being saved (empty when they are fine).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ConnectionError() is { } connection)
            errors.Add(connection);
        if (VrcxWaitSeconds is < 0 or > MaxVrcxWaitSeconds)
            errors.Add($"The VRCX wait must be between 0 and {MaxVrcxWaitSeconds} seconds.");
        if (!Enum.IsDefined(Visibility))
            errors.Add("Choose a visibility.");
        return errors;
    }

    /// <summary>The first problem with the URL or key, or null.</summary>
    private string? ConnectionError()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
            return "Enter the server URL, for example https://immich.example.com.";
        if (!ConfigStore.IsValidServerUrl(ServerUrl))
            return "The server URL must start with http:// or https://, for example https://immich.example.com.";

        var typed = ApiKey.Trim();
        if (typed.Length > 0)
            return typed.Any(char.IsWhiteSpace) ? "The API key can't contain spaces or line breaks." : null;
        if (!HasSavedKey)
            return "Enter an API key.";
        if (string.IsNullOrEmpty(_unprotect(_original.ApiKeyProtected)))
            return "The saved API key can't be read on this computer. Enter it again.";
        return null;
    }

    /// <summary>The key to test with: the typed one (trimmed), or the saved one.</summary>
    private string? EffectiveApiKey()
    {
        var typed = ApiKey.Trim();
        return typed.Length > 0 ? typed : _unprotect(_original.ApiKeyProtected);
    }

    // ---- Connection test ----

    /// <summary>
    /// Tests the URL and key in the window: the server version (reachable? Immich?), then the key (valid? can upload?),
    /// plus notes on the album permissions (when an album is set) and the optional copy/delete permissions. Never throws
    /// except for cancellation.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionError() is { } error)
            return new ConnectionTestResult(ConnectionTestOutcome.InvalidInput, error, []);

        var album = string.IsNullOrWhiteSpace(AlbumName) ? null : AlbumName.Trim();
        IServerInfoClient client;
        try
        {
            client = _connect(ConfigStore.NormalizeServerUrl(ServerUrl)!, EffectiveApiKey()!);
        }
        catch (ArgumentException ex)
        {
            return new ConnectionTestResult(ConnectionTestOutcome.InvalidInput, ex.Message, []);
        }

        using (client)
        {
            try
            {
                return await EvaluateAsync(client, album, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ConnectionTestResult(ConnectionTestOutcome.Unreachable, $"✖ Can't reach the server: {ex.Message}", []);
            }
        }
    }

    /// <summary>The test itself, on an existing client.</summary>
    internal static async Task<ConnectionTestResult> EvaluateAsync(IServerInfoClient client, string? albumName, CancellationToken cancellationToken)
    {
        var version = await client.GetServerVersionAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
        if (!version.Succeeded)
        {
            if (version.HttpStatus is null)
                return Fail(ConnectionTestOutcome.Unreachable,
                    "✖ Can't reach the server. Check the address and that the server is running.");
            if (version.HttpStatus is 408 or 429 or >= 500)
                return Fail(ConnectionTestOutcome.ServerError,
                    $"✖ The server answered with an error (HTTP {version.HttpStatus}). Try again later.");
            return Fail(ConnectionTestOutcome.NotImmich,
                $"✖ The server answered, but it doesn't look like Immich (HTTP {version.HttpStatus}). Check the URL.");
        }

        var immich = $"Immich {version.Value}";
        var key = await client.GetMyApiKeyAsync(albumName, TestTimeout, cancellationToken).ConfigureAwait(false);
        switch (key.Status)
        {
            case ApiKeyCheckStatus.KeyRejected:
                return Fail(ConnectionTestOutcome.KeyRejected,
                    $"✖ Connected to {immich}, but the API key was rejected. Check that it was copied completely and hasn't been deleted.",
                    version.Value);
            case ApiKeyCheckStatus.Unreachable:
                return Fail(ConnectionTestOutcome.Unreachable,
                    $"✖ Connected to {immich}, but the key check got no answer. Try again.", version.Value);
            case ApiKeyCheckStatus.UnexpectedResponse:
                return Fail(ConnectionTestOutcome.ServerError,
                    $"✖ Connected to {immich}, but the key check gave an unexpected answer (HTTP {key.HttpStatus?.ToString() ?? "?"}).",
                    version.Value);
        }

        var info = key.Key!;
        var name = string.IsNullOrWhiteSpace(info.Name) ? "" : $" \"{info.Name}\"";
        if (!info.CanUpload)
            return Fail(ConnectionTestOutcome.MissingUploadPermission,
                $"✖ Connected to {immich}, but key{name} is missing the '{ApiKeyInfo.PermissionAssetUpload}' permission.",
                version.Value, info.Name);

        var notes = new List<string>();
        if (albumName is not null)
        {
            var missing = info.MissingAlbumPermissions;
            notes.Add(missing.Count == 0
                ? $"Album \"{albumName}\": the key can use it."
                : $"⚠ Album \"{albumName}\": the key is missing {string.Join(", ", missing)}. Photos upload, but won't be added to the album.");
        }
        if (!info.CanReplace)
        {
            var missing = new[] { ApiKeyInfo.PermissionAssetCopy, ApiKeyInfo.PermissionAssetDelete }.Where(p => !info.Has(p));
            notes.Add($"Optional: {string.Join(", ", missing)} to replace photos VRCX edits late (otherwise both versions are kept).");
        }

        return new ConnectionTestResult(ConnectionTestOutcome.Success,
            $"✔ Connected — {immich}, key{name} can upload", notes, version.Value, info.Name);

        static ConnectionTestResult Fail(ConnectionTestOutcome outcome, string text, Version? v = null, string? keyName = null) =>
            new(outcome, text, [], v, keyName);
    }

    // ---- Result ----

    /// <summary>
    /// <paramref name="config"/> with this window's settings: the normalised URL, the new key encrypted (or the saved one
    /// kept when the box is blank), the album (null when blank), visibility, VRCX wait and exact names. Call after
    /// <see cref="Validate"/> passed.
    /// </summary>
    public AppConfig ApplyTo(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var typed = ApiKey.Trim();
        return config with
        {
            ServerUrl = ConfigStore.NormalizeServerUrl(ServerUrl.Trim()),
            ApiKeyProtected = typed.Length > 0 ? ProtectOnce(typed) : config.ApiKeyProtected,
            AlbumName = string.IsNullOrWhiteSpace(AlbumName) ? null : AlbumName.Trim(),
            Visibility = Visibility,
            VrcxWaitSeconds = VrcxWaitSeconds,
            StrictFileNames = StrictFileNames,
        };
    }

    private string? _protectedFor;
    private string? _protectedValue;

    /// <summary>Encrypts a key once, so applying to both the saved and the run config stores the same value.</summary>
    private string ProtectOnce(string key)
    {
        if (_protectedFor != key || _protectedValue is null)
        {
            _protectedValue = _protect(key);
            _protectedFor = key;
        }
        return _protectedValue;
    }
}
