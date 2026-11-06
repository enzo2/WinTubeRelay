namespace WinTubeRelay.Tray;

internal enum PlaybackIntent
{
    Play,
    Enqueue,
    Resume,
}

internal interface IPlaybackPreparer
{
    string Prepare(PlaybackIntent intent);
}

internal static class DisplayOutputClassifier
{
    public static bool IsDisplayBacked(string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
        {
            return false;
        }

        return outputName.Contains("display", StringComparison.OrdinalIgnoreCase)
            || outputName.Contains("monitor", StringComparison.OrdinalIgnoreCase)
            || outputName.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
            || outputName.Contains("lg", StringComparison.OrdinalIgnoreCase)
            || outputName.Contains("tv", StringComparison.OrdinalIgnoreCase)
            || outputName.Contains("projector", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TogglePreparation
{
    public static bool NeedsWake(ApiStatusSnapshot snapshot)
    {
        if (string.Equals(snapshot.PlayerState, "paused", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return snapshot.Properties.TryGetValue("pause", out var pause) && pause is true;
    }
}

internal sealed class DisplayPlaybackCoordinator : IPlaybackPreparer
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(350);

    private readonly Func<string?> _selectedDeviceId;
    private readonly Func<string?> _selectedDeviceName;
    private readonly Func<IReadOnlyList<AudioOutputDevice>> _getOutputs;
    private readonly Action _wakeDisplay;
    private readonly Action<Action> _invokeOnUiThread;
    private readonly IMpvPlayback _player;
    private readonly AppSettings _settings;
    private readonly Action<string>? _log;
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<TimeSpan> _sleep;
    private readonly Action? _onStillUnavailable;

    public DisplayPlaybackCoordinator(
        Func<string?> selectedDeviceId,
        Func<string?> selectedDeviceName,
        Func<IReadOnlyList<AudioOutputDevice>> getOutputs,
        Action wakeDisplay,
        Action<Action> invokeOnUiThread,
        IMpvPlayback player,
        AppSettings settings,
        Action<string>? log = null,
        TimeSpan? waitTimeout = null,
        TimeSpan? pollInterval = null,
        Func<DateTime>? utcNow = null,
        Action<TimeSpan>? sleep = null,
        Action? onStillUnavailable = null)
    {
        _selectedDeviceId = selectedDeviceId;
        _selectedDeviceName = selectedDeviceName;
        _getOutputs = getOutputs;
        _wakeDisplay = wakeDisplay;
        _invokeOnUiThread = invokeOnUiThread;
        _player = player;
        _settings = settings;
        _log = log;
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sleep = sleep ?? Thread.Sleep;
        _onStillUnavailable = onStillUnavailable;
    }

    public string Prepare(PlaybackIntent intent)
    {
        var selectedId = _selectedDeviceId();
        var selectedName = _selectedDeviceName();
        var displayBacked = DisplayOutputClassifier.IsDisplayBacked(selectedName);
        var initial = SafeOutputs();
        var resolved = Find(initial, selectedId, selectedName);
        var initiallyPresent = resolved is not null;

        if (displayBacked)
        {
            _invokeOnUiThread(_wakeDisplay);
        }

        if (!initiallyPresent && displayBacked)
        {
            var deadline = _utcNow() + _waitTimeout;
            while (resolved is null && _utcNow() < deadline)
            {
                _sleep(_pollInterval);
                resolved = Find(SafeOutputs(), selectedId, selectedName);
            }
        }

        var deviceId = resolved?.MpvAudioDeviceId ?? "auto";
        var selectedWasMissing = !initiallyPresent
            && (!string.IsNullOrWhiteSpace(selectedId) || !string.IsNullOrWhiteSpace(selectedName));
        if (selectedWasMissing && _player.IsPlayerRunning(_settings))
        {
            var applied = false;
            for (var attempt = 0; attempt < 3 && !applied; attempt++)
            {
                if (attempt > 0)
                {
                    _sleep(_pollInterval);
                }

                _log?.Invoke($"Reinitializing audio output '{deviceId}' before {intent} (attempt {attempt + 1}).");
                applied = _player.TryApplyAudioOutput(_settings, deviceId, forceReinitialize: true);
            }

            if (!applied)
            {
                _onStillUnavailable?.Invoke();
                throw new InvalidOperationException($"Could not reinitialize audio output '{deviceId}'.");
            }
        }

        if (resolved is null && selectedWasMissing)
        {
            _onStillUnavailable?.Invoke();
        }

        return deviceId;
    }

    private IReadOnlyList<AudioOutputDevice> SafeOutputs()
    {
        try
        {
            return _getOutputs() ?? Array.Empty<AudioOutputDevice>();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not enumerate audio outputs while preparing playback: {ex.Message}");
            return Array.Empty<AudioOutputDevice>();
        }
    }

    private static AudioOutputDevice? Find(
        IReadOnlyList<AudioOutputDevice> devices,
        string? selectedId,
        string? selectedName)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var byId = devices.FirstOrDefault(device => string.Equals(device.Id, selectedId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(selectedName))
        {
            return null;
        }

        return devices.FirstOrDefault(device =>
            string.Equals(device.FriendlyName, selectedName, StringComparison.CurrentCultureIgnoreCase));
    }
}
