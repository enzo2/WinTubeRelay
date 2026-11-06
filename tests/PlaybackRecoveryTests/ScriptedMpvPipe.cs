using System.Text;
using System.Text.Json;
using System.IO.Pipes;

namespace WinTubeRelay.PlaybackTests;

internal sealed class ScriptedMpvPipe : IDisposable
{
    private readonly ManualResetEventSlim _releaseHang = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly object _acceptGate = new();
    private readonly List<string> _commands = new();
    private readonly List<Task> _loops = new();
    private readonly List<NamedPipeServerStream> _waiting = new();
    private readonly List<NamedPipeServerStream> _monitorServers = new();
    private int _accepting;
    private int _monitorSessions;
    private volatile bool _pauseAccept;
    private volatile bool _dropMonitor;

    public int EndFileOnMonitorSession { get; set; }

    private int _failNext;

    public void FailNextAcceptedConnection()
    {
        Interlocked.Exchange(ref _failNext, 1);
    }

    private ScriptedMpvPipe(string pipeName)
    {
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public Dictionary<string, string> PropertyJson { get; } = new(StringComparer.Ordinal);

    public Func<string, string?>? PropertyProvider { get; set; }

    public Action<string>? OnCommand { get; set; }

    public Func<string, IReadOnlyList<string>>? LinesBeforeResponse { get; set; }

    public Func<string, string?>? ResponseOverride { get; set; }

    public bool HangAfterCommand { get; set; }

    public bool CloseWithoutResponse { get; set; }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    public static ScriptedMpvPipe Start(string? pipeName = null)
    {
        var pipe = new ScriptedMpvPipe(pipeName ?? $"wintuberelay-test-{Guid.NewGuid():N}");
        for (var index = 0; index < 4; index++)
        {
            pipe._loops.Add(Task.Run(pipe.AcceptLoop));
        }

        pipe.WaitUntilConnectable();
        return pipe;
    }

    public bool CommandsContain(string token)
    {
        lock (_gate)
        {
            return _commands.Exists(command => command.Contains(token, StringComparison.Ordinal));
        }
    }

    public int CommandCount(string token)
    {
        lock (_gate)
        {
            return _commands.Count(command => command.Contains(token, StringComparison.Ordinal));
        }
    }

    public void PauseAccepting()
    {
        NamedPipeServerStream[] waiting;
        lock (_acceptGate)
        {
            _pauseAccept = true;
            lock (_gate)
            {
                waiting = _waiting.ToArray();
                _waiting.Clear();
            }
        }

        foreach (var server in waiting)
        {
            try
            {
                server.Dispose();
            }
            catch
            {
            }
        }
    }

    public void ResumeAccepting()
    {
        _pauseAccept = false;
    }

    public int DroppedMonitors { get; private set; }

    public string LastDropError { get; private set; } = "";

    public void DropMonitor()
    {
        _dropMonitor = true;
        DroppedMonitors = 1;
        LastDropError = "drop requested";
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _releaseHang.Set();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            using var wake = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            wake.Connect(100);
        }
        catch
        {
        }

        Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }

    private void WaitUntilConnectable()
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                client.Connect(200);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(20);
            }
        }

        throw new TimeoutException($"Test mpv pipe '{PipeName}' did not accept a connection. {last?.Message}");
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_pauseAccept)
            {
                Thread.Sleep(15);
                continue;
            }

            NamedPipeServerStream? server = null;
            var waiting = false;
            try
            {
                lock (_acceptGate)
                {
                    if (_pauseAccept || _cts.IsCancellationRequested)
                    {
                        waiting = false;
                    }
                    else
                    {
                        server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.InOut,
                            16,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous,
                            inBufferSize: 65536,
                            outBufferSize: 65536);
                        lock (_gate)
                        {
                            _waiting.Add(server);
                        }

                        waiting = true;
                    }
                }

                if (!waiting)
                {
                    Thread.Sleep(15);
                    continue;
                }

                Interlocked.Increment(ref _accepting);
                server!.WaitForConnection();
                if (Interlocked.Exchange(ref _failNext, 0) == 1 || _pauseAccept || _cts.IsCancellationRequested)
                {
                    continue;
                }

                HandleClient(server);
            }
            catch (Exception) when (_cts.IsCancellationRequested || _pauseAccept)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    lock (_gate)
                    {
                        _waiting.Remove(server);
                    }

                    server.Dispose();
                }
            }
        }
    }

    private void HandleClient(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        string? line;
        var monitorSession = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lock (_gate)
            {
                _commands.Add(line);
            }

            OnCommand?.Invoke(line);

            if (monitorSession == 0 && line.Contains("request_log_messages", StringComparison.Ordinal))
            {
                monitorSession = Interlocked.Increment(ref _monitorSessions);
                lock (_gate)
                {
                    _monitorServers.Add(server);
                }
            }

            if (HangAfterCommand || CloseWithoutResponse)
            {
                if (HangAfterCommand)
                {
                    _releaseHang.Wait();
                }

                return;
            }

            var prefixes = LinesBeforeResponse?.Invoke(line) ?? Array.Empty<string>();
            foreach (var prefix in prefixes)
            {
                writer.WriteLine(prefix);
            }

            writer.WriteLine(ResponseOverride?.Invoke(line) ?? BuildResponse(line));
            if (monitorSession > 0
                && line.Contains("observe_property", StringComparison.Ordinal)
                && line.Contains("\"pause\"", StringComparison.Ordinal))
            {
                if (monitorSession == EndFileOnMonitorSession)
                {
                    writer.WriteLine("{\"event\":\"end-file\",\"reason\":\"eof\"}");
                }

                while (!_dropMonitor && !_cts.IsCancellationRequested)
                {
                    Thread.Sleep(20);
                }

                if (_dropMonitor)
                {
                    _dropMonitor = false;
                    writer.WriteLine();
                    LastDropError = "monitor dropped";
                }

                return;
            }
        }
    }

    private string BuildResponse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var requestId = root.TryGetProperty("request_id", out var requestIdElement) && requestIdElement.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var command = root.GetProperty("command");
        var name = command.GetArrayLength() > 0 ? command[0].GetString() : null;
        if (string.Equals(name, "get_property", StringComparison.Ordinal) && command.GetArrayLength() > 1)
        {
            var propertyName = command[1].GetString() ?? string.Empty;
            var provided = PropertyProvider?.Invoke(propertyName);
            var data = provided ?? (PropertyJson.TryGetValue(propertyName, out var json) ? json : "null");
            return $"{{\"data\":{data},\"request_id\":{requestId},\"error\":\"success\"}}";
        }

        return $"{{\"error\":\"success\",\"request_id\":{requestId}}}";
    }
}
