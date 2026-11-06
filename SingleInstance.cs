namespace WinTubeRelay.Tray;

internal static class SingleInstance
{
    [ThreadStatic]
    private static HashSet<string>? _ownedNames;

    public static bool TryAcquire(string name, out SingleInstanceLock? instance)
    {
        instance = null;
        _ownedNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_ownedNames.Contains(name))
        {
            return false;
        }

        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        try
        {
            if (!createdNew)
            {
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        mutex.Dispose();
                        return false;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner crashed. WaitOne granted ownership.
                }
            }

            _ownedNames.Add(name);
            instance = new SingleInstanceLock(mutex, () => _ownedNames.Remove(name));
            return true;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }
}

internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly Action _onRelease;
    private int _released;

    public SingleInstanceLock(Mutex mutex, Action onRelease)
    {
        _mutex = mutex;
        _onRelease = onRelease;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _onRelease();
        _mutex.Dispose();
    }
}
