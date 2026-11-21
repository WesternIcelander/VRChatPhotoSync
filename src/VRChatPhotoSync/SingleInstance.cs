namespace VRChatPhotoSync;

/// <summary>
/// Keeps one copy of the app running per Windows session, with a named mutex. A second copy waits a moment for the
/// mutex (a VRCX kill-then-relaunch can overlap briefly), and if it is still held, asks the running copy to show its
/// window (a named event) and exits.
/// <para>
/// A copy that was killed leaves the mutex <i>abandoned</i>: the next <see cref="WaitHandle.WaitOne(TimeSpan)"/> throws
/// <see cref="AbandonedMutexException"/> but does give ownership to the caller, so that counts as acquired. Otherwise
/// the relaunch straight after a VRCX kill would think another copy is running.
/// </para>
/// The mutex has thread affinity: acquire and dispose on the same thread (the UI thread in <c>Program.Main</c>).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name used by the app (per Windows session).</summary>
    public const string DefaultName = @"Local\VRChatPhotoSync.SingleInstance";

    /// <summary>How long a second copy waits for the mutex before giving up.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, bool wasAbandoned)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        WasAbandoned = wasAbandoned;
    }

    /// <summary>True when the previous owner was killed without releasing the mutex.</summary>
    public bool WasAbandoned { get; }

    /// <summary>The name of the "show your window" event for a mutex name.</summary>
    public static string ShowEventName(string name) => name + ".Show";

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the mutex <paramref name="name"/>. Returns the held instance, or null when
    /// another copy still holds it. An abandoned mutex counts as acquired (<see cref="WasAbandoned"/>).
    /// </summary>
    public static SingleInstance? TryAcquire(string name, TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        var abandoned = false;
        try
        {
            acquired = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // the previous owner was killed; we own it now
            abandoned = true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(name));
        return new SingleInstance(mutex, showEvent, abandoned);
    }

    /// <summary>
    /// Asks the copy holding <paramref name="name"/> to show its window. Returns false when no copy is listening.
    /// </summary>
    public static bool SignalRunning(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!EventWaitHandle.TryOpenExisting(ShowEventName(name), out var handle))
            return false;
        using (handle)
            return handle.Set();
    }

    /// <summary>Calls <paramref name="onShow"/> (on a thread-pool thread) each time another copy asks to show the window.</summary>
    public void ListenForShow(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration?.Unregister(null);
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            try { onShow(); }
            catch { /* never let a show request crash the app */ }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Releases the mutex (call on the thread that acquired it).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registration?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; the mutex is freed (abandoned) when the handle closes.
        }
        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
