namespace WinTubeRelay.Tray;

internal static class LogFileRetention
{
    internal const long MaximumLogFileBytes = 1024 * 1024;
    internal const int RetainedArchiveCount = 3;

    private static readonly Lock SyncRoot = new();

    public static void AppendLine(string logPath, string message)
    {
        lock (SyncRoot)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RotateIfNeededCore(logPath);
            File.AppendAllText(logPath, message);
        }
    }

    public static void RotateIfNeeded(string logPath)
    {
        lock (SyncRoot)
        {
            RotateIfNeededCore(logPath);
        }
    }

    private static void RotateIfNeededCore(string logPath)
    {
        var file = new FileInfo(logPath);
        if (!file.Exists || file.Length < MaximumLogFileBytes)
        {
            return;
        }

        for (var index = RetainedArchiveCount; index >= 1; index--)
        {
            var source = index == 1 ? logPath : $"{logPath}.{index - 1}";
            var destination = $"{logPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }
    }
}
