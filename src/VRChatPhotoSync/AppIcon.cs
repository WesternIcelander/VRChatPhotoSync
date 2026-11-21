namespace VRChatPhotoSync;

/// <summary>The application icon, loaded from the embedded <c>app.ico</c>.</summary>
internal static class AppIcon
{
    public const string ResourceName = "VRChatPhotoSync.app.ico";

    private static readonly Lazy<Icon> s_icon = new(Load);

    public static Icon Icon => s_icon.Value;

    private static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        return new Icon(stream);
    }
}
