namespace WinTubeRelay.Tray;

internal interface IMpvPlayback
{
    string? LastPlaybackError { get; }

    void Play(AppSettings settings, string url, bool enqueue, string audioDeviceId);

    void Stop(AppSettings settings, string audioDeviceId);

    void Pause(AppSettings settings, string audioDeviceId);

    void Resume(AppSettings settings, string audioDeviceId);

    void Toggle(AppSettings settings, string audioDeviceId);

    void Skip(AppSettings settings, string audioDeviceId);

    void SetVolume(AppSettings settings, string audioDeviceId, double volume);

    void Mute(AppSettings settings, string audioDeviceId);

    void Unmute(AppSettings settings, string audioDeviceId);

    bool IsPlayerRunning(AppSettings settings);

    bool TryApplyAudioOutput(AppSettings settings, string audioDeviceId, bool forceReinitialize = false);

    bool QuitPlayer(AppSettings settings);

    void InvokeExclusive(Action action);

    ApiStatusSnapshot GetApiStatusSnapshot(AppSettings settings, string audioDeviceId);

    (double? Volume, bool? Mute) GetVolumeSnapshot(AppSettings settings, string audioDeviceId);
}
