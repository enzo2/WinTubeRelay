using System.Diagnostics;
using System.Text.Json;

namespace WinTubeRelay.PlaybackTests;

internal static class FakeMpvProcess
{
    public static bool ShouldRun(string[] args)
    {
        return args.Any(arg => arg.StartsWith("--input-ipc-server=", StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(string[] args)
    {
        var raw = args.First(arg => arg.StartsWith("--input-ipc-server=", StringComparison.OrdinalIgnoreCase))["--input-ipc-server=".Length..];
        const string prefix = @"\\.\pipe\";
        var pipeName = raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? raw[prefix.Length..] : raw;
        var directory = SessionDirectory(pipeName);
        Directory.CreateDirectory(directory);
        var launchCount = IncrementLaunchCount(directory);
        File.WriteAllText(Path.Combine(directory, "pid"), Environment.ProcessId.ToString());
        if (launchCount > 1)
        {
            File.WriteAllText(Path.Combine(directory, "duplicate"), launchCount.ToString());
        }

        using var pipe = ScriptedMpvPipe.Start(pipeName);
        using var stopped = new ManualResetEventSlim(false);
        pipe.PropertyProvider = name => ReadProperty(directory, name);
        pipe.OnCommand = line =>
        {
            using (var stream = new FileStream(
                Path.Combine(directory, "commands.jsonl"),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(line);
            }
            if (line.Contains("\"quit\"", StringComparison.Ordinal) && !File.Exists(Path.Combine(directory, "ignore-quit")))
            {
                stopped.Set();
            }
        };
        File.WriteAllText(Path.Combine(directory, "ready"), Environment.ProcessId.ToString());
        stopped.Wait();
        return 0;
    }

    public static string HostPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var exe = Path.ChangeExtension(typeof(FakeMpvProcess).Assembly.Location, ".exe");
        return File.Exists(exe) ? exe : processPath ?? exe;
    }

    public static string SessionDirectory(string pipeName)
    {
        return Path.Combine(Path.GetTempPath(), "WinTubeRelayTests", pipeName);
    }

    public static Process StartLeftover(string pipeName)
    {
        var directory = SessionDirectory(pipeName);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo
        {
            FileName = HostPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add($@"--input-ipc-server=\\.\pipe\{pipeName}");
        start.ArgumentList.Add("--idle=yes");
        return Process.Start(start) ?? throw new InvalidOperationException("Failed to start the fake mpv process.");
    }

    public static void WaitUntilReady(string pipeName, TimeSpan timeout)
    {
        var ready = Path.Combine(SessionDirectory(pipeName), "ready");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(ready))
            {
                return;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"Fake mpv did not become ready for pipe {pipeName}.");
    }

    public static int LaunchCount(string pipeName)
    {
        var path = Path.Combine(SessionDirectory(pipeName), "launch-count");
        return File.Exists(path) && int.TryParse(File.ReadAllText(path), out var count) ? count : 0;
    }

    public static string CommandLog(string pipeName)
    {
        var path = Path.Combine(SessionDirectory(pipeName), "commands.jsonl");
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(25);
            }
        }

        using var fallback = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fallbackReader = new StreamReader(fallback);
        return fallbackReader.ReadToEnd();
    }

    private static int IncrementLaunchCount(string directory)
    {
        var path = Path.Combine(directory, "launch-count");
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = reader.ReadToEnd();
        var count = int.TryParse(text, out var parsed) ? parsed + 1 : 1;
        stream.SetLength(0);
        stream.Position = 0;
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(count);
        writer.Flush();
        return count;
    }

    private static string ReadProperty(string directory, string name)
    {
        var path = Path.Combine(directory, "state.json");
        if (!File.Exists(path))
        {
            return "null";
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(name, out var value))
        {
            return "null";
        }

        return value.GetRawText();
    }
}
