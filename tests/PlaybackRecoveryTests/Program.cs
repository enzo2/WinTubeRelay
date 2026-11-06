using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinTubeRelay.PlaybackTests;
using WinTubeRelay.Tray;

if (FakeMpvProcess.ShouldRun(args))
{
    return FakeMpvProcess.Run(args);
}

return await PlaybackRecoveryHarness.RunAsync(args);

internal static class PlaybackRecoveryHarness
{
    private const string TrackUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string LgEndpointId = @"\\?\SWD#MMDEVAPI#{11111111-1111-1111-1111-111111111111}#{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}";
    private const string LgMpvDeviceId = "wasapi/{11111111-1111-1111-1111-111111111111}";

    public static async Task<int> RunAsync(string[] args)
    {
        var selected = args.Where(arg => !string.IsNullOrWhiteSpace(arg)).ToArray();
        var tests = new (string Name, Func<Task> Test)[]
        {
            ("play_wake_before_reinitialize_before_play", () => AssertPreparedPlaybackAsync(PlaybackIntent.Play, enqueue: false)),
            ("resume_wake_before_reinitialize_before_resume", () => AssertPreparedPlaybackAsync(PlaybackIntent.Resume, enqueue: false)),
            ("enqueue_wake_before_reinitialize_before_enqueue", () => AssertPreparedPlaybackAsync(PlaybackIntent.Enqueue, enqueue: true)),
            ("reinit_absent_returns_same_device_id", ReinitAbsentReturnsSameDeviceId),
            ("timeout_uses_auto", TimeoutUsesAuto),
            ("healthy_display_does_not_force_reinit", HealthyDisplayDoesNotForceReinit),
            ("non_display_output_does_not_wake", NonDisplayOutputDoesNotWake),
            ("rejection_does_not_wake_or_touch_player", RejectionDoesNotWakeOrTouchPlayer),
            ("paused_loaded_track_is_not_idle_when_core_idle", MpvLifecycleTests.PausedLoadedTrackIsNotIdleWhenCoreIdle),
            ("buffering_core_idle_is_not_idle", MpvLifecycleTests.BufferingCoreIdleIsNotIdle),
            ("reinit_core_idle_without_idle_active_is_not_idle", MpvLifecycleTests.ReinitCoreIdleWithoutIdleActiveIsNotIdle),
            ("true_idle_when_idle_active", MpvLifecycleTests.TrueIdleWhenIdleActive),
            ("ipc_reply_skips_events_and_foreign_request_ids", MpvLifecycleTests.IpcReplySkipsEventsAndForeignRequestIds),
            ("ipc_error_response_is_not_success", MpvLifecycleTests.IpcErrorResponseIsNotSuccess),
            ("existing_pipe_is_adopted_without_restart", MpvLifecycleTests.ExistingPipeIsAdoptedWithoutRestartOrDuplicateLaunch),
            ("mutating_ipc_command_is_not_retried_after_write", MpvLifecycleTests.MutatingIpcCommandIsNotRetriedAfterWrite),
            ("ipc_read_has_deadline", MpvLifecycleTests.IpcReadHasDeadline),
            ("failsafe_does_not_replace_remaining_queue", MpvLifecycleTests.FailsafeDoesNotReplaceRemainingQueue),
            ("failsafe_does_not_seek_before_load_ready", MpvLifecycleTests.FailsafeDoesNotSeekBeforeLoadReady),
            ("pause_suppresses_failsafe_when_status_blips_idle", MpvLifecycleTests.PauseSuppressesFailsafeWhenStatusBlipsIdle),
            ("corrupt_settings_load_uses_backup", LifecycleSupportTests.CorruptSettingsLoadUsesBackup),
            ("second_process_does_not_acquire_instance_mutex", LifecycleSupportTests.SecondProcessDoesNotAcquireInstanceMutex),
            ("wake_lock_survives_idle_status_during_preparation", LifecycleSupportTests.WakeLockSurvivesIdleStatusDuringPreparation),
            ("queued_recovery_keeps_later_wake", LifecycleSupportTests.QueuedRecoveryKeepsLaterWake),
            ("web_ui_can_send_configured_api_key", LifecycleSupportTests.WebUiCanSendConfiguredApiKey),
            ("web_ui_get_headers_executes", ReviewRegressionTests.WebUiGetHeadersExecutes),
            ("exclusive_prepare_does_not_deadlock_ui_command", ReviewRegressionTests.ExclusivePrepareDoesNotDeadlockUiCommand),
            ("exclusive_actions_do_not_interleave", ReviewRegressionTests.ExclusiveActionsDoNotInterleave),
            ("favorite_and_recent_updates_share_settings_lock", ReviewRegressionTests.FavoriteAndRecentUpdatesShareSettingsLock),
            ("monitor_reconnects_after_one_failed_connect", ReviewRegressionTests.MonitorReconnectsAfterOneFailedConnect),
            ("mutating_ipc_is_not_retried_when_write_throws", ReviewRegressionTests.MutatingIpcIsNotRetriedWhenWriteThrows),
            ("idle_active_wins_over_stale_path", ReviewRegressionTests.IdleActiveWinsOverStalePath),
            ("toggle_pause_suppresses_failsafe", ReviewRegressionTests.TogglePauseSuppressesFailsafe),
            ("toggle_known_paused_resumes", ReviewRegressionTests.ToggleKnownPausedResumes),
            ("pending_resume_cleared_after_load_send", ReviewRegressionTests.PendingResumeClearedAfterLoadSend),
            ("pending_resume_cleared_after_load_failure", ReviewRegressionTests.PendingResumeClearedAfterLoadFailure),
            ("null_playlist_count_suppresses_failsafe", ReviewRegressionTests.NullPlaylistCountSuppressesFailsafe),
            ("abandoned_mutex_is_acquired_and_released", ReviewRegressionTests.AbandonedMutexIsAcquiredAndReleased),
            ("late_wake_survives_consume_complete_gap", ReviewRegressionTests.LateWakeSurvivesConsumeCompleteGap),
            ("toggle_paused_wakes_before_unpause", TogglePausedWakesBeforeUnpause),
            ("reinit_failure_does_not_start_playback", ReinitFailureDoesNotStartPlayback),
            ("web_server_start_does_not_deadlock_on_caller_context", WebLifecycleRegressionTests.WebServerStartDoesNotDeadlockOnCallerContext),
            ("web_server_stop_does_not_deadlock_on_caller_context", WebLifecycleRegressionTests.WebServerStopDoesNotDeadlockOnCallerContext),
            ("web_lifecycle_shutdown_idle_releases_port", WebLifecycleRegressionTests.ShutdownIdleReleasesPortAndDisposesOnce),
            ("web_lifecycle_shutdown_survives_inflight_ui_marshal", WebLifecycleRegressionTests.ShutdownSurvivesInflightUiMarshal),
            ("web_lifecycle_restart_binds_new_port_after_inflight", WebLifecycleRegressionTests.RestartBindsNewPortAfterInflightRequest),
            ("web_lifecycle_duplicate_shutdown_disposes_once", WebLifecycleRegressionTests.DuplicateShutdownDisposesOnce),
            ("web_lifecycle_shutdown_logs_dispose_errors", WebLifecycleRegressionTests.ShutdownLogsDisposeErrors),
        };

        var ran = 0;
        var failures = 0;
        foreach (var test in tests)
        {
            if (selected.Length > 0 && !selected.Any(name => test.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ran++;
            try
            {
                await test.Test();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}");
                Console.WriteLine(ex.Message);
            }
        }

        Console.WriteLine($"PLAYBACK_RECOVERY_RESULT ran={ran} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task AssertPreparedPlaybackAsync(PlaybackIntent intent, bool enqueue)
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            PlayerRunning = true,
        });
        using var response = intent == PlaybackIntent.Resume
            ? await session.PostResumeAsync()
            : await session.PostPlayAsync(enqueue);
        var text = await response.Content.ReadAsStringAsync();
        var events = session.EventText;
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 1, $"wakeCalls={session.WakeCalls}"),
            (session.WakeThread == session.UiThreadId, $"wakeThread={session.WakeThread} uiThread={session.UiThreadId}"),
            (session.ActionThread > 0 && session.ActionThread != session.UiThreadId, $"actionThread={session.ActionThread} uiThread={session.UiThreadId}"),
            (session.PollsOnUiThread == 0, $"pollsOnUiThread={session.PollsOnUiThread}"),
            (session.PostWakePolls >= 2, $"postWakePolls={session.PostWakePolls}"),
            (session.Player.ReinitCount == 1, $"reinitCount={session.Player.ReinitCount}"),
            (session.Player.LastReinitForce == true, $"reinitForce={session.Player.LastReinitForce}"),
            (session.Player.LastReinitDeviceId == LgMpvDeviceId, $"reinitDevice={session.Player.LastReinitDeviceId ?? "(none)"}"),
            (session.ActionDevice == LgMpvDeviceId, $"actionDevice={session.ActionDevice ?? "(none)"}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount}"),
            (intent != PlaybackIntent.Resume || (session.Player.ResumeCount == 1 && session.Player.PlayCount == 0), $"resume={session.Player.ResumeCount} play={session.Player.PlayCount}"),
            (intent == PlaybackIntent.Resume || (session.Player.PlayCount == 1 && session.PlayedEnqueue == enqueue), $"play={session.Player.PlayCount} enqueue={session.PlayedEnqueue}"),
            (session.UnavailableCallbacks == 0, $"unavailableCallbacks={session.UnavailableCallbacks}"),
            (IndexOf(session.Events, "wake") >= 0
                && IndexOf(session.Events, "wake") < IndexOfPrefix(session.Events, "reinit:")
                && IndexOfPrefix(session.Events, "reinit:") < IndexOfPrefix(session.Events, intent == PlaybackIntent.Resume ? "resume:" : "play:"),
                $"order={events}"));
    }

    private static async Task ReinitAbsentReturnsSameDeviceId()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            PlayerRunning = false,
        });
        using var response = await session.PostPlayAsync(enqueue: false);
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 1, $"wakeCalls={session.WakeCalls}"),
            (session.Player.ReinitCount == 0, $"reinitCount={session.Player.ReinitCount}"),
            (session.ActionDevice == LgMpvDeviceId, $"actionDevice={session.ActionDevice ?? "(none)"}"),
            (session.Player.PlayCount == 1 && session.PlayedEnqueue == false, $"play={session.Player.PlayCount} enqueue={session.PlayedEnqueue}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount}"));
    }

    private static async Task TimeoutUsesAuto()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = false,
            PlayerRunning = true,
        });
        using var response = await session.PostPlayAsync(enqueue: false);
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 1, $"wakeCalls={session.WakeCalls}"),
            (session.UnavailableCallbacks == 1, $"unavailableCallbacks={session.UnavailableCallbacks}"),
            (session.Player.ReinitCount == 1, $"reinitCount={session.Player.ReinitCount}"),
            (session.Player.LastReinitForce == true, $"reinitForce={session.Player.LastReinitForce}"),
            (session.Player.LastReinitDeviceId == "auto", $"reinitDevice={session.Player.LastReinitDeviceId ?? "(none)"}"),
            (session.ActionDevice == "auto", $"actionDevice={session.ActionDevice ?? "(none)"}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount} events={session.EventText}"));
    }

    private static async Task HealthyDisplayDoesNotForceReinit()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            InitiallyPresent = true,
            PlayerRunning = true,
        });
        using var response = await session.PostPlayAsync(enqueue: false);
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 1, $"wakeCalls={session.WakeCalls}"),
            (session.Player.ReinitCount == 0, $"reinitCount={session.Player.ReinitCount}"),
            (session.Player.LastReinitForce != true, $"reinitForce={session.Player.LastReinitForce}"),
            (session.ActionDevice == LgMpvDeviceId, $"actionDevice={session.ActionDevice ?? "(none)"}"),
            (session.UnavailableCallbacks == 0, $"unavailableCallbacks={session.UnavailableCallbacks}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount}"));
    }

    private static async Task NonDisplayOutputDoesNotWake()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            InitiallyPresent = true,
            OutputName = "Speakers (Realtek)",
            PlayerRunning = true,
        });
        using var response = await session.PostPlayAsync(enqueue: false);
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 0, $"wakeCalls={session.WakeCalls}"),
            (session.Player.ReinitCount == 0, $"reinitCount={session.Player.ReinitCount}"),
            (session.ActionDevice == LgMpvDeviceId, $"actionDevice={session.ActionDevice ?? "(none)"}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount}"));
    }

    private static async Task RejectionDoesNotWakeOrTouchPlayer()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = false,
            PlayerRunning = true,
            ApiKey = "secret",
        });
        using var http = session.CreateClient();
        using var missingKey = await http.PostAsync("/play", JsonBody(TrackUrl, false));
        using var resumeMissingKey = await http.PostAsync("/resume", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/play")
        {
            Content = JsonBody(TrackUrl, false),
        };
        wrongKeyRequest.Headers.TryAddWithoutValidation("x-api-key", "nope");
        using var wrongKey = await http.SendAsync(wrongKeyRequest);
        using var badUrlRequest = new HttpRequestMessage(HttpMethod.Post, "/play")
        {
            Content = JsonBody("https://example.com/watch?v=1", false),
        };
        badUrlRequest.Headers.TryAddWithoutValidation("x-api-key", "secret");
        using var badUrl = await http.SendAsync(badUrlRequest);

        ExpectAll(
            (missingKey.StatusCode == HttpStatusCode.Unauthorized, $"missing key HTTP {(int)missingKey.StatusCode}"),
            (resumeMissingKey.StatusCode == HttpStatusCode.Unauthorized, $"resume missing key HTTP {(int)resumeMissingKey.StatusCode}"),
            (wrongKey.StatusCode == HttpStatusCode.Unauthorized, $"wrong key HTTP {(int)wrongKey.StatusCode}"),
            (badUrl.StatusCode == HttpStatusCode.BadRequest, $"bad url HTTP {(int)badUrl.StatusCode}"),
            (session.WakeCalls == 0, $"wakeCalls={session.WakeCalls}"),
            (session.Player.PlayCount == 0, $"playCount={session.Player.PlayCount}"),
            (session.Player.ResumeCount == 0, $"resumeCount={session.Player.ResumeCount}"),
            (session.Player.ReinitCount == 0, $"reinitCount={session.Player.ReinitCount}"),
            (session.Player.QuitCount == 0 && session.Player.StopCount == 0, $"quit={session.Player.QuitCount} stop={session.Player.StopCount}"));
    }

    private static async Task TogglePausedWakesBeforeUnpause()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            PlayerRunning = true,
        });
        session.Player.SnapshotState = "paused";
        using var response = await session.CreateClient().PostAsync("/toggle", new StringContent("{}", Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.WakeCalls == 1, $"wakeCalls={session.WakeCalls}"),
            (session.Player.ToggleCount == 1, $"toggle={session.Player.ToggleCount}"),
            (session.Player.ReinitCount == 1, $"reinitCount={session.Player.ReinitCount}"));
    }

    private static async Task ReinitFailureDoesNotStartPlayback()
    {
        await using var session = await IncidentSession.StartAsync(new SessionOptions
        {
            ReturnDeviceAfterWake = true,
            PlayerRunning = true,
        });
        session.Player.FailReinit = true;
        using var response = await session.PostPlayAsync(enqueue: false);
        var text = await response.Content.ReadAsStringAsync();
        ExpectAll(
            (response.StatusCode == HttpStatusCode.InternalServerError, $"HTTP {(int)response.StatusCode}: {text}"),
            (session.Player.PlayCount == 0, $"play={session.Player.PlayCount}"),
            (session.Player.ReinitCount >= 2, $"reinitCount={session.Player.ReinitCount}"),
            (session.UnavailableCallbacks == 1, $"unavailableCallbacks={session.UnavailableCallbacks}"));
    }

    private static void ExpectAll(params (bool Condition, string Message)[] checks)
    {
        var failures = checks.Where(check => !check.Condition).Select(check => check.Message).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private static StringContent JsonBody(string url, bool enqueue)
    {
        return new StringContent(
            $$"""{"url":"{{url}}","enqueue":{{(enqueue ? "true" : "false")}}}""",
            Encoding.UTF8,
            "application/json");
    }

    private static int IndexOf(List<string> events, string value)
    {
        lock (events)
        {
            return events.IndexOf(value);
        }
    }

    private static int IndexOfPrefix(List<string> events, string prefix)
    {
        lock (events)
        {
            return events.FindIndex(item => item.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SessionOptions
    {
        public bool ReturnDeviceAfterWake { get; init; }

        public bool InitiallyPresent { get; init; }

        public bool PlayerRunning { get; init; } = true;

        public string OutputName { get; init; } = "LG Intel Display Audio";

        public string? ApiKey { get; init; }
    }

    private sealed class IncidentSession : IAsyncDisposable
    {
        private readonly UiThreadInvoker _ui;
        private readonly object _gate = new();
        private readonly List<string> _events = new();
        private WebServerService? _server;
        private HttpClient? _http;
        private DateTime _now = DateTime.UtcNow;
        private int _wakeCalls;
        private int _postWakePolls;
        private int _pollsOnUiThread;

        private IncidentSession(UiThreadInvoker ui, RecordingPlayer player, AppSettings settings)
        {
            _ui = ui;
            Player = player;
            Settings = settings;
        }

        public RecordingPlayer Player { get; }

        public AppSettings Settings { get; }

        public int WakeCalls => _wakeCalls;

        public int WakeThread { get; private set; } = -1;

        public int ActionThread { get; private set; } = -1;

        public string? ActionDevice { get; private set; }

        public bool? PlayedEnqueue { get; private set; }

        public int PostWakePolls => _postWakePolls;

        public int PollsOnUiThread => _pollsOnUiThread;

        public int UnavailableCallbacks { get; private set; }

        public int UiThreadId => _ui.ThreadId;

        public List<string> Events => _events;

        public string EventText
        {
            get
            {
                lock (_gate)
                {
                    return string.Join(" | ", _events);
                }
            }
        }

        public static async Task<IncidentSession> StartAsync(SessionOptions options)
        {
            var lgDevice = new AudioOutputDevice(LgEndpointId, options.OutputName, LgMpvDeviceId);
            var ui = new UiThreadInvoker();
            var player = new RecordingPlayer
            {
                Running = options.PlayerRunning,
            };
            var settings = new AppSettings
            {
                ApiPort = FreeTcpPort(),
                ApiKey = options.ApiKey,
                SelectedAudioDeviceId = LgEndpointId,
                SelectedAudioDeviceName = options.OutputName,
            };
            var session = new IncidentSession(ui, player, settings);
            player.OnPlay = (enqueue, deviceId) =>
            {
                session.PlayedEnqueue = enqueue;
                session.ActionThread = Environment.CurrentManagedThreadId;
                session.ActionDevice = deviceId;
                session.Record($"play:{enqueue}:{deviceId}");
            };
            player.OnResume = deviceId =>
            {
                session.ActionThread = Environment.CurrentManagedThreadId;
                session.ActionDevice = deviceId;
                session.Record($"resume:{deviceId}");
            };
            player.OnReinit = (deviceId, force) => session.Record($"reinit:{force}:{deviceId}");
            var coordinator = new DisplayPlaybackCoordinator(
                () => LgEndpointId,
                () => options.OutputName,
                () =>
                {
                    if (ui.IsCurrentThread)
                    {
                        Interlocked.Increment(ref session._pollsOnUiThread);
                    }

                    if (options.InitiallyPresent)
                    {
                        return new[] { lgDevice };
                    }

                    if (session._wakeCalls == 0)
                    {
                        session.Record("poll:before-wake");
                        return Array.Empty<AudioOutputDevice>();
                    }

                    var poll = Interlocked.Increment(ref session._postWakePolls);
                    session.Record($"poll:after-wake:{poll}");
                    return options.ReturnDeviceAfterWake && poll >= 2
                        ? new[] { lgDevice }
                        : Array.Empty<AudioOutputDevice>();
                },
                () =>
                {
                    session.WakeThread = Environment.CurrentManagedThreadId;
                    Interlocked.Increment(ref session._wakeCalls);
                    session.Record("wake");
                },
                ui.Invoke,
                player,
                settings,
                waitTimeout: TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(500),
                utcNow: () => session._now,
                sleep: delay => session._now = session._now.Add(delay),
                onStillUnavailable: () => session.UnavailableCallbacks++);
            var server = new WebServerService(
                () => settings,
                () => LgMpvDeviceId,
                player,
                () => { },
                _ => { },
                coordinator);
            try
            {
                await server.StartAsync();
            }
            catch
            {
                ui.Dispose();
                throw;
            }

            session._server = server;
            session._http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{settings.ApiPort}"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            return session;
        }

        public HttpClient CreateClient()
        {
            return _http ?? throw new InvalidOperationException("HTTP client was not started.");
        }

        public Task<HttpResponseMessage> PostPlayAsync(bool enqueue)
        {
            return CreateClient().PostAsync("/play", JsonBody(TrackUrl, enqueue));
        }

        public Task<HttpResponseMessage> PostResumeAsync()
        {
            return CreateClient().PostAsync("/resume", new StringContent("{}", Encoding.UTF8, "application/json"));
        }

        public async ValueTask DisposeAsync()
        {
            _http?.Dispose();
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }

            _ui.Dispose();
        }

        private void Record(string name)
        {
            lock (_gate)
            {
                _events.Add(name);
            }
        }
    }

    private sealed class RecordingPlayer : IMpvPlayback
    {
        public string? LastPlaybackError => null;

        public bool Running { get; set; }

        public int ReinitCount { get; private set; }

        public bool? LastReinitForce { get; private set; }

        public string? LastReinitDeviceId { get; private set; }

        public int ResumeCount { get; private set; }

        public int PlayCount { get; private set; }

        public int QuitCount { get; private set; }

        public int ToggleCount { get; private set; }

        public string SnapshotState { get; set; } = "idle";

        public int StopCount { get; private set; }

        public Action<bool, string>? OnPlay { get; set; }

        public Action<string>? OnResume { get; set; }

        public Action<string, bool>? OnReinit { get; set; }

        public void Play(AppSettings settings, string url, bool enqueue, string audioDeviceId)
        {
            PlayCount++;
            OnPlay?.Invoke(enqueue, audioDeviceId);
            Running = true;
        }

        public void Stop(AppSettings settings, string audioDeviceId)
        {
            StopCount++;
            Running = false;
        }

        public void Pause(AppSettings settings, string audioDeviceId)
        {
        }

        public void Resume(AppSettings settings, string audioDeviceId)
        {
            ResumeCount++;
            OnResume?.Invoke(audioDeviceId);
        }

        public void Toggle(AppSettings settings, string audioDeviceId)
        {
            ToggleCount++;
        }

        public void Skip(AppSettings settings, string audioDeviceId)
        {
        }

        public void SetVolume(AppSettings settings, string audioDeviceId, double volume)
        {
        }

        public void Mute(AppSettings settings, string audioDeviceId)
        {
        }

        public void Unmute(AppSettings settings, string audioDeviceId)
        {
        }

        public bool IsPlayerRunning(AppSettings settings)
        {
            return Running;
        }

        public bool FailReinit { get; set; }

        public bool TryApplyAudioOutput(AppSettings settings, string audioDeviceId, bool forceReinitialize = false)
        {
            ReinitCount++;
            LastReinitForce = forceReinitialize;
            LastReinitDeviceId = audioDeviceId;
            OnReinit?.Invoke(audioDeviceId, forceReinitialize);
            return !FailReinit;
        }

        public bool QuitPlayer(AppSettings settings)
        {
            QuitCount++;
            Running = false;
            return true;
        }

        public void InvokeExclusive(Action action)
        {
            lock (this)
            {
                action();
            }
        }

        public ApiStatusSnapshot GetApiStatusSnapshot(AppSettings settings, string audioDeviceId)
        {
            return new ApiStatusSnapshot(
                new Dictionary<string, object?> { ["pause"] = string.Equals(SnapshotState, "paused", StringComparison.Ordinal) },
                new List<string>(),
                SnapshotState);
        }

        public (double? Volume, bool? Mute) GetVolumeSnapshot(AppSettings settings, string audioDeviceId)
        {
            return (null, null);
        }
    }

    private sealed class UiThreadInvoker : IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new();

        public UiThreadInvoker()
        {
            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                ready.Set();
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    action();
                }
            })
            {
                IsBackground = true,
                Name = "WinTubeRelay-Test-UI",
            };
            _thread.Start();
            ready.Wait();
        }

        public int ThreadId { get; private set; }

        public bool IsCurrentThread => Environment.CurrentManagedThreadId == ThreadId;

        public void Invoke(Action action)
        {
            if (IsCurrentThread)
            {
                action();
                return;
            }

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
            if (!done.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("UI thread did not run the invoked action.");
            }

            if (error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }
    }
}
