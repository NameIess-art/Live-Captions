namespace LiveCaptions.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _listener;
    internal bool IsPrimary { get; }

    internal SingleInstance(string name)
    {
        // Create the signal first so a second launch during XAML startup is retained.
        _activation = new(false, EventResetMode.AutoReset, name + ".Activate");
        _mutex = new(false, name + ".Mutex");
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (!IsPrimary) _activation.Set();
    }

    internal void Listen(Action activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can listen for activation.");
        _listener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activate(), null,
            Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        // Ownership is acquired and released on the application's main thread.
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activation.Dispose();
    }
}
