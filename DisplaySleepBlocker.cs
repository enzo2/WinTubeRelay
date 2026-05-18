using System.Runtime.InteropServices;

namespace WinTubeRelay.Tray;

internal sealed class DisplaySleepBlocker
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly Action<string> _log;
    private readonly object _syncRoot = new();
    private bool _playbackActive;
    private bool _isActive;

    public DisplaySleepBlocker(Action<string> log)
    {
        _log = log;
    }

    public void SetPlaybackActive(bool isPlaying)
    {
        lock (_syncRoot)
        {
            _playbackActive = isPlaying;
            Apply();
        }
    }

    public void Release()
    {
        lock (_syncRoot)
        {
            _playbackActive = false;
            Apply();
        }
    }

    private void Apply()
    {
        var shouldBlock = _playbackActive;
        if (shouldBlock == _isActive)
        {
            return;
        }

        var flags = shouldBlock
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous;

        var result = SetThreadExecutionState(flags);
        if (result == 0)
        {
            _log($"SetThreadExecutionState failed while {(shouldBlock ? "acquiring" : "releasing")} wake lock.");
            return;
        }

        _isActive = shouldBlock;
        _log(shouldBlock
            ? "Wake lock enabled. Preventing display sleep while playback is active."
            : "Wake lock released.");
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
