using WinTubeRelay.Tray;

namespace WinTubeRelay.PlaybackTests;

internal static class MpvLifecycleTests
{
    public const string TrackUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    public static Task PausedLoadedTrackIsNotIdleWhenCoreIdle()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "true";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "false";
        pipe.PropertyJson["path"] = JsonString(TrackUrl);
        pipe.PropertyJson["media-title"] = JsonString("Paused at 2962");
        pipe.PropertyJson["time-pos"] = "2962.0";
        pipe.PropertyJson["duration"] = "5000.0";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "1";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";

        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        var snapshot = controller.GetApiStatusSnapshot(settings, "auto");

        Expect(
            (status?.State == PlayerState.Paused, $"status={status?.State.ToString() ?? "null"}"),
            (snapshot.PlayerState == "paused", $"api={snapshot.PlayerState}"),
            (status?.TimePositionSeconds == 2962, $"position={status?.TimePositionSeconds?.ToString() ?? "null"}"),
            (string.Equals(status?.Path, TrackUrl, StringComparison.Ordinal), $"path={status?.Path ?? "null"}"));
        return Task.CompletedTask;
    }

    public static Task BufferingCoreIdleIsNotIdle()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "false";
        pipe.PropertyJson["path"] = JsonString(TrackUrl);
        pipe.PropertyJson["media-title"] = JsonString("Buffering");
        pipe.PropertyJson["time-pos"] = "120.0";
        pipe.PropertyJson["duration"] = "5000.0";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "2";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";

        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        var snapshot = controller.GetApiStatusSnapshot(settings, "auto");
        Expect(
            (status?.State is PlayerState.Playing or PlayerState.Loading, $"status={status?.State.ToString() ?? "null"}"),
            (snapshot.PlayerState is "playing" or "loading", $"api={snapshot.PlayerState}"));
        return Task.CompletedTask;
    }

    public static Task ReinitCoreIdleWithoutIdleActiveIsNotIdle()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "false";
        pipe.PropertyJson["path"] = "null";
        pipe.PropertyJson["media-title"] = "null";
        pipe.PropertyJson["time-pos"] = "null";
        pipe.PropertyJson["duration"] = "null";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "2";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";

        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        var snapshot = controller.GetApiStatusSnapshot(settings, "auto");
        Expect(
            (status?.State is PlayerState.Playing or PlayerState.Loading, $"status={status?.State.ToString() ?? "null"}"),
            (snapshot.PlayerState is "playing" or "loading", $"api={snapshot.PlayerState}"));
        return Task.CompletedTask;
    }

    public static Task TrueIdleWhenIdleActive()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "true";
        pipe.PropertyJson["path"] = "null";
        pipe.PropertyJson["media-title"] = "null";
        pipe.PropertyJson["time-pos"] = "null";
        pipe.PropertyJson["duration"] = "null";
        pipe.PropertyJson["playlist-pos"] = "-1";
        pipe.PropertyJson["playlist-count"] = "0";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";

        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        var snapshot = controller.GetApiStatusSnapshot(settings, "auto");
        Expect(
            (status?.State == PlayerState.Idle, $"status={status?.State.ToString() ?? "null"}"),
            (snapshot.PlayerState == "idle", $"api={snapshot.PlayerState}"));
        return Task.CompletedTask;
    }

    public static Task IpcReplySkipsEventsAndForeignRequestIds()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["path"] = JsonString(TrackUrl);
        pipe.PropertyJson["core-idle"] = "false";
        pipe.PropertyJson["idle-active"] = "false";
        pipe.PropertyJson["media-title"] = JsonString("Playing");
        pipe.PropertyJson["time-pos"] = "10";
        pipe.PropertyJson["duration"] = "100";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "1";
        pipe.PropertyJson["volume"] = "40";
        pipe.PropertyJson["mute"] = "false";
        pipe.LinesBeforeResponse = line =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var command = document.RootElement.GetProperty("command");
            if (command.GetArrayLength() < 2 || command[1].GetString() != "pause")
            {
                return Array.Empty<string>();
            }

            return new[]
            {
                "{\"event\":\"property-change\",\"name\":\"pause\",\"data\":true}",
                "{\"data\":true,\"request_id\":999999,\"error\":\"success\"}",
            };
        };

        var logs = new List<string>();
        using var controller = new MpvController(logs.Add);
        var settings = IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        Expect((status?.State == PlayerState.Playing, $"event or foreign request was treated as the pause reply; status={status?.State.ToString() ?? "null"}; log={string.Join(" || ", logs)}"));
        return Task.CompletedTask;
    }

    public static Task IpcErrorResponseIsNotSuccess()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.ResponseOverride = line =>
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var requestId = document.RootElement.GetProperty("request_id").GetInt32();
            return $"{{\"error\":\"property unavailable\",\"request_id\":{requestId}}}";
        };
        using var controller = new MpvController(_ => { });
        var applied = controller.TryApplyAudioOutput(IsolatedSettings(pipe.PipeName), "wasapi/{11111111-1111-1111-1111-111111111111}");
        Expect((applied == false, "error IPC reply was treated as a successful audio apply"));
        return Task.CompletedTask;
    }

    public static Task ExistingPipeIsAdoptedWithoutRestartOrDuplicateLaunch()
    {
        var pipeName = $"wintuberelay-test-{Guid.NewGuid():N}";
        using var process = FakeMpvProcess.StartLeftover(pipeName);
        try
        {
            FakeMpvProcess.WaitUntilReady(pipeName, TimeSpan.FromSeconds(8));
            using var controller = new MpvController(_ => { });
            var settings = IsolatedSettings(pipeName);
            settings.MpvPath = FakeMpvProcess.HostPath();
            controller.Pause(settings, "auto");
            var log = FakeMpvProcess.CommandLog(pipeName);
            var duplicate = Path.Combine(FakeMpvProcess.SessionDirectory(pipeName), "duplicate");
            Expect(
                (FakeMpvProcess.LaunchCount(pipeName) == 1, $"launches={FakeMpvProcess.LaunchCount(pipeName)}"),
                (!log.Contains("\"quit\"", StringComparison.Ordinal), $"existing player was quit: {log}"),
                (!File.Exists(duplicate), "a second mpv was launched on the same pipe"),
                (!process.HasExited, "adopted mpv process exited"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        return Task.CompletedTask;
    }

    public static Task MutatingIpcCommandIsNotRetriedAfterWrite()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.CloseWithoutResponse = true;
        using var controller = new MpvController(_ => { });
        try
        {
            controller.Skip(IsolatedSettings(pipe.PipeName), "auto");
        }
        catch (Exception)
        {
        }

        var skips = pipe.CommandCount("playlist-next");
        Expect((skips == 1, $"playlist-next sent {skips} times"));
        return Task.CompletedTask;
    }

    public static Task IpcReadHasDeadline()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.HangAfterCommand = true;
        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var finished = Task.Run(() => controller.TryGetStatus(settings, "auto"));
        var completed = finished.Wait(TimeSpan.FromSeconds(8));
        Expect((completed, "status IPC read did not return within 8s"));
        return Task.CompletedTask;
    }

    public static Task FailsafeDoesNotReplaceRemainingQueue()
    {
        using var pipe = FilledPipe(idle: true, playlistCount: 3);
        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var previous = new PlayerStatus(PlayerState.Playing, "Queued", TrackUrl, 2962, 5000, 0, 3, 80, false);
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        Expect(
            (current?.PlaylistCount == 3, $"playlist={current?.PlaylistCount?.ToString() ?? "null"}"),
            (resumed == false, "failsafe replaced a playlist that was still present"),
            (!pipe.CommandsContain("loadfile"), $"commands={string.Join(" | ", pipe.Commands)}"));
        return Task.CompletedTask;
    }

    public static Task FailsafeDoesNotSeekBeforeLoadReady()
    {
        using var pipe = FilledPipe(idle: true, playlistCount: 0);
        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var previous = new PlayerStatus(PlayerState.Playing, "Track", TrackUrl, 2962, 5000, 0, 1, 80, false);
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        var loadfile = pipe.Commands.FirstOrDefault(command => command.Contains("loadfile", StringComparison.Ordinal));
        Expect(
            (current?.State == PlayerState.Idle, $"status={current?.State.ToString() ?? "null"}"),
            (resumed, "genuine idle drop was not resumed"),
            (loadfile is not null && loadfile.Contains("2962", StringComparison.Ordinal), $"loadfile did not carry the resume position: {loadfile ?? "(none)"}"),
            (!pipe.CommandsContain("\"seek\""), $"seek was sent before playback-restart: {string.Join(" | ", pipe.Commands)}"));
        return Task.CompletedTask;
    }

    public static Task PauseSuppressesFailsafeWhenStatusBlipsIdle()
    {
        using var pipe = FilledPipe(idle: false, playlistCount: 1);
        using var controller = new MpvController(_ => { });
        var settings = IsolatedSettings(pipe.PipeName);
        var previous = controller.TryGetStatus(settings, "auto");
        controller.RecordPlaybackSnapshot(previous);
        controller.Pause(settings, "auto");
        pipe.PropertyJson["path"] = "null";
        pipe.PropertyJson["idle-active"] = "true";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["pause"] = "true";
        pipe.PropertyJson["playlist-count"] = "0";
        pipe.PropertyJson["playlist-pos"] = "-1";
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        Expect(
            (previous?.State == PlayerState.Playing, $"previous={previous?.State.ToString() ?? "null"}"),
            (resumed == false, "pause did not suppress failsafe"),
            (!pipe.CommandsContain("loadfile"), $"commands={string.Join(" | ", pipe.Commands)}"));
        return Task.CompletedTask;
    }

    private static ScriptedMpvPipe FilledPipe(bool idle, int playlistCount)
    {
        var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = idle ? "true" : "false";
        pipe.PropertyJson["path"] = idle ? "null" : JsonString(TrackUrl);
        pipe.PropertyJson["media-title"] = idle ? "null" : JsonString("Track");
        pipe.PropertyJson["time-pos"] = idle ? "null" : "2962";
        pipe.PropertyJson["duration"] = idle ? "null" : "5000";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = playlistCount.ToString();
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";
        return pipe;
    }

    public static AppSettings IsolatedSettings(string pipeName)
    {
        return new AppSettings
        {
            MpvPath = string.Empty,
            MpvPipeName = pipeName,
            MpvLogFilePath = null,
            YtDlpPath = null,
            YtDlpBrowser = null,
        };
    }

    public static string JsonString(string value)
    {
        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    public static void Expect(params (bool Condition, string Message)[] checks)
    {
        var failures = checks.Where(check => !check.Condition).Select(check => check.Message).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }
}
