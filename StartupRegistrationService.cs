using Microsoft.Win32;

namespace WinTubeRelay.Tray;

internal sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(Branding.StartupEntryName) is string command
            && !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");

        if (enabled)
        {
            runKey.SetValue(Branding.StartupEntryName, BuildLaunchCommand(), RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(Branding.StartupEntryName, throwOnMissingValue: false);
        }
    }

    private static string BuildLaunchCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Could not determine the current executable path.");
        }

        return $"\"{executablePath}\"";
    }
}
