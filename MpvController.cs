using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinTubeRelay.Tray;

internal enum PlayerState
{
    Unknown,
    Idle,
    Playing,
    Paused,
}

internal sealed record PlayerStatus(
    PlayerState State,
    string? MediaTitle,
    string? Path,
    double? TimePositionSeconds,
    double? DurationSeconds,
    int? PlaylistPosition,
    int? PlaylistCount,
    double? Volume,
    bool? Mute);

internal sealed record ApiStatusSnapshot(
    Dictionary<string, object?> Properties,
    List<string> Errors,
    string PlayerState);

internal sealed class MpvController : IDisposable
{
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Process? _mpvProcess;
    private int _requestId;
    private CancellationTokenSource? _eventMonitorCts;
    private Task? _eventMonitorTask;
    private string? _lastKnownMediaTitle;
    private string? _lastKnownPath;
    private bool _expectedProcessExit;
    private string? _resumeSourceUrl;
    private double? _lastStablePositionSeconds;
    private double? _lastStableDurationSeconds;
    private DateTime _suppressAutoResumeUntilUtc;
    private DateTime _lastAutoResumeAttemptUtc;
    private int _autoResumeAttempts;
    private bool _autoResumeInFlight;

    public MpvController(Action<string> log)
    {
        _log = log;
    }

    public void Play(AppSettings settings, string url, bool enqueue, string audioDeviceId)
    {
        if (!UrlValidator.IsValidYoutubeUrl(url))
        {
            throw new InvalidOperationException("Only YouTube and YouTube Music URLs are supported.");
        }

        LoadUrl(settings, url, enqueue, audioDeviceId, isAutoResume: false);
    }

    public void Stop(AppSettings settings, string audioDeviceId)
    {
        SuppressAutoResume(TimeSpan.FromSeconds(5), "stop command");
        EnsureMpv(settings, audioDeviceId);
        SendIpcRequest(settings, new object[] { "stop" });
        SendIpcRequest(settings, new object[] { "playlist-clear" });
    }

    public void Pause(AppSettings settings, string audioDeviceId)
    {
        SetProperty(settings, audioDeviceId, "pause", true);
    }

    public void Resume(AppSettings settings, string audioDeviceId)
    {
        SetProperty(settings, audioDeviceId, "pause", false);
    }

    public void Toggle(AppSettings settings, string audioDeviceId)
    {
        EnsureMpv(settings, audioDeviceId);
        SendIpcRequest(settings, new object[] { "cycle", "pause" });
    }

    public void Skip(AppSettings settings, string audioDeviceId)
    {
        SuppressAutoResume(TimeSpan.FromSeconds(5), "skip command");
        EnsureMpv(settings, audioDeviceId);
        SendIpcRequest(settings, new object[] { "playlist-next", "force" });
    }

    public void SetVolume(AppSettings settings, string audioDeviceId, double volume)
    {
        var normalized = Math.Max(0, Math.Min(100, volume));
        SetProperty(settings, audioDeviceId, "volume", normalized);
    }

    public void AdjustVolume(AppSettings settings, string audioDeviceId, double delta)
    {
        var status = TryGetStatus(settings, audioDeviceId);
        var currentVolume = status?.Volume ?? 50;
        SetVolume(settings, audioDeviceId, currentVolume + delta);
    }

    public void Mute(AppSettings settings, string audioDeviceId)
    {
        SetProperty(settings, audioDeviceId, "mute", true);
    }

    public void Unmute(AppSettings settings, string audioDeviceId)
    {
        SetProperty(settings, audioDeviceId, "mute", false);
    }

    public bool TryApplyAudioOutput(AppSettings settings, string audioDeviceId)
    {
        try
        {
            if (!TryConnect(settings))
            {
                return false;
            }

            SendIpcRequest(
                settings,
                new object[] { "set_property", "audio-device", audioDeviceId },
                ensureStarted: false);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Could not apply audio output immediately: {ex.Message}");
            return false;
        }
    }

    public bool QuitPlayer(AppSettings settings)
    {
        try
        {
            SuppressAutoResume(TimeSpan.FromSeconds(10), "quit command");
            _expectedProcessExit = true;
            SendIpcRequest(settings, new object[] { "quit" }, ensureStarted: false);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Failed to quit mpv: {ex.Message}");
            return false;
        }
    }

    public bool IsPlayerRunning(AppSettings settings)
    {
        return TryConnect(settings);
    }

    public void RecordPlaybackSnapshot(PlayerStatus? status)
    {
        if (status is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.MediaTitle))
        {
            _lastKnownMediaTitle = status.MediaTitle;
        }

        if (!string.IsNullOrWhiteSpace(status.Path))
        {
            _lastKnownPath = status.Path;
        }

        if (status.State is not (PlayerState.Playing or PlayerState.Paused))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.Path) && UrlValidator.IsValidYoutubeUrl(status.Path))
        {
            _resumeSourceUrl = status.Path;
        }

        if (status.TimePositionSeconds is not null)
        {
            _lastStablePositionSeconds = status.TimePositionSeconds;
        }

        if (status.DurationSeconds is not null)
        {
            _lastStableDurationSeconds = status.DurationSeconds;
        }

        if (_autoResumeInFlight && status.State == PlayerState.Playing)
        {
            _autoResumeInFlight = false;
            _autoResumeAttempts = 0;
            _log($"Failsafe resume succeeded ({DescribeCurrentTrack()}).");
        }
    }

    public bool TryFailsafeResume(
        AppSettings settings,
        string audioDeviceId,
        PlayerStatus? previousStatus,
        PlayerStatus? currentStatus)
    {
        if (previousStatus is null
            || currentStatus is null
            || previousStatus.State is not (PlayerState.Playing or PlayerState.Paused)
            || currentStatus.State != PlayerState.Idle
            || DateTime.UtcNow < _suppressAutoResumeUntilUtc)
        {
            return false;
        }

        var resumePosition = previousStatus.TimePositionSeconds ?? _lastStablePositionSeconds;
        if (resumePosition is null || resumePosition < 3)
        {
            return false;
        }

        var duration = previousStatus.DurationSeconds ?? _lastStableDurationSeconds;
        if (duration is not null && resumePosition >= Math.Max(0, duration.Value - 5))
        {
            return false;
        }

        var resumeUrl = ResolveResumeUrl(previousStatus);
        if (string.IsNullOrWhiteSpace(resumeUrl))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAutoResumeAttemptUtc > TimeSpan.FromMinutes(2))
        {
            _autoResumeAttempts = 0;
        }

        if (_autoResumeAttempts >= 3)
        {
            _log($"Failsafe resume skipped after repeated failures ({DescribeCurrentTrack()}).");
            return false;
        }

        try
        {
            _autoResumeAttempts++;
            _lastAutoResumeAttemptUtc = now;
            _autoResumeInFlight = true;
            _log($"Attempting failsafe resume at {resumePosition.Value:0.0}s for {resumeUrl} (attempt {_autoResumeAttempts}).");
            LoadUrl(settings, resumeUrl, enqueue: false, audioDeviceId, isAutoResume: true);
            Thread.Sleep(250);
            SendIpcRequest(settings, new object[] { "seek", resumePosition.Value, "absolute+exact" });
            SendIpcRequest(settings, new object[] { "set_property", "pause", false });
            return true;
        }
        catch (Exception ex)
        {
            _autoResumeInFlight = false;
            _log($"Failsafe resume attempt failed: {ex.Message}");
            return false;
        }
    }

    public ApiStatusSnapshot GetApiStatusSnapshot(AppSettings settings, string audioDeviceId)
    {
        if (!TryConnect(settings))
        {
            return new ApiStatusSnapshot(new Dictionary<string, object?>(), new List<string>(), "idle");
        }

        var props = new Dictionary<string, object?>();
        var errors = new List<string>();
        bool? pause = null;
        string? path = null;
        bool? coreIdle = null;

        AddProperty("pause", () => pause = GetProperty<bool?>(settings, "pause", ensureStarted: false));
        AddProperty("media-title", () => GetProperty<string?>(settings, "media-title", ensureStarted: false));
        AddProperty("path", () => path = GetProperty<string?>(settings, "path", ensureStarted: false));
        AddProperty("time-pos", () => GetProperty<double?>(settings, "time-pos", ensureStarted: false));
        AddProperty("duration", () => GetProperty<double?>(settings, "duration", ensureStarted: false));
        AddProperty("playlist-pos", () => GetProperty<int?>(settings, "playlist-pos", ensureStarted: false));
        AddProperty("playlist-count", () => GetProperty<int?>(settings, "playlist-count", ensureStarted: false));
        AddProperty("core-idle", () => coreIdle = GetProperty<bool?>(settings, "core-idle", ensureStarted: false));
        AddProperty("volume", () => GetProperty<double?>(settings, "volume", ensureStarted: false));
        AddProperty("mute", () => GetProperty<bool?>(settings, "mute", ensureStarted: false));

        var playerState = "unknown";
        if (!string.IsNullOrWhiteSpace(path))
        {
            playerState = pause == true ? "paused" : "playing";
        }
        else if (coreIdle == true)
        {
            playerState = "idle";
        }

        return new ApiStatusSnapshot(props, errors, playerState);

        void AddProperty<T>(string propertyName, Func<T?> getter)
        {
            try
            {
                props[propertyName] = getter();
            }
            catch (Exception ex)
            {
                props[propertyName] = null;
                errors.Add($"{propertyName}: {ex.Message}");
            }
        }
    }

    public (double? Volume, bool? Mute) GetVolumeSnapshot(AppSettings settings, string audioDeviceId)
    {
        if (!TryConnect(settings))
        {
            return (null, null);
        }

        return (
            GetProperty<double?>(settings, "volume", ensureStarted: false),
            GetProperty<bool?>(settings, "mute", ensureStarted: false));
    }

    public PlayerStatus? TryGetStatus(AppSettings settings, string audioDeviceId)
    {
        try
        {
            if (!TryConnect(settings))
            {
                return new PlayerStatus(PlayerState.Idle, null, null, null, null, null, null, null, null);
            }

            var pause = GetProperty<bool?>(settings, "pause", ensureStarted: false);
            var mediaTitle = GetProperty<string?>(settings, "media-title", ensureStarted: false);
            var path = GetProperty<string?>(settings, "path", ensureStarted: false);
            var timePos = GetProperty<double?>(settings, "time-pos", ensureStarted: false);
            var duration = GetProperty<double?>(settings, "duration", ensureStarted: false);
            var playlistPos = GetProperty<int?>(settings, "playlist-pos", ensureStarted: false);
            var playlistCount = GetProperty<int?>(settings, "playlist-count", ensureStarted: false);
            var coreIdle = GetProperty<bool?>(settings, "core-idle", ensureStarted: false);
            var volume = GetProperty<double?>(settings, "volume", ensureStarted: false);
            var mute = GetProperty<bool?>(settings, "mute", ensureStarted: false);

            var state = PlayerState.Unknown;
            if (!string.IsNullOrWhiteSpace(path))
            {
                state = pause == true ? PlayerState.Paused : PlayerState.Playing;
            }
            else if (coreIdle == true)
            {
                state = PlayerState.Idle;
            }

            return new PlayerStatus(
                state,
                mediaTitle,
                path,
                timePos,
                duration,
                playlistPos,
                playlistCount,
                volume,
                mute);
        }
        catch (Exception ex)
        {
            _log($"Status update failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopEventMonitor();

        if (_mpvProcess is { HasExited: false })
        {
            try
            {
                _expectedProcessExit = true;
                _mpvProcess.Kill(entireProcessTree: false);
            }
            catch
            {
            }
        }

        _mpvProcess?.Dispose();
    }

    private T? GetProperty<T>(AppSettings settings, string propertyName, bool ensureStarted = true)
    {
        var response = SendIpcRequest(settings, new object[] { "get_property", propertyName }, ensureStarted);
        if (!response.TryGetProperty("data", out var data))
        {
            return default;
        }

        return data.Deserialize<T>();
    }

    private void SetProperty(AppSettings settings, string audioDeviceId, string propertyName, object value)
    {
        EnsureMpv(settings, audioDeviceId);
        SendIpcRequest(settings, new object[] { "set_property", propertyName, value });
    }

    private void LoadUrl(AppSettings settings, string url, bool enqueue, string audioDeviceId, bool isAutoResume)
    {
        if (!enqueue)
        {
            _resumeSourceUrl = url;
            _lastStablePositionSeconds = isAutoResume ? _lastStablePositionSeconds : 0;
            _lastStableDurationSeconds = isAutoResume ? _lastStableDurationSeconds : null;
            _autoResumeInFlight = isAutoResume;
            if (!isAutoResume)
            {
                _autoResumeAttempts = 0;
            }

            SuppressAutoResume(
                isAutoResume ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5),
                isAutoResume ? "failsafe resume" : "replace play request");
        }

        EnsureMpv(settings, audioDeviceId);
        var mode = enqueue ? "append-play" : "replace";
        SendIpcRequest(settings, new object[] { "loadfile", url, mode });
    }

    private void EnsureMpv(AppSettings settings, string audioDeviceId)
    {
        lock (_gate)
        {
            if (_mpvProcess is { HasExited: true })
            {
                StopTrackingProcess();
                _mpvProcess.Dispose();
                _mpvProcess = null;
            }

            if (_mpvProcess is { HasExited: false } && TryConnect(settings))
            {
                return;
            }

            if (_mpvProcess is null && TryConnect(settings))
            {
                _log("Found an existing mpv instance on the WinTubeRelay IPC pipe. Restarting it to apply current launch settings.");
                TryQuitExistingPlayer(settings);
                WaitForPipeToClose(settings, TimeSpan.FromSeconds(2));
            }

            if (string.IsNullOrWhiteSpace(settings.MpvPath) || !File.Exists(settings.MpvPath))
            {
                throw new InvalidOperationException($"mpv was not found at '{settings.MpvPath}'.");
            }

            var arguments = new List<string>
            {
                "--idle=yes",
                "--no-video",
                "--force-window=no",
                "--audio-display=no",
                "--no-terminal",
                $"--input-ipc-server={BuildPipePath(settings)}",
                $"--audio-device={audioDeviceId}",
                "--ytdl=yes",
            };

            var ytdlOptions = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.YtDlpPath))
            {
                if (!File.Exists(settings.YtDlpPath))
                {
                    _log($"yt-dlp not found at '{settings.YtDlpPath}', mpv will rely on PATH.");
                }
                else
                {
                    ytdlOptions.Add($"ytdl_path={settings.YtDlpPath}");
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.YtDlpBrowser))
            {
                ytdlOptions.Add($"cookies_from_browser={settings.YtDlpBrowser}");
            }

            if (ytdlOptions.Count > 0)
            {
                arguments.Add($"--script-opts=ytdl_hook-{string.Join(';', ytdlOptions)}");
            }

            if (!string.IsNullOrWhiteSpace(settings.MpvLogFilePath))
            {
                arguments.Add($"--log-file={settings.MpvLogFilePath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.MpvPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = false,
                RedirectStandardOutput = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _log($"Starting mpv with audio device {audioDeviceId}.");
            StopTrackingProcess();
            _mpvProcess?.Dispose();
            _mpvProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start mpv.");
            _expectedProcessExit = false;
            _lastKnownMediaTitle = null;
            _lastKnownPath = null;
            _mpvProcess.EnableRaisingEvents = true;
            _mpvProcess.Exited += OnMpvProcessExited;

            WaitForPipe(settings, TimeSpan.FromSeconds(3));
            StartEventMonitor(settings);
        }
    }

    private void TryQuitExistingPlayer(AppSettings settings)
    {
        try
        {
            _expectedProcessExit = true;
            SendIpcRequest(settings, new object[] { "quit" }, ensureStarted: false, allowRetry: false);
        }
        catch (Exception ex)
        {
            _log($"Failed to quit existing mpv instance cleanly: {ex.Message}");
        }
    }

    private JsonElement SendIpcRequest(
        AppSettings settings,
        object[] command,
        bool ensureStarted = true,
        bool allowRetry = true)
    {
        if (ensureStarted)
        {
            EnsureMpv(settings, "auto");
        }

        var payload = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["request_id"] = Interlocked.Increment(ref _requestId),
        };

        try
        {
            using var client = CreatePipeClient(settings);
            client.Connect(1500);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true);

            writer.WriteLine(JsonSerializer.Serialize(payload));
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new IOException("mpv IPC returned an empty response.");
            }

            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (allowRetry)
        {
            _log($"IPC request failed for {DescribeCommand(command)}, retrying once: {ex.Message}");
            return SendIpcRequest(settings, command, ensureStarted, allowRetry: false);
        }
    }

    private bool TryConnect(AppSettings settings)
    {
        try
        {
            using var client = CreatePipeClient(settings);
            client.Connect(100);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private void WaitForPipe(AppSettings settings, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = CreatePipeClient(settings);
                client.Connect(150);
                if (client.IsConnected)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException($"mpv IPC pipe did not become ready. {lastError?.Message}");
    }

    private void WaitForPipeToClose(AppSettings settings, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!TryConnect(settings))
            {
                return;
            }

            Thread.Sleep(50);
        }

        _log("Timed out waiting for the previous mpv IPC pipe to close; continuing with a fresh launch attempt.");
    }

    private static NamedPipeClientStream CreatePipeClient(AppSettings settings)
    {
        return new NamedPipeClientStream(
            ".",
            NormalizePipeName(settings.MpvPipeName),
            PipeDirection.InOut,
            PipeOptions.None);
    }

    private void StartEventMonitor(AppSettings settings)
    {
        StopEventMonitor();

        _eventMonitorCts = new CancellationTokenSource();
        _eventMonitorTask = Task.Run(() => MonitorEventsAsync(settings, _eventMonitorCts.Token));
    }

    private void StopEventMonitor()
    {
        if (_eventMonitorCts is null)
        {
            return;
        }

        try
        {
            _eventMonitorCts.Cancel();
            _eventMonitorTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        finally
        {
            _eventMonitorTask = null;
            _eventMonitorCts.Dispose();
            _eventMonitorCts = null;
        }
    }

    private async Task MonitorEventsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreatePipeClient(settings);
            await client.ConnectAsync(1500, cancellationToken);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true);

            await SendMonitorCommandAsync(writer, new object[] { "request_log_messages", "warn" }, cancellationToken);
            await SendMonitorCommandAsync(writer, new object[] { "observe_property", 1, "media-title" }, cancellationToken);
            await SendMonitorCommandAsync(writer, new object[] { "observe_property", 2, "path" }, cancellationToken);
            await SendMonitorCommandAsync(writer, new object[] { "observe_property", 3, "pause" }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _log("mpv event monitor disconnected from IPC.");
                    }

                    break;
                }

                using var document = JsonDocument.Parse(line);
                HandleMonitorMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log($"mpv event monitor failed: {ex.Message}");
            }
        }
    }

    private async Task SendMonitorCommandAsync(StreamWriter writer, object[] command, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["request_id"] = Interlocked.Increment(ref _requestId),
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), cancellationToken);
    }

    private void HandleMonitorMessage(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var eventNameElement))
        {
            return;
        }

        var eventName = eventNameElement.GetString();
        switch (eventName)
        {
            case "start-file":
                _log($"mpv event: start-file ({DescribeCurrentTrack()})");
                break;
            case "playback-restart":
                _log($"mpv event: playback-restart ({DescribeCurrentTrack()})");
                break;
            case "end-file":
                LogEndFileEvent(message);
                break;
            case "log-message":
                LogMpvMessage(message);
                break;
            case "property-change":
                HandlePropertyChange(message);
                break;
        }
    }

    private void HandlePropertyChange(JsonElement message)
    {
        if (!message.TryGetProperty("name", out var nameElement))
        {
            return;
        }

        var propertyName = nameElement.GetString();
        switch (propertyName)
        {
            case "media-title":
                _lastKnownMediaTitle = ReadOptionalString(message, "data");
                break;
            case "path":
                _lastKnownPath = ReadOptionalString(message, "data");
                break;
            case "pause":
                if (message.TryGetProperty("data", out var pauseElement) && pauseElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _log($"mpv event: {(pauseElement.GetBoolean() ? "paused" : "resumed")} ({DescribeCurrentTrack()})");
                }

                break;
        }
    }

    private void LogEndFileEvent(JsonElement message)
    {
        var reason = ReadOptionalString(message, "reason") ?? "unknown";
        var error = ReadOptionalString(message, "error");
        var playlistEntryId = ReadOptionalInt(message, "playlist_entry_id");
        var details = new List<string>
        {
            $"reason={reason}",
            $"track={DescribeCurrentTrack()}",
        };

        if (!string.IsNullOrWhiteSpace(error))
        {
            details.Add($"error={error}");
        }

        if (playlistEntryId is not null)
        {
            details.Add($"playlist_entry_id={playlistEntryId}");
        }

        _log($"mpv event: end-file ({string.Join(", ", details)})");
    }

    private void LogMpvMessage(JsonElement message)
    {
        var prefix = ReadOptionalString(message, "prefix") ?? "mpv";
        var level = ReadOptionalString(message, "level") ?? "info";
        var text = ReadOptionalString(message, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _log($"mpv {level} [{prefix}]: {text.Trim()}");
    }

    private void OnMpvProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        var exitCode = SafeGetExitCode(process);
        var expected = _expectedProcessExit;
        _log($"mpv process exited with code {exitCode} ({(expected ? "expected" : "unexpected")}) ({DescribeCurrentTrack()})");
        StopEventMonitor();
    }

    private void StopTrackingProcess()
    {
        StopEventMonitor();

        if (_mpvProcess is null)
        {
            return;
        }

        _mpvProcess.Exited -= OnMpvProcessExited;
        _expectedProcessExit = false;
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return int.MinValue;
        }
    }

    private string DescribeCurrentTrack()
    {
        return !string.IsNullOrWhiteSpace(_lastKnownMediaTitle)
            ? _lastKnownMediaTitle!
            : !string.IsNullOrWhiteSpace(_lastKnownPath)
                ? _lastKnownPath!
                : "unknown";
    }

    private string? ResolveResumeUrl(PlayerStatus previousStatus)
    {
        if (!string.IsNullOrWhiteSpace(previousStatus.Path) && UrlValidator.IsValidYoutubeUrl(previousStatus.Path))
        {
            return previousStatus.Path;
        }

        if (!string.IsNullOrWhiteSpace(_lastKnownPath) && UrlValidator.IsValidYoutubeUrl(_lastKnownPath))
        {
            return _lastKnownPath;
        }

        return !string.IsNullOrWhiteSpace(_resumeSourceUrl) && UrlValidator.IsValidYoutubeUrl(_resumeSourceUrl)
            ? _resumeSourceUrl
            : null;
    }

    private static string DescribeCommand(object[] command)
    {
        return command.Length == 0 ? "(empty command)" : string.Join(" ", command.Select(item => item?.ToString() ?? "null"));
    }

    private void SuppressAutoResume(TimeSpan duration, string reason)
    {
        _suppressAutoResumeUntilUtc = DateTime.UtcNow + duration;
        _log($"Auto-resume suppressed for {duration.TotalSeconds:0}s ({reason}).");
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString(),
        };
    }

    private static int? ReadOptionalInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string BuildPipePath(AppSettings settings)
    {
        return $@"\\.\pipe\{NormalizePipeName(settings.MpvPipeName)}";
    }

    private static string NormalizePipeName(string pipeName)
    {
        const string prefix = @"\\.\pipe\";
        var normalized = pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
        return string.IsNullOrWhiteSpace(normalized) ? "wintuberelay-mpv" : normalized;
    }
}
