using System.Diagnostics;

namespace VRChatPhotoSync.Tests;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => @"Local\VRChatPhotoSync.Test." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Starts a separate process that takes the mutex and keeps it (until killed). PowerShell is always present on
    /// Windows, so no helper executable is needed.
    /// </summary>
    private static Process StartHolder(string name)
    {
        var script =
            $"$m = New-Object System.Threading.Mutex($false, '{name}'); " +
            "if (-not $m.WaitOne(10000)) { exit 1 }; " +
            "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); " +
            "Start-Sleep -Seconds 60";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        var line = process.StandardOutput.ReadLineAsync();
        if (!line.Wait(TimeSpan.FromSeconds(30)) || line.Result != "ready")
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("The child process didn't take the mutex.");
        }
        return process;
    }

    /// <summary>
    /// The VRCX case: the relaunched copy is already waiting for the mutex when the old copy is hard-killed. The mutex
    /// is then abandoned (<see cref="AbandonedMutexException"/>), which must count as acquired.
    /// </summary>
    [Fact]
    public void AbandonedMutex_ChildAcquiresAndIsKilledWhileWaiting_AcquireSucceeds()
    {
        var name = UniqueName();
        using var child = StartHolder(name);
        try
        {
            // While the child holds it, a second copy gives up after the wait.
            Assert.Null(SingleInstance.TryAcquire(name, TimeSpan.FromMilliseconds(200)));

            bool? acquired = null, abandoned = null;
            using var waiting = new ManualResetEventSlim();
            var next = new Thread(() =>
            {
                waiting.Set();
                using var instance = SingleInstance.TryAcquire(name, TimeSpan.FromSeconds(20));
                acquired = instance is not null;
                abandoned = instance?.WasAbandoned;
            });
            next.Start();
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(300); // let it reach WaitOne

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10_000));
            Assert.True(next.Join(TimeSpan.FromSeconds(20)));

            Assert.True(acquired);
            Assert.True(abandoned);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }

        // Released normally this time, so the next one acquires it cleanly.
        using var again = SingleInstance.TryAcquire(name, TimeSpan.FromSeconds(1));
        Assert.NotNull(again);
        Assert.False(again.WasAbandoned);
    }

    /// <summary>The same with another handle open across the kill (so the abandoned mutex object survives).</summary>
    [Fact]
    public void AbandonedMutex_ChildKilled_ThenNextAcquireSucceeds()
    {
        var name = UniqueName();
        using var keepAlive = new Mutex(initiallyOwned: false, name);
        using var child = StartHolder(name);
        try
        {
            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10_000));
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }

        using var instance = SingleInstance.TryAcquire(name, TimeSpan.FromSeconds(1));
        Assert.NotNull(instance);
        Assert.True(instance.WasAbandoned);
    }

    /// <summary>When the killed copy's mutex is already gone (no handle left), the next copy simply gets a fresh one.</summary>
    [Fact]
    public void KilledHolder_WithNoOtherHandle_NextAcquireSucceeds()
    {
        var name = UniqueName();
        using (var child = StartHolder(name))
        {
            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(10_000));
        }

        using var instance = SingleInstance.TryAcquire(name, TimeSpan.FromSeconds(1));
        Assert.NotNull(instance);
    }

    [Fact]
    public void Held_WaitsForRelease_WithinTheWait()
    {
        var name = UniqueName();
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var first = SingleInstance.TryAcquire(name, TimeSpan.Zero);
            Assert.NotNull(first);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        holder.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));

        // A kill-then-relaunch overlap: the old copy lets go while the new one waits.
        _ = Task.Delay(300).ContinueWith(_ => release.Set(), TaskScheduler.Default);
        var watch = Stopwatch.StartNew();
        using var second = SingleInstance.TryAcquire(name, SingleInstance.DefaultWait);
        Assert.NotNull(second);
        Assert.False(second.WasAbandoned);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(200));
        holder.Join();
    }

    [Fact]
    public void SecondCopy_SignalsTheRunningCopyToShowItsWindow()
    {
        var name = UniqueName();
        using var running = SingleInstance.TryAcquire(name, TimeSpan.Zero);
        Assert.NotNull(running);
        using var shown = new SemaphoreSlim(0);
        running.ListenForShow(() => shown.Release());

        var acquiredOnOtherThread = true;
        var other = new Thread(() =>
        {
            using var second = SingleInstance.TryAcquire(name, TimeSpan.FromMilliseconds(100));
            acquiredOnOtherThread = second is not null;
        });
        other.Start();
        other.Join();
        Assert.False(acquiredOnOtherThread);

        Assert.True(SingleInstance.SignalRunning(name));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SingleInstance.SignalRunning(name));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalRunning_WithNoRunningCopy_ReturnsFalse()
    {
        Assert.False(SingleInstance.SignalRunning(UniqueName()));
    }
}
