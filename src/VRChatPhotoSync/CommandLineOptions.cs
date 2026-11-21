namespace VRChatPhotoSync;

/// <summary>
/// Command-line options. They override <c>config.json</c> for this run only and are never
/// saved back to it.
/// <list type="bullet">
/// <item><c>--folder &lt;path&gt;</c>: watch this folder instead of VRChat's photo folder.</item>
/// <item><c>--server &lt;url&gt;</c>: use this Immich server instead of the saved one.</item>
/// <item><c>--minimized</c>: start minimized (to the taskbar or tray, per the minimize setting).</item>
/// <item><c>--settings</c>: open the Immich server window at startup even if the config is fine.</item>
/// </list>
/// Option names ignore case, and <c>--name=value</c> works as well as <c>--name value</c>.
/// Parsing never throws: problems are collected in <see cref="Errors"/> and that option is ignored.
/// </summary>
/// <param name="Folder">The <c>--folder</c> value (not yet expanded or made absolute; <see cref="VRChatSettings.Load"/> does that), or null.</param>
/// <param name="ServerUrl">The <c>--server</c> value, normalised like the saved URL (see <see cref="ConfigStore.NormalizeServerUrl"/>), or null.</param>
/// <param name="Minimized">True when <c>--minimized</c> was given.</param>
/// <param name="ForceSettings">True when <c>--settings</c> was given.</param>
/// <param name="Errors">Unknown options, missing values and invalid URLs. Empty when everything was fine.</param>
public sealed record CommandLineOptions(
    string? Folder,
    string? ServerUrl,
    bool Minimized,
    bool ForceSettings,
    IReadOnlyList<string> Errors)
{
    public const string FolderOption = "--folder";
    public const string ServerOption = "--server";
    public const string MinimizedOption = "--minimized";
    public const string SettingsOption = "--settings";

    /// <summary>No options given.</summary>
    public static CommandLineOptions Empty { get; } = new(null, null, false, false, []);

    /// <summary>A short usage line for error messages.</summary>
    public const string Usage = "Options: --folder <path>, --server <url>, --minimized, --settings";

    /// <summary>Parses the arguments (as passed to <c>Main</c>, without the exe name).</summary>
    public static CommandLineOptions Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
            return Empty;

        string? folder = null;
        string? server = null;
        bool minimized = false;
        bool settings = false;
        var errors = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i]?.Trim() ?? "";
            if (arg.Length == 0)
                continue;

            string name = arg;
            string? inlineValue = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            if (Is(name, MinimizedOption) || Is(name, SettingsOption))
            {
                if (inlineValue is not null)
                    errors.Add($"{name} doesn't take a value; ignoring \"{arg}\".");
                else if (Is(name, MinimizedOption))
                    minimized = true;
                else
                    settings = true;
            }
            else if (Is(name, FolderOption) || Is(name, ServerOption))
            {
                string? value = inlineValue;
                if (value is null)
                {
                    // The next argument is the value, unless it is missing or is another option.
                    if (i + 1 < args.Count && !IsKnownOption(args[i + 1]))
                        value = args[++i];
                }

                value = value?.Trim().Trim('"').Trim();
                if (string.IsNullOrEmpty(value))
                {
                    errors.Add($"{name.ToLowerInvariant()} needs a value.");
                    continue;
                }

                if (Is(name, FolderOption))
                {
                    folder = value;
                }
                else if (ConfigStore.IsValidServerUrl(value))
                {
                    server = ConfigStore.NormalizeServerUrl(value);
                }
                else
                {
                    errors.Add($"--server \"{value}\" is not an absolute http or https URL; ignoring it.");
                }
            }
            else
            {
                errors.Add($"Unknown option \"{arg}\". {Usage}");
            }
        }

        return new CommandLineOptions(folder, server, minimized, settings, errors);
    }

    /// <summary>
    /// Returns the config to use for this run: <paramref name="config"/> with
    /// <c>--server</c> and <c>--minimized</c> applied. The result must not be saved, so
    /// that the overrides stay for this run only.
    /// </summary>
    public AppConfig ApplyTo(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (ServerUrl is not null)
            config = config with { ServerUrl = ServerUrl };
        if (Minimized)
            config = config with { StartMinimized = true };
        return config;
    }

    /// <summary>True when any option that changes the config for this run was given.</summary>
    public bool HasConfigOverrides => ServerUrl is not null || Minimized;

    private static bool Is(string arg, string option) => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownOption(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;
        var name = arg.Trim();
        int eq = name.IndexOf('=');
        if (eq > 2)
            name = name[..eq];
        return Is(name, FolderOption) || Is(name, ServerOption) || Is(name, MinimizedOption) || Is(name, SettingsOption);
    }
}
