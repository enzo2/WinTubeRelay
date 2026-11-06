namespace WinTubeRelay.Tray;

internal sealed class TrayWebLifecycle
{
    private int _shutdownStarted;

    public Task<bool> ShutdownAsync(WebServerService server, Action disposeAfterDrain, Action<string> log)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return Task.FromResult(false);
        }

        return ShutdownCoreAsync(server, disposeAfterDrain, log);
    }

    public async Task<Exception?> RestartAsync(
        WebServerService server,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await server.RestartAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            SafeLog(log, ex);
            return ex;
        }
    }

    private async Task<bool> ShutdownCoreAsync(WebServerService server, Action disposeAfterDrain, Action<string> log)
    {
        try
        {
            // The caller owns UI resources disposed below. Keep its context while
            // awaiting; WebServerService itself never requires that context to stop.
            await server.DisposeAsync();
        }
        catch (Exception ex)
        {
            SafeLog(log, ex);
        }

        try
        {
            disposeAfterDrain();
        }
        catch (Exception ex)
        {
            SafeLog(log, ex);
        }

        return true;
    }

    private static void SafeLog(Action<string> log, Exception ex)
    {
        try
        {
            log(ex.ToString());
        }
        catch
        {
        }
    }
}
