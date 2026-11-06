namespace WinTubeRelay.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!SingleInstance.TryAcquire(@"Local\WinTubeRelay", out var instanceMutex))
        {
            return;
        }

        using (instanceMutex)
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext();
            Application.Run(context);
        }
    }
}
