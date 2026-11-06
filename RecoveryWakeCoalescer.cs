namespace WinTubeRelay.Tray;

internal sealed class RecoveryWakeCoalescer
{
    private int _queued;
    private int _pendingWake;

    public bool TryBegin(bool wakeDisplay)
    {
        if (wakeDisplay)
        {
            Interlocked.Exchange(ref _pendingWake, 1);
        }

        return Interlocked.Exchange(ref _queued, 1) == 0;
    }

    public bool ConsumeWake()
    {
        return Interlocked.Exchange(ref _pendingWake, 0) == 1;
    }

    public bool Complete()
    {
        if (Interlocked.Exchange(ref _pendingWake, 0) == 1)
        {
            return true;
        }

        Interlocked.Exchange(ref _queued, 0);
        if (Interlocked.Exchange(ref _pendingWake, 0) == 1)
        {
            if (Interlocked.CompareExchange(ref _queued, 1, 0) == 0)
            {
                return true;
            }

            Interlocked.Exchange(ref _pendingWake, 1);
        }

        return false;
    }
}
