using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinTubeRelay.Tray;

namespace WinTubeRelay.PlaybackTests;

internal static class WebLifecycleRegressionTests
{
    public static Task WebServerStartDoesNotDeadlockOnCallerContext()
    {
        using var ui = new PumpingSynchronizationContext();
        var port = FreeTcpPort();
        var server = CreateServer(port, preparer: null);
        try
        {
            ui.Send(() => server.StartAsync().GetAwaiter().GetResult());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var health = client.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            MpvLifecycleTests.Expect((health.IsSuccessStatusCode, $"health HTTP {(int)health.StatusCode}"));
        }
        finally
        {
            TryStop(server);
            ui.Dispose();
        }

        return Task.CompletedTask;
    }

    public static async Task WebServerStopDoesNotDeadlockOnCallerContext()
    {
        using var ui = new PumpingSynchronizationContext();
        var port = FreeTcpPort();
        var server = CreateServer(port, preparer: null);
        await server.StartAsync();
        try
        {
            ui.Send(() => server.DisposeAsync().AsTask().GetAwaiter().GetResult());
            MpvLifecycleTests.Expect((!PortAccepts(port), $"port {port} still accepted a connection after stop"));
        }
        finally
        {
            TryStop(server);
            ui.Dispose();
        }
    }

    public static async Task ShutdownIdleReleasesPortAndDisposesOnce()
    {
        using var ui = new PumpingSynchronizationContext();
        var port = FreeTcpPort();
        var server = CreateServer(port, preparer: null);
        await server.StartAsync();
        var disposed = new ManualResetEventSlim(false);
        var logs = new ConcurrentQueue<string>();
        var lifecycle = new TrayWebLifecycle();
        try
        {
            var shutdown = StartOnUi(ui, () => lifecycle.ShutdownAsync(server, () =>
            {
                if (Environment.CurrentManagedThreadId != ui.ThreadId)
                {
                    throw new InvalidOperationException("Shell disposal must run on the owning UI thread.");
                }
                disposed.Set();
            }, logs.Enqueue));
            var completed = await WaitAsync(shutdown, TimeSpan.FromSeconds(5));
            MpvLifecycleTests.Expect(
                (completed && shutdown.IsCompletedSuccessfully && shutdown.Result, "idle shutdown did not finish on the UI pump"),
                (disposed.IsSet, "icon/audio dispose did not run after idle stop"),
                (!PortAccepts(port), $"port {port} still accepted a connection after idle shutdown"));
        }
        finally
        {
            TryStop(server);
            ui.Dispose();
            disposed.Dispose();
        }
    }

    public static async Task ShutdownSurvivesInflightUiMarshal()
    {
        await using var hold = await InflightHold.StartAsync();
        var disposedBeforeMarshal = false;
        var disposed = new ManualResetEventSlim(false);
        var logs = new ConcurrentQueue<string>();
        var lifecycle = new TrayWebLifecycle();
        var shutdown = StartOnUi(hold.Ui, () => lifecycle.ShutdownAsync(
            hold.Server,
            () =>
            {
                disposedBeforeMarshal = !hold.Marshaled.IsSet;
                disposed.Set();
            },
            logs.Enqueue));

        hold.AllowSend.Set();
        if (!hold.Marshaled.Wait(TimeSpan.FromSeconds(4)))
        {
            throw new TimeoutException("inflight request could not marshal to the UI during shutdown");
        }

        hold.Release.Set();
        var completed = await WaitAsync(shutdown, TimeSpan.FromSeconds(5));
        using var response = await WaitAsync(hold.Request, TimeSpan.FromSeconds(5))
            ? await hold.Request
            : throw new TimeoutException("inflight play request did not finish after the UI marshal");
        MpvLifecycleTests.Expect(
            (completed && shutdown.IsCompletedSuccessfully && shutdown.Result, "shutdown did not finish after the inflight request drained"),
            (hold.Preparer.MarshalThreadId == hold.Ui.ThreadId, $"marshalThread={hold.Preparer.MarshalThreadId} uiThread={hold.Ui.ThreadId}"),
            (disposed.IsSet && !disposedBeforeMarshal, "shell was disposed before the inflight UI marshal finished"),
            (!PortAccepts(hold.Port), $"port {hold.Port} still accepted a connection after shutdown"),
            (response.IsSuccessStatusCode, $"play HTTP {(int)response.StatusCode}"));
        disposed.Dispose();
    }

    public static async Task RestartBindsNewPortAfterInflightRequest()
    {
        await using var hold = await InflightHold.StartAsync();
        var portB = FreeTcpPort();
        hold.Settings.ApiPort = portB;
        var logs = new ConcurrentQueue<string>();
        var lifecycle = new TrayWebLifecycle();
        var restart = StartOnUi(hold.Ui, () => lifecycle.RestartAsync(hold.Server, logs.Enqueue));

        hold.AllowSend.Set();
        if (!hold.Marshaled.Wait(TimeSpan.FromSeconds(4)))
        {
            throw new TimeoutException("inflight request could not marshal to the UI during restart");
        }

        var boundEarly = PortAccepts(portB);
        hold.Release.Set();
        var completed = await WaitAsync(restart, TimeSpan.FromSeconds(5));
        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await health.GetAsync($"http://127.0.0.1:{portB}/health");
        var body = await response.Content.ReadAsStringAsync();
        MpvLifecycleTests.Expect(
            (!boundEarly, $"port {portB} accepted a connection before the existing request finished"),
            (completed && restart.IsCompletedSuccessfully && restart.Result is null, restart.IsCompletedSuccessfully ? restart.Result?.ToString() ?? "restart failed" : "restart did not finish"),
            (response.IsSuccessStatusCode && body.Contains("\"ok\":true", StringComparison.Ordinal), $"new port health HTTP {(int)response.StatusCode}: {body}"),
            (!PortAccepts(hold.Port), $"old port {hold.Port} still accepted a connection after restart"),
            (hold.Preparer.MarshalThreadId == hold.Ui.ThreadId, $"marshalThread={hold.Preparer.MarshalThreadId} uiThread={hold.Ui.ThreadId}"));
    }

    public static async Task DuplicateShutdownDisposesOnce()
    {
        var server = CreateServer(FreeTcpPort(), preparer: null);
        var count = 0;
        var lifecycle = new TrayWebLifecycle();
        var first = await lifecycle.ShutdownAsync(server, () => count++, _ => { });
        var second = await lifecycle.ShutdownAsync(server, () => count++, _ => { });
        MpvLifecycleTests.Expect(
            (first, "first shutdown did not run"),
            (!second, "duplicate shutdown ran again"),
            (count == 1, $"disposeCount={count}"));
    }

    public static async Task ShutdownLogsDisposeErrors()
    {
        var server = CreateServer(FreeTcpPort(), preparer: null);
        var logs = new ConcurrentQueue<string>();
        var lifecycle = new TrayWebLifecycle();
        Exception? thrown = null;
        try
        {
            await lifecycle.ShutdownAsync(
                server,
                () => throw new InvalidOperationException("dispose failed"),
                logs.Enqueue);
        }
        catch (Exception ex)
        {
            thrown = ex.GetBaseException();
        }

        MpvLifecycleTests.Expect(
            (thrown is null, thrown?.Message ?? "ok"),
            (logs.Any(line => line.Contains("dispose failed", StringComparison.Ordinal)), "dispose error was not logged"));
    }

    private static Task<T> StartOnUi<T>(PumpingSynchronizationContext ui, Func<Task<T>> start)
    {
        var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            ui.MarkEntered();
            var task = start();
            started.TrySetResult(task);
            ui.MarkReturned();
        });
        if (!ui.WaitReturned(TimeSpan.FromSeconds(4)))
        {
            throw new TimeoutException("UI starter did not return; the lifecycle call blocked the synchronization context.");
        }

        return started.Task.GetAwaiter().GetResult();
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout));
        return finished == task;
    }

    private static WebServerService CreateServer(int port, IPlaybackPreparer? preparer)
    {
        var settings = new AppSettings
        {
            ApiPort = port,
            ApiKey = null,
        };
        return new WebServerService(
            () => settings,
            () => "auto",
            new IdlePlayer(),
            () => { },
            _ => { },
            preparer);
    }

    private static void TryStop(WebServerService server)
    {
        try
        {
            var stop = server.DisposeAsync().AsTask();
            stop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    private static bool PortAccepts(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(400)))
            {
                return false;
            }

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static StringContent JsonPlay()
    {
        return new StringContent(
            "{\"url\":\"" + MpvLifecycleTests.TrackUrl + "\",\"enqueue\":false}",
            Encoding.UTF8,
            "application/json");
    }

    private sealed class InflightHold : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly Task<HttpResponseMessage> _request;

        private InflightHold(
            PumpingSynchronizationContext ui,
            WebServerService server,
            AppSettings settings,
            UiMarshalPreparer preparer,
            HttpClient http,
            Task<HttpResponseMessage> request,
            int port)
        {
            Ui = ui;
            Server = server;
            Settings = settings;
            Preparer = preparer;
            _http = http;
            _request = request;
            Port = port;
        }

        public PumpingSynchronizationContext Ui { get; }

        public WebServerService Server { get; }

        public AppSettings Settings { get; }

        public UiMarshalPreparer Preparer { get; }

        public Task<HttpResponseMessage> Request => _request;

        public int Port { get; }

        public ManualResetEventSlim AllowSend => Preparer.AllowSend;

        public ManualResetEventSlim Marshaled => Preparer.Marshaled;

        public ManualResetEventSlim Release => Preparer.Release;

        public static async Task<InflightHold> StartAsync()
        {
            var ui = new PumpingSynchronizationContext();
            var port = FreeTcpPort();
            var settings = new AppSettings
            {
                ApiPort = port,
                ApiKey = null,
            };
            var preparer = new UiMarshalPreparer(ui);
            var server = new WebServerService(
                () => settings,
                () => "auto",
                new IdlePlayer(),
                () => { },
                _ => { },
                preparer);
            try
            {
                await server.StartAsync();
            }
            catch
            {
                ui.Dispose();
                throw;
            }

            var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(20),
            };
            var request = http.PostAsync("/play", JsonPlay());
            if (!preparer.Entered.Wait(TimeSpan.FromSeconds(5)))
            {
                http.Dispose();
                ui.Dispose();
                throw new TimeoutException("play request did not enter prepare");
            }

            return new InflightHold(ui, server, settings, preparer, http, request, port);
        }

        public async ValueTask DisposeAsync()
        {
            Release.Set();
            AllowSend.Set();
            try
            {
                await WaitAsync(_request, TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _http.Dispose();
            TryStop(Server);
            Ui.Dispose();
            Preparer.Dispose();
        }
    }

    private sealed class UiMarshalPreparer : IPlaybackPreparer, IDisposable
    {
        private readonly PumpingSynchronizationContext _ui;

        public UiMarshalPreparer(PumpingSynchronizationContext ui)
        {
            _ui = ui;
        }

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim AllowSend { get; } = new(false);

        public ManualResetEventSlim Marshaled { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int MarshalThreadId { get; private set; } = -1;

        public string Prepare(PlaybackIntent intent)
        {
            Entered.Set();
            if (!AllowSend.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("lifecycle call did not reach the UI before prepare marshaled");
            }

            _ui.Send(() =>
            {
                MarshalThreadId = Environment.CurrentManagedThreadId;
                Marshaled.Set();
            });
            if (!Release.Wait(TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException("inflight prepare was not released");
            }

            return "auto";
        }

        public void Dispose()
        {
            Entered.Dispose();
            AllowSend.Dispose();
            Marshaled.Dispose();
            Release.Dispose();
        }
    }

    private sealed class IdlePlayer : IMpvPlayback
    {
        public string? LastPlaybackError => null;

        public void Play(AppSettings settings, string url, bool enqueue, string audioDeviceId)
        {
        }

        public void Stop(AppSettings settings, string audioDeviceId)
        {
        }

        public void Pause(AppSettings settings, string audioDeviceId)
        {
        }

        public void Resume(AppSettings settings, string audioDeviceId)
        {
        }

        public void Toggle(AppSettings settings, string audioDeviceId)
        {
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

        public bool IsPlayerRunning(AppSettings settings) => false;

        public bool TryApplyAudioOutput(AppSettings settings, string audioDeviceId, bool forceReinitialize = false) => false;

        public bool QuitPlayer(AppSettings settings) => false;

        public void InvokeExclusive(Action action) => action();

        public ApiStatusSnapshot GetApiStatusSnapshot(AppSettings settings, string audioDeviceId)
        {
            return new ApiStatusSnapshot(new Dictionary<string, object?>(), new List<string>(), "idle");
        }

        public (double? Volume, bool? Mute) GetVolumeSnapshot(AppSettings settings, string audioDeviceId)
        {
            return (null, null);
        }
    }

    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Work> _queue = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _returned = new(false);
        private int _threadId;

        public PumpingSynchronizationContext()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WinTubeRelay-Lifecycle-UI",
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("synchronization context did not start");
            }
        }

        public int ThreadId => _threadId;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Add(new Work(callback, state, null));
        }

        public void Post(Action action)
        {
            Post(_ => action(), null);
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                callback(state);
                return;
            }

            var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new Work(callback, state, done));
            if (!done.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("UI send timed out; the synchronization context was blocked.");
            }

            var error = done.Task.GetAwaiter().GetResult();
            if (error is not null)
            {
                throw error;
            }
        }

        public void Send(Action action)
        {
            Send(_ => action(), null);
        }

        public void MarkEntered()
        {
            _entered.Set();
        }

        public void MarkReturned()
        {
            _returned.Set();
        }

        public bool WaitReturned(TimeSpan timeout)
        {
            return _returned.Wait(timeout);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _ready.Dispose();
            _entered.Dispose();
            _returned.Dispose();
        }

        private void Run()
        {
            _threadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            _ready.Set();
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                Exception? error = null;
                try
                {
                    work.Callback(work.State);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                work.Done?.TrySetResult(error);
            }
        }

        private sealed record Work(SendOrPostCallback Callback, object? State, TaskCompletionSource<Exception?>? Done);
    }
}
