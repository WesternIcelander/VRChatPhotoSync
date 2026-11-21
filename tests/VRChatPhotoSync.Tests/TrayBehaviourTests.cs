using System.Runtime.InteropServices;

namespace VRChatPhotoSync.Tests;

/// <summary>The tray icon and what the close and minimize buttons do (STA tests on real windows).</summary>
public class TrayBehaviourTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test timed out");
        if (error is not null)
            throw new Xunit.Sdk.XunitException($"STA test failed: {error}");
    }

    private static AppConfig SetUp => new() { ServerUrl = "https://immich.example.com/api", ApiKeyProtected = "x" };

    private sealed class Harness : IDisposable
    {
        public Harness(AppConfig config)
        {
            Live = new LiveConfig(config, config, Written.Add);
            Form = new MainForm(Live, new MainViewModel(config, @"D:\Photos\VRChat"), @"D:\Photos\VRChat", null);
            Form.ShowInTaskbar = false;
            Form.FormClosed += (_, _) => Closed = true;
        }

        public List<AppConfig> Written { get; } = [];
        public LiveConfig Live { get; }
        public MainForm Form { get; }
        public bool Closed { get; private set; }

        public void SysCommand(int command) => SendMessage(Form.Handle, MainForm.WM_SYSCOMMAND, command, IntPtr.Zero);

        public void PlainClose() => SendMessage(Form.Handle, MainForm.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        public void Dispose() => Form.Dispose();
    }

    [Fact]
    public void HideToTray_ScCloseHides_ButWmCloseExits()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { CloseButtonAction = CloseButtonAction.HideToTray });
            h.Form.Show();
            Assert.True(h.Form.Visible);

            h.SysCommand(MainForm.SC_CLOSE);
            Assert.False(h.Closed);
            Assert.False(h.Form.IsDisposed);
            Assert.False(h.Form.Visible);
            Assert.True(h.Form.IsInTray);
            Assert.True(h.Live.Saved.WindowMinimized);

            h.PlainClose();
            Assert.True(h.Closed);
            Assert.True(h.Form.IsDisposed);
            Assert.False(h.Live.Saved.WindowMinimized); // a normal exit clears it
        });
    }

    [Fact]
    public void HideToTray_WmCloseWhileVisible_Exits()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { CloseButtonAction = CloseButtonAction.HideToTray });
            h.Form.Show();
            h.PlainClose();
            Assert.True(h.Closed);
        });
    }

    [Fact]
    public void Exit_ScCloseExits()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { CloseButtonAction = CloseButtonAction.Exit });
            h.Form.Show();
            h.SysCommand(MainForm.SC_CLOSE);
            Assert.True(h.Closed);
        });
    }

    [Fact]
    public void MinimizeToTray_Hides_AndRestoreShowsAgain()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { MinimizeButtonAction = MinimizeButtonAction.Tray });
            h.Form.Show();
            h.SysCommand(MainForm.SC_MINIMIZE);
            Assert.False(h.Form.Visible);
            Assert.False(h.Closed);
            Assert.True(h.Live.Saved.WindowMinimized);

            h.Form.RestoreWindow();
            Assert.True(h.Form.Visible);
            Assert.Equal(FormWindowState.Normal, h.Form.WindowState);
            Assert.False(h.Live.Saved.WindowMinimized);
        });
    }

    [Fact]
    public void MinimizeToTaskbar_Minimizes_AndStaysVisible()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { MinimizeButtonAction = MinimizeButtonAction.Taskbar });
            h.Form.Show();
            h.SysCommand(MainForm.SC_MINIMIZE);
            Assert.True(h.Form.Visible);
            Assert.Equal(FormWindowState.Minimized, h.Form.WindowState);
            Assert.True(h.Live.Saved.WindowMinimized);
        });
    }

    [Fact]
    public void StartMinimized_ToTray_CreatesTheWindowHidden()
    {
        RunSta(() =>
        {
            // --minimized sets StartMinimized for the run (CommandLineOptions.ApplyTo).
            var config = CommandLineOptions.Parse(["--minimized"]).ApplyTo(SetUp with { MinimizeButtonAction = MinimizeButtonAction.Tray });
            using var h = new Harness(config);
            h.Form.Show();
            Assert.False(h.Form.Visible);
            Assert.True(h.Form.IsHandleCreated);
            Assert.True(h.Form.IsInTray);

            h.Form.RestoreWindow();
            Assert.True(h.Form.Visible);
        });
    }

    [Fact]
    public void StartMinimized_ToTaskbar_ShowsMinimized()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { StartMinimized = true, MinimizeButtonAction = MinimizeButtonAction.Taskbar });
            h.Form.Show();
            Assert.True(h.Form.Visible);
            Assert.Equal(FormWindowState.Minimized, h.Form.WindowState);
        });
    }

    [Fact]
    public void StillMinimizedAfterAKill_StartsMinimizedAgain()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { WindowMinimized = true, MinimizeButtonAction = MinimizeButtonAction.Tray });
            h.Form.Show();
            Assert.False(h.Form.Visible);
        });
    }

    [Fact]
    public void NotMinimized_StartsVisible()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp);
            h.Form.Show();
            Assert.True(h.Form.Visible);
            Assert.Equal(FormWindowState.Normal, h.Form.WindowState);
        });
    }

    [Fact]
    public void WindowPosition_IsRestored_AndSavedOnExit()
    {
        RunSta(() =>
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            var start = new Point(area.Left + 40, area.Top + 40);
            using var h = new Harness(SetUp with { WindowLeft = start.X, WindowTop = start.Y });
            h.Form.Show();
            Assert.Equal(start, h.Form.Location);

            var moved = new Point(area.Left + 70, area.Top + 60);
            h.Form.Location = moved;
            h.PlainClose();

            Assert.Equal(moved.X, h.Live.Saved.WindowLeft);
            Assert.Equal(moved.Y, h.Live.Saved.WindowTop);
            Assert.Equal(moved.X, h.Written[^1].WindowLeft);
        });
    }

    [Fact]
    public void WindowPosition_OffScreen_IsIgnored()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp with { WindowLeft = -50000, WindowTop = -50000 });
            Assert.Equal(FormStartPosition.CenterScreen, h.Form.StartPosition);
            Assert.False(MainForm.IsOnScreen(new Rectangle(-50000, -50000, 376, 199)));
            var area = Screen.PrimaryScreen!.WorkingArea;
            Assert.True(MainForm.IsOnScreen(new Rectangle(area.Left + 10, area.Top + 10, 376, 199)));
        });
    }

    [Fact]
    public void TrayIcon_IsAlwaysPresent_WithCountTooltipAndShowExitMenu()
    {
        RunSta(() =>
        {
            using var h = new Harness(SetUp);
            var icon = h.Form.TrayIcon;
            Assert.True(icon.Visible);
            Assert.StartsWith("PhotoSync — 0 uploaded", icon.Text);
            var items = icon.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
            Assert.Equal(["Show", "Exit"], items);

            h.Form.Show();
            h.Form.HideToTray();
            icon.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Show").PerformClick();
            Assert.True(h.Form.Visible);

            icon.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Exit").PerformClick();
            Assert.True(h.Closed);
        });
    }

    [Fact]
    public void TrayText_HasTheCount_AndFitsTheLimit()
    {
        var view = new MainView("PhotoSync — 12 uploaded", new string('x', 300), null, "", "", null, null, "", "", true, false);
        var text = MainForm.TrayText(view);
        Assert.StartsWith("PhotoSync — 12 uploaded\n", text);
        Assert.True(text.Length <= MainForm.MaxTrayTextLength);
    }
}
