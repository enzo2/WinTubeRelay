using System.Text.Json;

namespace WinTubeRelay.Tray;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string _legacySettingsPath;

    public SettingsStore()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTubeRelay");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _legacySettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MegaFire.Player.Tray",
            "settings.json");
    }

    public AppSettings Load()
    {
        var pathToLoad = ResolveSettingsPath();
        if (pathToLoad is null)
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(pathToLoad);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public string GetSettingsPath()
    {
        return _settingsPath;
    }

    private string? ResolveSettingsPath()
    {
        if (File.Exists(_settingsPath))
        {
            return _settingsPath;
        }

        if (File.Exists(_legacySettingsPath))
        {
            return _legacySettingsPath;
        }

        return null;
    }
}
