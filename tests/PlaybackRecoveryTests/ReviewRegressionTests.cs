using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinTubeRelay.Tray;

namespace WinTubeRelay.PlaybackTests;

internal static class ReviewRegressionTests
{
    private const string TrackUrl = MpvLifecycleTests.TrackUrl;

    public static Task ExclusivePrepareDoesNotDeadlockUiCommand()
    {
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings($"wintuberelay-test-{Guid.NewGuid():N}");
        using var ui = new DedicatedUiThread();
        var entered = new ManualResetEventSlim(false);
        var uiStarted = new ManualResetEventSlim(false);
        var exclusive = Task.Run(() =>
        {
            controller.InvokeExclusive(() =>
            {
                entered.Set();
                if (!uiStarted.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("UI player command did not start.");
                }

                ui.Send(() => { });
            });
        });
        var pause = Task.Run(() =>
        {
            if (!entered.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("exclusive action did not start");
            }

            ui.Post(() =>
            {
                uiStarted.Set();
                try
                {
                    controller.Pause(settings, "auto");
                }
                catch (InvalidOperationException)
                {
                    // Missing mpv is fine. The failure mode is the UI thread staying blocked on the player gate.
                }
            });
        });

        var completed = Task.WaitAll(new[] { exclusive, pause }, TimeSpan.FromSeconds(4));
        MpvLifecycleTests.Expect(
            (completed && exclusive.IsCompleted && pause.IsCompleted, "exclusive prepare held the player gate across a UI send while a UI player command waited"),
            (!exclusive.IsFaulted, exclusive.IsFaulted ? exclusive.Exception?.GetBaseException().Message ?? "exclusive failed" : "ok"));
        return Task.CompletedTask;
    }

    public static Task ExclusiveActionsDoNotInterleave()
    {
        using var controller = new MpvController(_ => { });
        var order = new List<string>();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var first = Task.Run(() => controller.InvokeExclusive(() =>
        {
            lock (order)
            {
                order.Add("enter");
            }

            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            lock (order)
            {
                order.Add("exit");
            }
        }));
        if (!entered.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("first exclusive action did not enter");
        }

        var second = Task.Run(() => controller.InvokeExclusive(() =>
        {
            lock (order)
            {
                order.Add("second");
            }
        }));
        var overlapped = second.Wait(TimeSpan.FromMilliseconds(200));
        release.Set();
        second.Wait(TimeSpan.FromSeconds(2));
        first.Wait(TimeSpan.FromSeconds(2));
        string seen;
        lock (order)
        {
            seen = string.Join(",", order);
        }

        MpvLifecycleTests.Expect(
            (!overlapped, "second exclusive action ran while the first still held the operation"),
            (seen == "enter,exit,second", $"order={seen}"));
        return Task.CompletedTask;
    }

    public static async Task FavoriteAndRecentUpdatesShareSettingsLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinTubeRelayTests", $"settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(directory);
        var settings = store.Load();
        settings.ApiPort = FreeTcpPort();
        settings.ApiKey = null;
        var recentUrl = "https://www.youtube.com/watch?v=recentlockcheck1";
        settings.RecentUrls.Add(new SavedUrlEntry { Name = "Recent", Url = recentUrl, LastPlayedAtUtc = DateTime.UtcNow });
        store.Save(settings);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        store.HoldWhileLockedForTests = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException("settings lock hold was not released");
            }
        };

        await using var server = new WebServerService(
            () => settings,
            () => "auto",
            new MpvController(_ => { }),
            () => store.Save(settings),
            _ => { },
            settingsStore: store);
        await server.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.ApiPort}"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        try
        {
            var favoriteUrl = "https://www.youtube.com/watch?v=favoritelockcheck1";
            var first = http.PostAsync("/favorites", JsonFavorite(favoriteUrl, "Locked favorite"));
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
            {
                var detail = first.IsCompleted ? await first.Result.Content.ReadAsStringAsync() : "request still running";
                throw new TimeoutException($"favorite save did not enter the settings lock: {detail}");
            }

            var second = http.PostAsync("/recent/clear", new StringContent("{}", Encoding.UTF8, "application/json"));
            await Task.Delay(400);
            var clearedOutsideLock = settings.RecentUrls.Count == 0;
            release.Set();
            var firstResponse = await first;
            var secondResponse = await second;
            var firstText = await firstResponse.Content.ReadAsStringAsync();
            var secondText = await secondResponse.Content.ReadAsStringAsync();
            var loaded = store.Load();
            MpvLifecycleTests.Expect(
                (!clearedOutsideLock, "recent clear mutated the list while another save held the settings lock"),
                (firstResponse.StatusCode == HttpStatusCode.OK, $"favorite HTTP {(int)firstResponse.StatusCode}: {firstText}"),
                (secondResponse.StatusCode == HttpStatusCode.OK, $"recent HTTP {(int)secondResponse.StatusCode}: {secondText}"),
                (loaded.Favorites.Any(item => item.Url == favoriteUrl), "favorite was lost"),
                (loaded.RecentUrls.Count == 0, $"recent remained={loaded.RecentUrls.Count}"),
                (File.Exists(store.GetSettingsPath() + ".bak"), "atomic backup missing"));
        }
        finally
        {
            release.Set();
            store.HoldWhileLockedForTests = null;
            http.Dispose();
            await server.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    public static async Task MonitorReconnectsAfterOneFailedConnect()
    {
        using var pipe = ScriptedMpvPipe.Start();
        var logs = new List<string>();
        var logGate = new object();
        using var controller = new MpvController(message =>
        {
            lock (logGate)
            {
                logs.Add(message);
            }
        });
        pipe.EndFileOnMonitorSession = 2;
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        controller.Pause(settings, "auto");
        var sawMonitor = await WaitUntilAsync(() => pipe.CommandsContain("observe_property"), TimeSpan.FromSeconds(5));
        pipe.FailNextAcceptedConnection();
        pipe.DropMonitor();
        var sawEndFile = await WaitUntilAsync(() =>
        {
            lock (logGate)
            {
                return logs.Exists(line => line.Contains("end-file", StringComparison.Ordinal));
            }
        }, TimeSpan.FromSeconds(8));
        MpvLifecycleTests.Expect(
            (pipe.PipeName.StartsWith("wintuberelay-test-", StringComparison.Ordinal), $"pipe={pipe.PipeName}"),
            (sawMonitor, $"event monitor did not connect; commands={string.Join(" | ", pipe.Commands)} log={Dump(logs, logGate)}"),
            (sawEndFile, $"end-file was not observed after a failed reconnect; dropped={pipe.DroppedMonitors} dropError={pipe.LastDropError} log={Dump(logs, logGate)}"),
            (Dump(logs, logGate).Contains("Pipe is broken", StringComparison.Ordinal) || Dump(logs, logGate).Contains("monitor failed", StringComparison.Ordinal), "reconnect did not fail once before the next accept"));
    }

    public static Task MutatingIpcIsNotRetriedWhenWriteThrows()
    {
        using var pipe = ScriptedMpvPipe.Start();
        using var controller = new MpvController(_ => { });
        var writes = 0;
        MpvController.IpcStreamWrapperForTests = stream => new ThrowOnWriteStream(stream, () => Interlocked.Increment(ref writes));
        try
        {
            var applied = controller.TryApplyAudioOutput(
                MpvLifecycleTests.IsolatedSettings(pipe.PipeName),
                "wasapi/{11111111-1111-1111-1111-111111111111}");
            MpvLifecycleTests.Expect(
                (writes == 1, $"mutating IPC write was attempted {writes} times after the connection succeeded"),
                (!applied, "a failed write was reported as a successful audio apply"));
        }
        finally
        {
            MpvController.IpcStreamWrapperForTests = null;
        }

        return Task.CompletedTask;
    }

    public static Task IdleActiveWinsOverStalePath()
    {
        using var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "true";
        pipe.PropertyJson["path"] = MpvLifecycleTests.JsonString(TrackUrl);
        pipe.PropertyJson["media-title"] = MpvLifecycleTests.JsonString("Stale title");
        pipe.PropertyJson["time-pos"] = "2962";
        pipe.PropertyJson["duration"] = "5000";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "1";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var status = controller.TryGetStatus(settings, "auto");
        var snapshot = controller.GetApiStatusSnapshot(settings, "auto");
        MpvLifecycleTests.Expect(
            (status?.State == PlayerState.Idle, $"status={status?.State.ToString() ?? "null"}"),
            (snapshot.PlayerState == "idle", $"api={snapshot.PlayerState}"));
        return Task.CompletedTask;
    }

    public static Task TogglePauseSuppressesFailsafe()
    {
        using var pipe = PlayingPipe();
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var previous = controller.TryGetStatus(settings, "auto");
        controller.RecordPlaybackSnapshot(previous);
        controller.Toggle(settings, "auto");
        pipe.PropertyJson["path"] = "null";
        pipe.PropertyJson["idle-active"] = "true";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["pause"] = "true";
        pipe.PropertyJson["playlist-count"] = "0";
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        MpvLifecycleTests.Expect(
            (previous?.State == PlayerState.Playing, $"previous={previous?.State.ToString() ?? "null"}"),
            (!pipe.CommandsContain("\"cycle\""), $"toggle cycled pause: {string.Join(" | ", pipe.Commands)}"),
            (pipe.CommandsContain("set_property") && pipe.CommandsContain("\"pause\""), $"toggle did not pause from the actual playing state: {string.Join(" | ", pipe.Commands)}"),
            (!resumed, "toggle-to-pause did not suppress failsafe"),
            (!pipe.CommandsContain("loadfile"), "failsafe replaced playback after toggle-to-pause"));
        return Task.CompletedTask;
    }

    public static Task ToggleKnownPausedResumes()
    {
        using var pipe = PlayingPipe();
        pipe.PropertyJson["pause"] = "true";
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var before = controller.TryGetStatus(settings, "auto");
        controller.Toggle(settings, "auto");
        MpvLifecycleTests.Expect(
            (before?.State == PlayerState.Paused, $"before={before?.State.ToString() ?? "null"}"),
            (!pipe.CommandsContain("\"cycle\""), $"toggle cycled pause: {string.Join(" | ", pipe.Commands)}"),
            (pipe.Commands.Any(command => command.Contains("set_property", StringComparison.Ordinal) && command.Contains("false", StringComparison.Ordinal)),
                $"known paused toggle did not resume: {string.Join(" | ", pipe.Commands)}"));
        return Task.CompletedTask;
    }

    public static Task PendingResumeClearedAfterLoadSend()
    {
        using var pipe = IdlePipe(playlistCount: "0");
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var previous = new PlayerStatus(PlayerState.Playing, "Track", TrackUrl, 2962, 5000, 0, 1, 80, false);
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        MpvLifecycleTests.Expect(
            (resumed, "failsafe did not send the resume load"),
            (!controller.HasPendingResumeForTests, "pending resume position remained after the load was sent"));
        return Task.CompletedTask;
    }

    public static Task PendingResumeClearedAfterLoadFailure()
    {
        using var pipe = IdlePipe(playlistCount: "0");
        pipe.ResponseOverride = line =>
        {
            if (!line.Contains("loadfile", StringComparison.Ordinal))
            {
                return null;
            }

            using var document = JsonDocument.Parse(line);
            var requestId = document.RootElement.GetProperty("request_id").GetInt32();
            return $"{{\"error\":\"invalid parameter\",\"request_id\":{requestId}}}";
        };
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var previous = new PlayerStatus(PlayerState.Playing, "Track", TrackUrl, 2962, 5000, 0, 1, 80, false);
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        MpvLifecycleTests.Expect(
            (!resumed, "failed failsafe load was reported as success"),
            (!controller.HasPendingResumeForTests, "pending resume position remained after the load failed"));
        return Task.CompletedTask;
    }

    public static Task NullPlaylistCountSuppressesFailsafe()
    {
        using var pipe = IdlePipe(playlistCount: "null");
        using var controller = new MpvController(_ => { });
        var settings = MpvLifecycleTests.IsolatedSettings(pipe.PipeName);
        var previous = new PlayerStatus(PlayerState.Playing, "Track", TrackUrl, 2962, 5000, 0, 1, 80, false);
        var current = controller.TryGetStatus(settings, "auto");
        var resumed = controller.TryFailsafeResume(settings, "auto", previous, current);
        MpvLifecycleTests.Expect(
            (current?.State == PlayerState.Idle, $"status={current?.State.ToString() ?? "null"}"),
            (current?.PlaylistCount is null, $"playlist={current?.PlaylistCount?.ToString() ?? "null"}"),
            (!resumed, "failsafe replaced playback when playlist-count was unknown"),
            (!pipe.CommandsContain("loadfile"), $"commands={string.Join(" | ", pipe.Commands)}"));
        return Task.CompletedTask;
    }

    public static Task AbandonedMutexIsAcquiredAndReleased()
    {
        var name = $@"Local\WinTubeRelay.Tests.{Guid.NewGuid():N}";
        var started = new ManualResetEventSlim(false);
        var abandon = new ManualResetEventSlim(false);
        var crashed = new Thread(() =>
        {
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (!createdNew)
            {
                throw new InvalidOperationException("crash fixture did not create the mutex");
            }

            started.Set();
            abandon.Wait();
            mutex.Dispose();
        });
        crashed.Start();
        if (!started.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("abandoned-mutex fixture did not start");
        }

        abandon.Set();
        crashed.Join();
        var acquired = SingleInstance.TryAcquire(name, out var instance);
        try
        {
            MpvLifecycleTests.Expect((acquired && instance is not null, "abandoned mutex was not treated as acquired"));
        }
        finally
        {
            instance?.Dispose();
        }

        var afterRelease = SingleInstance.TryAcquire(name, out var released);
        released?.Dispose();
        MpvLifecycleTests.Expect((afterRelease, "owned mutex was not released on disposal"));
        return Task.CompletedTask;
    }

    public static Task LateWakeSurvivesConsumeCompleteGap()
    {
        var coalescer = new RecoveryWakeCoalescer();
        var started = coalescer.TryBegin(wakeDisplay: false);
        var early = coalescer.ConsumeWake();
        var coalesced = coalescer.TryBegin(wakeDisplay: true);
        var lateKept = coalescer.Complete();
        MpvLifecycleTests.Expect(
            (started, "recovery did not start"),
            (!early, "consume saw a wake that had not been requested"),
            (!coalesced, "late wake started a second recovery"),
            (lateKept, "wake requested between consume and complete was dropped"));
        return Task.CompletedTask;
    }

    public static Task WebUiGetHeadersExecutes()
    {
        var node = FindNode();
        if (node is null)
        {
            throw new InvalidOperationException("node was not installed; getHeaders was not executed");
        }

        var htmlPath = FindProductionIndex();
        var scriptPath = Path.Combine(Path.GetTempPath(), "WinTubeRelayTests", $"get-headers-{Guid.NewGuid():N}.js");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, """
            const fs = require('fs');
            const html = fs.readFileSync(process.argv[2], 'utf8');
            const start = html.indexOf('function getHeaders()');
            const end = html.indexOf('function saveApiKey()');
            if (start < 0 || end <= start) {
              console.error('getHeaders function was not found');
              process.exit(2);
            }
            // Executes only the repository's own getHeaders against a fixture key. The body is sliced from static/index.html, not from request or user input.
            const source = html.slice(start, end);
            function run(key) {
              const localStorage = { getItem() { return key; } };
              return new Function('localStorage', source + '\nreturn getHeaders();')(localStorage);
            }
            const withKey = run('fixture-key');
            const withoutKey = run('   ');
            if (withKey['Content-Type'] !== 'application/json') process.exit(3);
            if (withKey['x-api-key'] !== 'fixture-key') process.exit(4);
            if (Object.prototype.hasOwnProperty.call(withoutKey, 'x-api-key')) process.exit(5);
            console.log('GET_HEADERS_OK');
            """);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = node,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add(htmlPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start");
            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("node getHeaders execution timed out");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            MpvLifecycleTests.Expect(
                (process.ExitCode == 0 && stdout.Contains("GET_HEADERS_OK", StringComparison.Ordinal),
                    $"getHeaders execution failed exit={process.ExitCode} stdout={stdout} stderr={stderr}"));
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
            }
        }

        return Task.CompletedTask;
    }

    private static ScriptedMpvPipe PlayingPipe()
    {
        var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "false";
        pipe.PropertyJson["idle-active"] = "false";
        pipe.PropertyJson["path"] = MpvLifecycleTests.JsonString(TrackUrl);
        pipe.PropertyJson["media-title"] = MpvLifecycleTests.JsonString("Track");
        pipe.PropertyJson["time-pos"] = "120";
        pipe.PropertyJson["duration"] = "5000";
        pipe.PropertyJson["playlist-pos"] = "0";
        pipe.PropertyJson["playlist-count"] = "1";
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";
        return pipe;
    }

    private static ScriptedMpvPipe IdlePipe(string playlistCount)
    {
        var pipe = ScriptedMpvPipe.Start();
        pipe.PropertyJson["pause"] = "false";
        pipe.PropertyJson["core-idle"] = "true";
        pipe.PropertyJson["idle-active"] = "true";
        pipe.PropertyJson["path"] = "null";
        pipe.PropertyJson["media-title"] = "null";
        pipe.PropertyJson["time-pos"] = "null";
        pipe.PropertyJson["duration"] = "null";
        pipe.PropertyJson["playlist-pos"] = "-1";
        pipe.PropertyJson["playlist-count"] = playlistCount;
        pipe.PropertyJson["volume"] = "80";
        pipe.PropertyJson["mute"] = "false";
        return pipe;
    }

    private static string Dump(List<string> logs, object gate)
    {
        lock (gate)
        {
            return logs.Count == 0 ? "(empty)" : string.Join(" || ", logs);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
            {
                return true;
            }

            await Task.Delay(30);
        }

        return ready();
    }

    private static StringContent JsonFavorite(string url, string name)
    {
        return new StringContent(
            JsonSerializer.Serialize(new { url, name }),
            Encoding.UTF8,
            "application/json");
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindNode()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FindProductionIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "static", "index.html");
            if (File.Exists(candidate) && File.ReadAllText(candidate).Contains("function getHeaders()", StringComparison.Ordinal))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Production static/index.html was not found.");
    }

    private sealed class DedicatedUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public DedicatedUiThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            })
            {
                IsBackground = true,
                Name = "WinTubeRelay-Deadlock-UI",
            };
            _thread.Start();
        }

        public void Send(Action action)
        {
            Exception? error = null;
            using var done = new ManualResetEventSlim(false);
            _queue.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            if (!done.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("UI send timed out while a player command held the UI thread.");
            }

            if (error is not null)
            {
                throw error;
            }
        }

        public void Post(Action action)
        {
            _queue.Add(action);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }

    private sealed class ThrowOnWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action _onWrite;

        public ThrowOnWriteStream(Stream inner, Action onWrite)
        {
            _inner = inner;
            _onWrite = onWrite;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _onWrite();
            throw new IOException("simulated partial IPC write");
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _onWrite();
            throw new IOException("simulated partial IPC write");
        }
    }
}
