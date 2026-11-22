namespace VRChatPhotoSync.Tests;

public class MainFormTests
{
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

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static AppConfig SetUp => new() { ServerUrl = "https://immich.example.com/api", ApiKeyProtected = "x" };

    [Fact]
    public void Form_IsSmallFixedAndShowsTheViewModel()
    {
        RunSta(() =>
        {
            var vm = new MainViewModel(SetUp, @"D:\Photos\VRChat");
            using var form = new MainForm(new LiveConfig(SetUp, SetUp, _ => { }), vm, @"D:\Photos\VRChat", null);
            Assert.Equal(new Size(380, 164), form.ClientSize);
            Assert.Equal(FormBorderStyle.FixedSingle, form.FormBorderStyle);
            Assert.Equal("PhotoSync — 0 uploaded", form.Text);
            Assert.Contains(form.Controls.OfType<Label>(), l => l.Text == MainViewModel.WatchingText);
        });
    }

    [Fact]
    public void UpdatesFromOtherThreads_AreMarshalledToTheForm()
    {
        RunSta(() =>
        {
            var vm = new MainViewModel(SetUp, @"D:\Photos\VRChat");
            using var form = new MainForm(new LiveConfig(SetUp, SetUp, _ => { }), vm, @"D:\Photos\VRChat", null);
            _ = form.Handle; // create the handle so updates go through BeginInvoke
            var path = @"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png";

            var worker = Task.Run(() =>
            {
                for (var i = 0; i < 3; i++)
                    vm.Record(new UploadEvent(UploadOutcome.Uploaded, path, "x"));
                vm.SetQueueLength(2);
            });
            PumpUntil(() => worker.IsCompleted && form.Text == "PhotoSync — 3 uploaded");

            Assert.Equal("PhotoSync — 3 uploaded", form.Text);
            Assert.Contains(form.Controls.OfType<Label>(), l => l.Text == "Queued: 2");
            Assert.Contains(form.Controls.OfType<Label>(), l => l.Text == "Last: VRChat_2026-11-21_21-03-08.102_3840x2160.png");
        });
    }

    [Fact]
    public void Checkboxes_SaveToConfigImmediately()
    {
        RunSta(() =>
        {
            var written = new List<AppConfig>();
            var live = new LiveConfig(SetUp, SetUp, written.Add);
            var vm = new MainViewModel(SetUp, @"D:\Photos\VRChat");
            using var form = new MainForm(live, vm, @"D:\Photos\VRChat", null);
            var boxes = form.Controls.OfType<CheckBox>().ToList();
            var prints = boxes.Single(b => b.Text.StartsWith("Skip prints"));
            var layers = boxes.Single(b => b.Text.StartsWith("Upload photo layers"));
            Assert.True(prints.Checked);
            Assert.False(layers.Checked);

            prints.Checked = false;
            layers.Checked = true;

            Assert.Equal(2, written.Count);
            Assert.False(written[^1].ExcludePrints);
            Assert.True(written[^1].UploadLayers);
            Assert.False(live.Current.ExcludePrints);
            Assert.True(vm.Snapshot().UploadLayers);
        });
    }
}
