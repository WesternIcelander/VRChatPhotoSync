namespace VRChatPhotoSync;

public partial class Form1 : Form
{
    /// <summary>For the designer only.</summary>
    public Form1()
    {
        InitializeComponent();
        Icon = AppIcon.Icon;
    }

    public Form1(StartupContext startup)
        : this()
    {
        Startup = startup;
    }

    /// <summary>The startup settings (config, command-line overrides, photo folder). Null only in the designer.</summary>
    internal StartupContext? Startup { get; }
}
