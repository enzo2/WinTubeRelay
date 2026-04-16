using System.Diagnostics;
using System.Drawing;

namespace WinTubeRelay.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly MpvController _mpvController;
    private readonly WebServerService _webServerService;
    private readonly DisplaySleepBlocker _displaySleepBlocker;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _brandIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _positionItem;
    private readonly ToolStripMenuItem _currentOutputItem;
    private readonly ToolStripMenuItem _audioOutputsMenuItem;
    private readonly ToolStripMenuItem _favoritesMenuItem;
    private readonly ToolStripMenuItem _recentMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _settingsPathItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private PlayerStatus? _lastObservedStatus;

    public TrayApplicationContext()
    {
        _settingsStore = new SettingsStore();
        _audioDeviceService = new AudioDeviceService();
        _startupRegistrationService = new StartupRegistrationService();
        _mpvController = new MpvController(Log);
        _displaySleepBlocker = new DisplaySleepBlocker(Log);
        _settings = _settingsStore.Load();
        _webServerService = new WebServerService(
            () => _settings,
            () => CurrentAudioDeviceId,
            _mpvController,
            SaveSettings,
            RecordSuccessfulPlay,
            Log);
        _brandIcon = Branding.CreateTrayIcon();

        _statusItem = new ToolStripMenuItem("Status: starting...")
        {
            Enabled = false,
        };
        _positionItem = new ToolStripMenuItem("Position: -")
        {
            Enabled = false,
        };
        _currentOutputItem = new ToolStripMenuItem("Current output: detecting...")
        {
            Enabled = false,
        };
        _audioOutputsMenuItem = new ToolStripMenuItem("Audio Output");
        _favoritesMenuItem = new ToolStripMenuItem("Favorites");
        _recentMenuItem = new ToolStripMenuItem("Recent");
        _startupMenuItem = new ToolStripMenuItem("Launch At Sign-In")
        {
            CheckOnClick = false,
        };
        _startupMenuItem.Click += (_, _) => ToggleStartupRegistration();
        _settingsPathItem = new ToolStripMenuItem($"Settings file: {_settingsStore.GetSettingsPath()}")
        {
            Enabled = false,
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Opening += (_, _) =>
        {
            RefreshStartupMenu();
            RefreshFavoritesMenu();
            RefreshRecentMenu();
        };
        contextMenu.Items.Add(new ToolStripMenuItem(Branding.BuildHeaderText()) { Enabled = false });
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(_positionItem);
        contextMenu.Items.Add(_currentOutputItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Open Web UI", null, (_, _) => OpenWebUi()));
        contextMenu.Items.Add(new ToolStripMenuItem("Play URL...", null, (_, _) => PlayFromPrompt(enqueue: false)));
        contextMenu.Items.Add(new ToolStripMenuItem("Play From Clipboard", null, (_, _) => PlayFromClipboard(enqueue: false)));
        contextMenu.Items.Add(new ToolStripMenuItem("Queue URL...", null, (_, _) => PlayFromPrompt(enqueue: true)));
        contextMenu.Items.Add(new ToolStripMenuItem("Queue From Clipboard", null, (_, _) => PlayFromClipboard(enqueue: true)));
        contextMenu.Items.Add(BuildPlaybackMenu());
        contextMenu.Items.Add(BuildVolumeMenu());
        contextMenu.Items.Add(_favoritesMenuItem);
        contextMenu.Items.Add(_recentMenuItem);
        contextMenu.Items.Add(_audioOutputsMenuItem);
        contextMenu.Items.Add(new ToolStripMenuItem("Refresh Outputs", null, (_, _) => RebuildAudioOutputsMenu()));
        contextMenu.Items.Add(_startupMenuItem);
        contextMenu.Items.Add(new ToolStripMenuItem("Player Settings...", null, (_, _) => ShowPlayerSettings()));
        contextMenu.Items.Add(new ToolStripMenuItem("Open Publish Folder", null, (_, _) => OpenPublishFolder()));
        contextMenu.Items.Add(_settingsPathItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _brandIcon,
            ContextMenuStrip = contextMenu,
            Text = Branding.AppName,
            Visible = true,
        };

        RebuildAudioOutputsMenu();
        RefreshStartupMenu();
        RefreshFavoritesMenu();
        RefreshRecentMenu();
        UpdateStatus();
        StartWebServer();

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000,
            Enabled = true,
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    protected override void ExitThreadCore()
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _brandIcon.Dispose();
        _displaySleepBlocker.SetPlaybackActive(false);
        _webServerService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _audioDeviceService.Dispose();
        _mpvController.Dispose();
        base.ExitThreadCore();
    }

    private ToolStripMenuItem BuildPlaybackMenu()
    {
        var playbackMenu = new ToolStripMenuItem("Playback");
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Pause", null, (_, _) => RunPlayerAction("Paused playback.", controller => controller.Pause(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Resume", null, (_, _) => RunPlayerAction("Resumed playback.", controller => controller.Resume(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Toggle Pause", null, (_, _) => RunPlayerAction("Toggled playback.", controller => controller.Toggle(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Stop", null, (_, _) => RunPlayerAction("Stopped playback.", controller => controller.Stop(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Skip", null, (_, _) => RunPlayerAction("Skipped to the next item.", controller => controller.Skip(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Quit Player", null, (_, _) => QuitPlayer()));
        return playbackMenu;
    }

    private ToolStripMenuItem BuildVolumeMenu()
    {
        var volumeMenu = new ToolStripMenuItem("Volume");
        volumeMenu.DropDownItems.Add(new ToolStripMenuItem("Louder", null, (_, _) => RunPlayerAction("Raised volume.", controller => controller.AdjustVolume(_settings, CurrentAudioDeviceId, _settings.VolumeStep))));
        volumeMenu.DropDownItems.Add(new ToolStripMenuItem("Quieter", null, (_, _) => RunPlayerAction("Lowered volume.", controller => controller.AdjustVolume(_settings, CurrentAudioDeviceId, -_settings.VolumeStep))));
        volumeMenu.DropDownItems.Add(new ToolStripMenuItem("Set Volume...", null, (_, _) => SetVolumePrompt()));
        volumeMenu.DropDownItems.Add(new ToolStripSeparator());
        volumeMenu.DropDownItems.Add(new ToolStripMenuItem("Mute", null, (_, _) => RunPlayerAction("Muted output.", controller => controller.Mute(_settings, CurrentAudioDeviceId))));
        volumeMenu.DropDownItems.Add(new ToolStripMenuItem("Unmute", null, (_, _) => RunPlayerAction("Unmuted output.", controller => controller.Unmute(_settings, CurrentAudioDeviceId))));
        return volumeMenu;
    }

    private void RebuildAudioOutputsMenu()
    {
        _audioOutputsMenuItem.DropDownItems.Clear();

        var devices = _audioDeviceService.GetOutputDevices();
        if (devices.Count == 0)
        {
            _currentOutputItem.Text = "Current output: no active outputs found";
            _audioOutputsMenuItem.DropDownItems.Add(new ToolStripMenuItem("No active outputs found")
            {
                Enabled = false,
            });
            return;
        }

        var selectedDevice = devices.FirstOrDefault(device => device.Id == _settings.SelectedAudioDeviceId);
        if (selectedDevice is null)
        {
            selectedDevice = devices[0];
            _settings.SelectedAudioDeviceId = selectedDevice.Id;
            _settingsStore.Save(_settings);
        }

        foreach (var device in devices)
        {
            var menuItem = new ToolStripMenuItem(device.FriendlyName)
            {
                Checked = device.Id == _settings.SelectedAudioDeviceId,
                Tag = device,
            };
            menuItem.Click += OnAudioOutputSelected;
            _audioOutputsMenuItem.DropDownItems.Add(menuItem);
        }

        UpdateCurrentOutput(selectedDevice);
    }

    private void RefreshFavoritesMenu()
    {
        _favoritesMenuItem.DropDownItems.Clear();
        _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("Add Favorite...", null, (_, _) => AddFavoriteFromPrompt()));
        _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("Add Clipboard To Favorites", null, (_, _) => AddClipboardToFavorites()));
        _favoritesMenuItem.DropDownItems.Add(new ToolStripSeparator());

        if (_settings.Favorites.Count == 0)
        {
            _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("No favorites yet")
            {
                Enabled = false,
            });
        }
        else
        {
            foreach (var favorite in _settings.Favorites.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var favoriteItem = new ToolStripMenuItem(Shorten(favorite.Name))
                {
                    Tag = favorite,
                };
                favoriteItem.Click += (_, _) => PlayUrl(favorite.Url, enqueue: false);
                _favoritesMenuItem.DropDownItems.Add(favoriteItem);
            }

            _favoritesMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("Clear Favorites", null, (_, _) => ClearFavorites()));
        }
    }

    private void RefreshRecentMenu()
    {
        _recentMenuItem.DropDownItems.Clear();

        if (_settings.RecentUrls.Count == 0)
        {
            _recentMenuItem.DropDownItems.Add(new ToolStripMenuItem("No recent URLs yet")
            {
                Enabled = false,
            });
            return;
        }

        foreach (var recent in _settings.RecentUrls.OrderByDescending(item => item.LastPlayedAtUtc))
        {
            var recentItem = new ToolStripMenuItem(Shorten(recent.Name))
            {
                Tag = recent,
            };
            recentItem.Click += (_, _) => PlayUrl(recent.Url, enqueue: false);
            _recentMenuItem.DropDownItems.Add(recentItem);
        }

        _recentMenuItem.DropDownItems.Add(new ToolStripSeparator());
        _recentMenuItem.DropDownItems.Add(new ToolStripMenuItem("Clear Recent", null, (_, _) => ClearRecent()));
    }

    private void RefreshStartupMenu()
    {
        _startupMenuItem.Checked = _startupRegistrationService.IsEnabled();
    }

    private void OnAudioOutputSelected(object? sender, EventArgs eventArgs)
    {
        if (sender is not ToolStripMenuItem menuItem || menuItem.Tag is not AudioOutputDevice device)
        {
            return;
        }

        _settings.SelectedAudioDeviceId = device.Id;
        _settingsStore.Save(_settings);
        UpdateCurrentOutput(device);

        foreach (ToolStripItem dropDownItem in _audioOutputsMenuItem.DropDownItems)
        {
            if (dropDownItem is ToolStripMenuItem audioItem && audioItem.Tag is AudioOutputDevice audioDevice)
            {
                audioItem.Checked = audioDevice.Id == device.Id;
            }
        }

        var appliedNow = _mpvController.TryApplyAudioOutput(_settings, device.MpvAudioDeviceId);
        ShowInfo(appliedNow
            ? $"Audio output set to {device.FriendlyName}"
            : $"Saved {device.FriendlyName}. It will be used the next time playback starts.");
        UpdateStatus();
    }

    private void PlayFromPrompt(bool enqueue)
    {
        var url = PromptForm.ShowDialog(
            null,
            enqueue ? "Queue YouTube URL" : "Play YouTube URL",
            "Enter a YouTube or YouTube Music URL:");

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        PlayUrl(url, enqueue);
    }

    private void PlayFromClipboard(bool enqueue)
    {
        if (!Clipboard.ContainsText())
        {
            ShowError("Clipboard does not contain text.");
            return;
        }

        PlayUrl(Clipboard.GetText().Trim(), enqueue);
    }

    private void PlayUrl(string url, bool enqueue)
    {
        if (!UrlValidator.IsValidYoutubeUrl(url))
        {
            ShowError("Only YouTube and YouTube Music URLs are supported.");
            return;
        }

        RunPlayerAction(
            enqueue ? "Queued URL." : "Playing URL.",
            controller => controller.Play(_settings, url, enqueue, CurrentAudioDeviceId),
            () => RecordSuccessfulPlay(url));
    }

    private void AddFavoriteFromPrompt()
    {
        var url = PromptForm.ShowDialog(
            null,
            "Add Favorite",
            "Enter a YouTube or YouTube Music URL:");

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        AddFavorite(url.Trim());
    }

    private void AddClipboardToFavorites()
    {
        if (!Clipboard.ContainsText())
        {
            ShowError("Clipboard does not contain text.");
            return;
        }

        AddFavorite(Clipboard.GetText().Trim());
    }

    private void AddFavorite(string url)
    {
        if (!UrlValidator.IsValidYoutubeUrl(url))
        {
            ShowError("Only YouTube and YouTube Music URLs are supported.");
            return;
        }

        var suggestedName = BuildFriendlyName(url);
        var name = PromptForm.ShowDialog(
            null,
            "Favorite Name",
            "Name this favorite:",
            suggestedName);

        if (name is null)
        {
            return;
        }

        UpsertFavorite(url, string.IsNullOrWhiteSpace(name) ? suggestedName : name.Trim());
        ShowInfo("Favorite saved.");
    }

    private void SetVolumePrompt()
    {
        var status = _mpvController.TryGetStatus(_settings, CurrentAudioDeviceId);
        var initialValue = status?.Volume?.ToString("0") ?? "50";
        var value = PromptForm.ShowDialog(
            null,
            "Set Volume",
            "Enter a volume level from 0 to 100:",
            initialValue);

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value, out var parsedVolume))
        {
            ShowError("Volume must be a number from 0 to 100.");
            return;
        }

        RunPlayerAction(
            $"Set volume to {Math.Clamp(parsedVolume, 0, 100):0}%.",
            controller => controller.SetVolume(_settings, CurrentAudioDeviceId, parsedVolume));
    }

    private void ToggleStartupRegistration()
    {
        try
        {
            var enable = !_startupRegistrationService.IsEnabled();
            _startupRegistrationService.SetEnabled(enable);
            RefreshStartupMenu();
            ShowInfo(enable ? "Launch at sign-in enabled." : "Launch at sign-in disabled.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            Log(ex.ToString());
        }
    }

    private void QuitPlayer()
    {
        var success = _mpvController.QuitPlayer(_settings);
        if (success)
        {
            ShowInfo("Player process told to quit.");
        }
        else
        {
            ShowError("Failed to quit the player.");
        }

        UpdateStatus();
    }

    private void ShowPlayerSettings()
    {
        using var form = new PlayerSettingsForm(_settings);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var updatedSettings = form.BuildSettings(_settings);
        var restartWebServer = updatedSettings.ApiPort != _settings.ApiPort;
        CopySettings(updatedSettings, _settings);
        _settingsStore.Save(_settings);
        _settingsPathItem.Text = $"Settings file: {_settingsStore.GetSettingsPath()}";
        if (restartWebServer)
        {
            RestartWebServer();
        }
        ShowInfo("Player settings saved.");
    }

    private void OpenPublishFolder()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "publish")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "publish")),
            Path.GetFullPath(AppContext.BaseDirectory),
        };

        var publishPath = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        Directory.CreateDirectory(publishPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = publishPath,
            UseShellExecute = true,
        });
    }

    private void OpenWebUi()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://localhost:{_settings.ApiPort}/",
            UseShellExecute = true,
        });
    }

    private void ClearFavorites()
    {
        _settings.Favorites.Clear();
        _settingsStore.Save(_settings);
        RefreshFavoritesMenu();
        ShowInfo("Favorites cleared.");
    }

    private void ClearRecent()
    {
        _settings.RecentUrls.Clear();
        _settingsStore.Save(_settings);
        RefreshRecentMenu();
        ShowInfo("Recent history cleared.");
    }

    private void UpsertFavorite(string url, string name)
    {
        var existing = _settings.Favorites.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _settings.Favorites.Add(new SavedUrlEntry
            {
                Name = name,
                Url = url,
                LastPlayedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Name = name;
            existing.LastPlayedAtUtc = DateTime.UtcNow;
        }

        _settingsStore.Save(_settings);
        RefreshFavoritesMenu();
    }

    private void RecordSuccessfulPlay(string url)
    {
        var existingRecent = _settings.RecentUrls.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existingRecent is not null)
        {
            _settings.RecentUrls.Remove(existingRecent);
        }

        _settings.RecentUrls.Insert(0, new SavedUrlEntry
        {
            Name = existingRecent?.Name ?? BuildFriendlyName(url),
            Url = url,
            LastPlayedAtUtc = DateTime.UtcNow,
        });

        if (_settings.RecentUrls.Count > _settings.MaxRecentUrls)
        {
            _settings.RecentUrls = _settings.RecentUrls.Take(_settings.MaxRecentUrls).ToList();
        }

        var favorite = _settings.Favorites.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
        if (favorite is not null)
        {
            favorite.LastPlayedAtUtc = DateTime.UtcNow;
        }

        _settingsStore.Save(_settings);
        RefreshRecentMenu();
        RefreshFavoritesMenu();
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    private void RunPlayerAction(string successMessage, Action<MpvController> action, Action? onSuccess = null)
    {
        try
        {
            action(_mpvController);
            onSuccess?.Invoke();
            ShowInfo(successMessage);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            Log(ex.ToString());
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var previousStatus = _lastObservedStatus;
        var status = _mpvController.TryGetStatus(_settings, CurrentAudioDeviceId);
        _mpvController.RecordPlaybackSnapshot(status);
        if (status is null)
        {
            _displaySleepBlocker.SetPlaybackActive(false);
            _statusItem.Text = "Status: unavailable";
            _positionItem.Text = "Position: -";
            _notifyIcon.Text = BuildTrayText("status unavailable");
            _lastObservedStatus = null;
            return;
        }

        var stateText = status.State switch
        {
            PlayerState.Playing => "playing",
            PlayerState.Paused => "paused",
            PlayerState.Idle => "idle",
            _ => "unknown",
        };

        _displaySleepBlocker.SetPlaybackActive(status.State == PlayerState.Playing);

        var title = string.IsNullOrWhiteSpace(status.MediaTitle) ? "(idle)" : status.MediaTitle;
        _statusItem.Text = $"Status: {stateText} - {Shorten(title, 48)}";
        _positionItem.Text = BuildPositionText(status);
        _notifyIcon.Text = BuildTrayText(title);
        if (_mpvController.TryFailsafeResume(_settings, CurrentAudioDeviceId, previousStatus, status))
        {
            ShowInfo("Playback dropped unexpectedly. Resuming...");
        }

        _lastObservedStatus = status;
    }

    private void UpdateCurrentOutput(AudioOutputDevice device)
    {
        _currentOutputItem.Text = $"Current output: {device.FriendlyName}";
    }

    private void ShowInfo(string message)
    {
        _notifyIcon.ShowBalloonTip(2000, Branding.AppName, message, ToolTipIcon.Info);
    }

    private void ShowError(string message)
    {
        _notifyIcon.ShowBalloonTip(2500, Branding.AppName, message, ToolTipIcon.Error);
    }

    private string CurrentAudioDeviceId
    {
        get
        {
            var device = _audioDeviceService
                .GetOutputDevices()
                .FirstOrDefault(item => item.Id == _settings.SelectedAudioDeviceId);

            if (device is not null)
            {
                return device.MpvAudioDeviceId;
            }

            return "auto";
        }
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.SelectedAudioDeviceId = source.SelectedAudioDeviceId;
        destination.MpvPath = source.MpvPath;
        destination.YtDlpPath = source.YtDlpPath;
        destination.YtDlpBrowser = source.YtDlpBrowser;
        destination.MpvPipeName = source.MpvPipeName;
        destination.MpvLogFilePath = source.MpvLogFilePath;
        destination.ApiPort = source.ApiPort;
        destination.ApiKey = source.ApiKey;
        destination.VolumeStep = source.VolumeStep;
        destination.MaxRecentUrls = source.MaxRecentUrls;
        destination.Favorites = source.Favorites;
        destination.RecentUrls = source.RecentUrls;
    }

    private void StartWebServer()
    {
        try
        {
            _webServerService.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            ShowError($"Web UI/API failed to start on port {_settings.ApiPort}: {ex.Message}");
        }
    }

    private void RestartWebServer()
    {
        try
        {
            _webServerService.RestartAsync().GetAwaiter().GetResult();
            ShowInfo($"Web UI/API restarted on port {_settings.ApiPort}.");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            ShowError($"Web UI/API restart failed: {ex.Message}");
        }
    }

    private static string BuildPositionText(PlayerStatus status)
    {
        if (status.TimePositionSeconds is null || status.DurationSeconds is null)
        {
            return "Position: -";
        }

        return $"Position: {FormatTime(status.TimePositionSeconds.Value)} / {FormatTime(status.DurationSeconds.Value)}";
    }

    private static string BuildFriendlyName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var id = uri.Query.TrimStart('?');
            return string.IsNullOrWhiteSpace(id)
                ? $"{uri.Host}{uri.AbsolutePath}"
                : $"{uri.Host} [{id}]";
        }

        return url;
    }

    private static string FormatTime(double totalSeconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private static string BuildTrayText(string detail)
    {
        return Shorten($"{Branding.AppName} - {detail}", 63);
    }

    private static string Shorten(string value, int maxLength = 40)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..(maxLength - 3)]}...";
    }

    private void Log(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WinTubeRelay");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "wintuberelay.log");
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
