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
        if (options.Errors.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, options.Errors),
                "VRChat PhotoSync: command-line options",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        var store = new ConfigStore();
        var loaded = store.Load();
        // The command line overrides the file for this run only: savedConfig is what gets
        // written back when settings change, runConfig is what this run uses.
        var savedConfig = loaded.Config;
        var runConfig = options.ApplyTo(savedConfig);
        var vrchat = VRChatSettings.Load(options.Folder);

        Application.Run(new Form1(new StartupContext(options, store, loaded, savedConfig, runConfig, vrchat)));
    }
}

/// <summary>Everything worked out at startup, handed to the main window.</summary>
/// <param name="Options">The parsed command line.</param>
/// <param name="Store">Where the config is loaded from and saved to.</param>
/// <param name="ConfigLoad">The result of loading <c>config.json</c>.</param>
/// <param name="SavedConfig">The config as stored in the file (no command-line overrides). Save changes on top of this.</param>
/// <param name="RunConfig">The config for this run: <paramref name="SavedConfig"/> with the command-line overrides applied. Never saved.</param>
/// <param name="VRChat">The photo folder (from <c>--folder</c>, VRChat's config, or the default).</param>
public sealed record StartupContext(
    CommandLineOptions Options,
    ConfigStore Store,
    ConfigLoadResult ConfigLoad,
    AppConfig SavedConfig,
    AppConfig RunConfig,
    VRChatSettings VRChat)
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
