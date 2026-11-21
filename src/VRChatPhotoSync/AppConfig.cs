using System.Text.Json.Serialization;

namespace VRChatPhotoSync;

/// <summary>
/// User settings stored in <c>config.json</c>. There is deliberately no photo-folder
/// field (the folder comes from VRChat's own config) and no file-type setting
/// (the upload rule is fixed to <c>VRChat_*.png</c>).
/// </summary>
public sealed record AppConfig
{
    /// <summary>Immich API base URL, normalised to end in <c>/api</c>. Null or empty = not set up.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>The API key, DPAPI-encrypted (CurrentUser) and base64-encoded. Never plain text.</summary>
    public string? ApiKeyProtected { get; init; }

    /// <summary>Album to add uploads to. Null or empty = no album.</summary>
    public string? AlbumName { get; init; }

    /// <summary>Immich visibility for uploaded assets.</summary>
    public AssetVisibility Visibility { get; init; } = AssetVisibility.Timeline;

    /// <summary>Skip VRChat prints (2048x1440 polaroid-style images).</summary>
    public bool ExcludePrints { get; init; } = true;

    /// <summary>Also upload the <c>_Environment</c>, <c>_Player</c> and <c>_UI</c> layer files of multi-layer photos.</summary>
    public bool UploadLayers { get; init; }

    /// <summary>Only accept file names that match VRChat's exact naming pattern.</summary>
    public bool StrictFileNames { get; init; } = true;

    /// <summary>How long to wait for VRCX to add its metadata chunk, in seconds. 0 = don't wait.</summary>
    public int VrcxWaitSeconds { get; init; } = 10;

    /// <summary>What the title-bar X button (or Alt+F4) does.</summary>
    public CloseButtonAction CloseButtonAction { get; init; } = CloseButtonAction.Exit;

    /// <summary>What the minimize button does.</summary>
    public MinimizeButtonAction MinimizeButtonAction { get; init; } = MinimizeButtonAction.Taskbar;

    /// <summary>Start with the window minimized (to the taskbar or tray, per <see cref="MinimizeButtonAction"/>).</summary>
    public bool StartMinimized { get; init; }

    /// <summary>True when the album feature is in use.</summary>
    [JsonIgnore]
    public bool HasAlbum => !string.IsNullOrWhiteSpace(AlbumName);
}

/// <summary>Immich asset visibility. Serialized as the lowercase API value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssetVisibility>))]
public enum AssetVisibility
{
    [JsonStringEnumMemberName("timeline")] Timeline,
    [JsonStringEnumMemberName("archive")] Archive,
    [JsonStringEnumMemberName("hidden")] Hidden,
    [JsonStringEnumMemberName("locked")] Locked,
}

[JsonConverter(typeof(JsonStringEnumConverter<CloseButtonAction>))]
public enum CloseButtonAction
{
    Exit,
    HideToTray,
}

[JsonConverter(typeof(JsonStringEnumConverter<MinimizeButtonAction>))]
public enum MinimizeButtonAction
{
    Taskbar,
    Tray,
}

public static class AssetVisibilityExtensions
{
    /// <summary>The value Immich expects in the <c>visibility</c> form field.</summary>
    public static string ToApiValue(this AssetVisibility visibility) => visibility switch
    {
        AssetVisibility.Timeline => "timeline",
        AssetVisibility.Archive => "archive",
        AssetVisibility.Hidden => "hidden",
        AssetVisibility.Locked => "locked",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, null),
    };
}
