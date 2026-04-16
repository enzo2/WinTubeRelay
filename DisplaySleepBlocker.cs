using System.Runtime.InteropServices;

namespace WinTubeRelay.Tray;

internal sealed class DisplaySleepBlocker
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly Action<string> _log;
    private bool _isActive;

    public DisplaySleepBlocker(Action<string> log)
    {
        _log = log;
    }

    public void SetPlaybackActive(bool isPlaying)
    {
        if (isPlaying == _isActive)
        {
            return;
        }

        var flags = isPlaying
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous;

        var result = SetThreadExecutionState(flags);
        if (result == 0)
        {
            _log($"SetThreadExecutionState failed while {(isPlaying ? "acquiring" : "releasing")} playback wake lock.");
            return;
        }

        _isActive = isPlaying;
        _log(isPlaying
            ? "Playback wake lock enabled. Preventing display sleep while playing."
            : "Playback wake lock released.");
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
