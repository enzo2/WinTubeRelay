using System.Text.Json;

namespace WinTubeRelay.Tray;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _settingsDirectory;
    internal Action? HoldWhileLockedForTests { get; set; }
    private readonly string _settingsPath;
    private readonly string _legacySettingsPath;

    public SettingsStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTubeRelay"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MegaFire.Player.Tray", "settings.json"))
    {
    }

    internal SettingsStore(string settingsDirectory, string? legacySettingsPath = null)
    {
        _settingsDirectory = settingsDirectory;
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        _legacySettingsPath = legacySettingsPath ?? Path.Combine(settingsDirectory, "legacy-not-used.json");
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            return TryRead(ResolveSettingsPath())
                ?? TryRead(_settingsPath + ".bak")
                ?? new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            lock (settings.SyncRoot)
            {
                PersistLocked(settings);
            }
        }
    }

    public void Update(AppSettings settings, Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            lock (settings.SyncRoot)
            {
                mutate(settings);
                PersistLocked(settings);
            }
        }
    }

    public void UpdateIfChanged(AppSettings settings, Func<AppSettings, bool> mutate)
    {
        lock (_gate)
        {
            lock (settings.SyncRoot)
            {
                if (!mutate(settings))
                {
                    return;
                }

                PersistLocked(settings);
            }
        }
    }

    public IReadOnlyList<SavedUrlEntry> CopyFavorites(AppSettings settings)
    {
        lock (_gate)
        {
            lock (settings.SyncRoot)
            {
                return settings.Favorites.ToList();
            }
        }
    }

    public IReadOnlyList<SavedUrlEntry> CopyRecent(AppSettings settings)
    {
        lock (_gate)
        {
            lock (settings.SyncRoot)
            {
                return settings.RecentUrls.ToList();
            }
        }
    }

    public void ReplaceFrom(AppSettings destination, AppSettings source)
    {
        Update(destination, current =>
        {
            current.SelectedAudioDeviceId = source.SelectedAudioDeviceId;
            current.SelectedAudioDeviceName = source.SelectedAudioDeviceName;
            current.MpvPath = source.MpvPath;
            current.YtDlpPath = source.YtDlpPath;
            current.YtDlpBrowser = source.YtDlpBrowser;
            current.MpvPipeName = source.MpvPipeName;
            current.MpvLogFilePath = source.MpvLogFilePath;
            current.ApiPort = source.ApiPort;
            current.ApiKey = source.ApiKey;
            current.VolumeStep = source.VolumeStep;
            current.MaxRecentUrls = source.MaxRecentUrls;
            current.Favorites = source.Favorites.ToList();
            current.RecentUrls = source.RecentUrls.ToList();
        });
    }

    private void PersistLocked(AppSettings settings)
    {
        HoldWhileLockedForTests?.Invoke();
        Directory.CreateDirectory(_settingsDirectory);
        var json = JsonSerializer.Serialize(Snapshot(settings), JsonOptions);
        var tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        var backupPath = _settingsPath + ".bak";
        if (File.Exists(_settingsPath))
        {
            File.Replace(tempPath, _settingsPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _settingsPath, overwrite: true);
            File.Copy(_settingsPath, backupPath, overwrite: true);
        }
    }

    private static AppSettings Snapshot(AppSettings settings)
    {
        return new AppSettings
        {
            SelectedAudioDeviceId = settings.SelectedAudioDeviceId,
            SelectedAudioDeviceName = settings.SelectedAudioDeviceName,
            MpvPath = settings.MpvPath,
            YtDlpPath = settings.YtDlpPath,
            YtDlpBrowser = settings.YtDlpBrowser,
            MpvPipeName = settings.MpvPipeName,
            MpvLogFilePath = settings.MpvLogFilePath,
            ApiPort = settings.ApiPort,
            ApiKey = settings.ApiKey,
            VolumeStep = settings.VolumeStep,
            MaxRecentUrls = settings.MaxRecentUrls,
            Favorites = settings.Favorites.ToList(),
            RecentUrls = settings.RecentUrls.ToList(),
        };
    }

    private static AppSettings? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
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
