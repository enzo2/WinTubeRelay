using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;

namespace WinTubeRelay.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly MpvController _mpvController;
    private readonly WebServerService _webServerService;
    private readonly DisplaySleepBlocker _displaySleepBlocker;
    private readonly IPlaybackPreparer _playbackPreparer;
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
    private readonly System.Windows.Forms.Timer _outputRecoveryTimer;
    private readonly RecoveryWakeCoalescer _recoveryWake = new();
    private readonly SynchronizationContext _uiContext;
    private readonly TrayWebLifecycle _webLifecycle = new();
    private PlayerStatus? _lastObservedStatus;
    private string _currentAudioDeviceId = "auto";
    private string? _lastPlaybackErrorShown;
    private bool _isMenuOpen;
    private bool _savedMenusDirty;
    private bool _audioOutputsMenuDirty;
    private bool _statusUpdateInProgress;
    private bool _isExiting;
    private int _shellDisposed;
    private int _selectedOutputUnavailable;
    private int _outputRecoveryAttemptsRemaining;
    private const int MaxRecentHistory = 10;
    private const int OutputRecoveryAttemptCount = 6;

    public TrayApplicationContext()
    {
        _settingsStore = new SettingsStore();
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _audioDeviceService = new AudioDeviceService();
        _startupRegistrationService = new StartupRegistrationService();
        _mpvController = new MpvController(Log);
        _displaySleepBlocker = new DisplaySleepBlocker(Log);
        _settings = _settingsStore.Load();
        _outputRecoveryTimer = new System.Windows.Forms.Timer
        {
            Interval = 350,
        };
        _outputRecoveryTimer.Tick += OnOutputRecoveryTimerTick;
        _playbackPreparer = new DisplayPlaybackCoordinator(
            () => _settings.SelectedAudioDeviceId,
            () => _settings.SelectedAudioDeviceName,
            () => _audioDeviceService.GetOutputDevices(),
            () => _displaySleepBlocker.WakeDisplay(),
            InvokeOnUiThread,
            _mpvController,
            _settings,
            Log,
            onStillUnavailable: () =>
            {
                MarkSelectedOutputUnavailable();
                QueueAudioOutputRecovery("playback requested while the saved output was unavailable");
            });
        _webServerService = new WebServerService(
            () => _settings,
            () => CurrentAudioDeviceId,
            _mpvController,
            SaveSettings,
            Log,
            _playbackPreparer,
            _settingsStore);
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
        contextMenu.Opened += (_, _) => _isMenuOpen = true;
        contextMenu.Closed += (_, _) =>
        {
            _isMenuOpen = false;
            if (_audioOutputsMenuDirty)
            {
                RebuildAudioOutputsMenu();
                _audioOutputsMenuDirty = false;
            }

            if (_savedMenusDirty)
            {
                RefreshFavoritesMenu();
                RefreshRecentMenu();
                _savedMenusDirty = false;
            }
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
        contextMenu.Items.Add(new ToolStripMenuItem("Refresh Outputs", null, (_, _) => RefreshAudioOutputsAndRecover()));
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

        _audioDeviceService.OutputDevicesChanged += OnAudioOutputsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
        _statusTimer.Tick += (_, _) =>
        {
            if (_isExiting || ShellDisposed)
            {
                return;
            }

            UpdateStatus();
        };
        _statusTimer.Start();
    }

    protected override async void ExitThreadCore()
    {
        _isExiting = true;
        try
        {
            DetachShutdownSources();
            if (!await _webLifecycle.ShutdownAsync(_webServerService, DisposeShell, Log))
            {
                return;
            }

            base.ExitThreadCore();
        }
        catch (Exception ex)
        {
            try
            {
                Log(ex.ToString());
            }
            catch
            {
            }

            try
            {
                DisposeShell();
            }
            catch (Exception disposeEx)
            {
                try
                {
                    Log(disposeEx.ToString());
                }
                catch
                {
                }
            }

            try
            {
                base.ExitThreadCore();
            }
            catch
            {
            }
        }
    }

    private void DetachShutdownSources()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _audioDeviceService.OutputDevicesChanged -= OnAudioOutputsChanged;
        _statusTimer.Stop();
        _outputRecoveryTimer.Stop();
    }

    private void DisposeShell()
    {
        if (Interlocked.Exchange(ref _shellDisposed, 1) != 0)
        {
            return;
        }

        _statusTimer.Dispose();
        _outputRecoveryTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _brandIcon.Dispose();
        _displaySleepBlocker.Release();
        _audioDeviceService.Dispose();
        _mpvController.Dispose();
    }

    private bool ShellDisposed => Volatile.Read(ref _shellDisposed) != 0;

    private ToolStripMenuItem BuildPlaybackMenu()
    {
        var playbackMenu = new ToolStripMenuItem("Playback");
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Pause", null, (_, _) => RunPlayerAction("Paused playback.", controller => controller.Pause(_settings, CurrentAudioDeviceId))));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Resume", null, (_, _) => ResumePlayback()));
        playbackMenu.DropDownItems.Add(new ToolStripMenuItem("Toggle Pause", null, (_, _) => TogglePlayback()));
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

    private AudioOutputDevice? RebuildAudioOutputsMenu(bool reapplyAudioOutput = false)
    {
        _audioOutputsMenuItem.DropDownItems.Clear();

        IReadOnlyList<AudioOutputDevice> devices;
        try
        {
            devices = _audioDeviceService.GetOutputDevices();
        }
        catch (Exception ex)
        {
            Log($"Could not enumerate audio outputs: {ex.Message}");
            _currentAudioDeviceId = "auto";
            _currentOutputItem.Text = BuildUnavailableOutputText();
            _audioOutputsMenuItem.DropDownItems.Add(new ToolStripMenuItem("Audio outputs are temporarily unavailable")
            {
                Enabled = false,
            });
            MarkSelectedOutputUnavailable();
            return null;
        }

        if (devices.Count == 0)
        {
            _currentAudioDeviceId = "auto";
            _currentOutputItem.Text = BuildUnavailableOutputText();
            _audioOutputsMenuItem.DropDownItems.Add(new ToolStripMenuItem("No active outputs found")
            {
                Enabled = false,
            });
            MarkSelectedOutputUnavailable();
            return null;
        }

        var selectedDevice = ResolveSelectedAudioDevice(devices);

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

        if (selectedDevice is null)
        {
            _currentAudioDeviceId = "auto";
            _currentOutputItem.Text = BuildUnavailableOutputText();
            MarkSelectedOutputUnavailable();
        }
        else
        {
            UpdateCurrentOutput(selectedDevice);
            _currentAudioDeviceId = selectedDevice.MpvAudioDeviceId;

            var hadBeenUnavailable = reapplyAudioOutput
                && Volatile.Read(ref _selectedOutputUnavailable) == 1;
            if (hadBeenUnavailable)
            {
                var deviceId = selectedDevice.MpvAudioDeviceId;
                var friendlyName = selectedDevice.FriendlyName;
                Task.Run(() =>
                {
                    if (!_mpvController.IsPlayerRunning(_settings))
                    {
                        _uiContext.Post(_ => Interlocked.Exchange(ref _selectedOutputUnavailable, 0), null);
                        return;
                    }

                    var reapplied = _mpvController.TryApplyAudioOutput(_settings, deviceId, forceReinitialize: true);
                    _uiContext.Post(_ =>
                    {
                        if (reapplied)
                        {
                            Interlocked.Exchange(ref _selectedOutputUnavailable, 0);
                        }

                        Log(reapplied
                            ? $"Recovered saved audio output '{friendlyName}'."
                            : $"Saved audio output '{friendlyName}' is back, but mpv is not ready to reapply it yet.");
                    }, null);
                });
            }
        }

        return selectedDevice;
    }

    private void RefreshFavoritesMenu()
    {
        _favoritesMenuItem.DropDownItems.Clear();
        _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("Add Favorite...", null, (_, _) => AddFavoriteFromPrompt()));
        _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("Add Clipboard To Favorites", null, (_, _) => AddClipboardToFavorites()));
        _favoritesMenuItem.DropDownItems.Add(new ToolStripSeparator());

        if (_settingsStore.CopyFavorites(_settings).Count == 0)
        {
            _favoritesMenuItem.DropDownItems.Add(new ToolStripMenuItem("No favorites yet")
            {
                Enabled = false,
            });
        }
        else
        {
            foreach (var favorite in _settingsStore.CopyFavorites(_settings).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
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

        var recentItems = _settingsStore.CopyRecent(_settings)
            .OrderByDescending(item => item.LastPlayedAtUtc)
            .Take(MaxRecentHistory)
            .ToList();
        if (recentItems.Count == 0)
        {
            _recentMenuItem.DropDownItems.Add(new ToolStripMenuItem("No recent URLs yet")
            {
                Enabled = false,
            });
            return;
        }

        foreach (var recent in recentItems)
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

        _settingsStore.Update(_settings, current =>
        {
            current.SelectedAudioDeviceId = device.Id;
            current.SelectedAudioDeviceName = device.FriendlyName;
        });
        _currentAudioDeviceId = device.MpvAudioDeviceId;
        UpdateCurrentOutput(device);

        foreach (ToolStripItem dropDownItem in _audioOutputsMenuItem.DropDownItems)
        {
            if (dropDownItem is ToolStripMenuItem audioItem && audioItem.Tag is AudioOutputDevice audioDevice)
            {
                audioItem.Checked = audioDevice.Id == device.Id;
            }
        }

        Task.Run(() =>
        {
            var appliedNow = _mpvController.TryApplyAudioOutput(_settings, device.MpvAudioDeviceId);
            _uiContext.Post(_ =>
            {
                ShowInfo(appliedNow
                    ? $"Audio output set to {device.FriendlyName}"
                    : $"Saved {device.FriendlyName}. It will be used the next time playback starts.");
                UpdateStatus();
            }, null);
        });
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

        Task.Run(() =>
        {
            try
            {
                _mpvController.InvokeExclusive(() =>
                {
                    var prepared = _playbackPreparer.Prepare(enqueue ? PlaybackIntent.Enqueue : PlaybackIntent.Play);
                    _mpvController.Play(_settings, url, enqueue, prepared);
                });
                _uiContext.Post(_ =>
                {
                    ShowInfo(enqueue ? "Queued URL." : "Playback request sent.");
                    UpdateStatus();
                }, null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ =>
                {
                    ShowError(ex.Message);
                    Log(ex.ToString());
                }, null);
            }
        });
    }

    private void ResumePlayback()
    {
        Task.Run(() =>
        {
            try
            {
                _mpvController.InvokeExclusive(() =>
                {
                    var prepared = _playbackPreparer.Prepare(PlaybackIntent.Resume);
                    _mpvController.Resume(_settings, prepared);
                });
                _uiContext.Post(_ =>
                {
                    ShowInfo("Resumed playback.");
                    UpdateStatus();
                }, null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ =>
                {
                    ShowError(ex.Message);
                    Log(ex.ToString());
                }, null);
            }
        });
    }

    private void TogglePlayback()
    {
        Task.Run(() =>
        {
            try
            {
                _mpvController.InvokeExclusive(() =>
                {
                    var snapshot = _mpvController.GetApiStatusSnapshot(_settings, CurrentAudioDeviceId);
                    if (!TogglePreparation.NeedsWake(snapshot))
                    {
                        _mpvController.Toggle(_settings, CurrentAudioDeviceId);
                        return;
                    }

                    var prepared = _playbackPreparer.Prepare(PlaybackIntent.Resume);
                    _mpvController.Toggle(_settings, prepared);
                });
                _uiContext.Post(_ => ShowInfo("Toggled playback."), null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ =>
                {
                    ShowError(ex.Message);
                    Log(ex.ToString());
                }, null);
            }
        });
    }

    private void InvokeOnUiThread(Action action)
    {
        if (ShellDisposed)
        {
            return;
        }

        if (SynchronizationContext.Current == _uiContext)
        {
            if (!ShellDisposed)
            {
                action();
            }

            return;
        }

        _uiContext.Send(_ =>
        {
            if (!ShellDisposed)
            {
                action();
            }
        }, null);
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
        Task.Run(() =>
        {
            var success = _mpvController.QuitPlayer(_settings);
            _uiContext.Post(_ =>
            {
                if (success)
                {
                    ShowInfo("Player process told to quit.");
                }
                else
                {
                    ShowError("Failed to quit the player.");
                }

                UpdateStatus();
            }, null);
        });
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
        _settingsStore.ReplaceFrom(_settings, updatedSettings);
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
        _settingsStore.Update(_settings, current => current.Favorites.Clear());
        RefreshSavedMenus();
        ShowInfo("Favorites cleared.");
    }

    private void ClearRecent()
    {
        _settingsStore.Update(_settings, current => current.RecentUrls.Clear());
        RefreshSavedMenus();
        ShowInfo("Recent history cleared.");
    }

    private void UpsertFavorite(string url, string name)
    {
        _settingsStore.Update(_settings, current =>
        {
            var existing = current.Favorites.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                current.Favorites.Add(new SavedUrlEntry
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
        });
        RefreshSavedMenus();
    }

    private void RecordNowPlayingRecent(PlayerStatus status)
    {
        if (status.State != PlayerState.Playing || string.IsNullOrWhiteSpace(status.MediaTitle))
        {
            return;
        }

        var url = status.Path;
        if (string.IsNullOrWhiteSpace(url) || !UrlValidator.IsValidYoutubeUrl(url))
        {
            url = _mpvController.CurrentSourceUrl;
        }

        if (string.IsNullOrWhiteSpace(url) || !UrlValidator.IsValidYoutubeUrl(url))
        {
            return;
        }

        RecordSuccessfulPlay(url, status.MediaTitle);
    }

    private void RecordSuccessfulPlay(string url, string? name = null)
    {
        var changed = false;
        _settingsStore.UpdateIfChanged(_settings, current =>
        {
            var existingRecent = current.RecentUrls.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            var resolvedName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : existingRecent?.Name ?? BuildFriendlyName(url);
            if (existingRecent is not null
                && ReferenceEquals(existingRecent, current.RecentUrls.FirstOrDefault())
                && string.Equals(existingRecent.Name, resolvedName, StringComparison.Ordinal))
            {
                return false;
            }

            if (existingRecent is not null)
            {
                current.RecentUrls.Remove(existingRecent);
            }

            current.RecentUrls.Insert(0, new SavedUrlEntry
            {
                Name = resolvedName,
                Url = url,
                LastPlayedAtUtc = DateTime.UtcNow,
            });

            if (current.RecentUrls.Count > MaxRecentHistory)
            {
                current.RecentUrls = current.RecentUrls.Take(MaxRecentHistory).ToList();
            }

            var favorite = current.Favorites.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            if (favorite is not null)
            {
                favorite.LastPlayedAtUtc = DateTime.UtcNow;
            }

            changed = true;
            return true;
        });
        if (changed)
        {
            RefreshSavedMenus();
        }
    }

    private void RefreshSavedMenus()
    {
        if (_isMenuOpen)
        {
            _savedMenusDirty = true;
            return;
        }

        RefreshFavoritesMenu();
        RefreshRecentMenu();
        _savedMenusDirty = false;
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    private void RunPlayerAction(string successMessage, Action<MpvController> action, Action? onSuccess = null)
    {
        Task.Run(() =>
        {
            try
            {
                _mpvController.InvokeExclusive(() => action(_mpvController));
                _uiContext.Post(_ =>
                {
                    onSuccess?.Invoke();
                    ShowInfo(successMessage);
                    UpdateStatus();
                }, null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ =>
                {
                    ShowError(ex.Message);
                    Log(ex.ToString());
                    UpdateStatus();
                }, null);
            }
        });
    }

    private void UpdateStatus()
    {
        if (_statusUpdateInProgress)
        {
            return;
        }

        _statusUpdateInProgress = true;
        var previousStatus = _lastObservedStatus;
        var audioDeviceId = CurrentAudioDeviceId;

        Task.Run(() =>
        {
            var status = _mpvController.TryGetStatus(_settings, audioDeviceId);
            _mpvController.RecordPlaybackSnapshot(status);
            var resumed = status is not null
                && _mpvController.TryFailsafeResume(_settings, audioDeviceId, previousStatus, status);
            return (status, resumed);
        }).ContinueWith(task =>
        {
            _uiContext.Post(_ =>
            {
                if (ShellDisposed)
                {
                    _statusUpdateInProgress = false;
                    return;
                }

                try
                {
                    if (task.IsFaulted)
                    {
                        Log(task.Exception?.GetBaseException().ToString() ?? "Status update failed.");
                        ApplyStatus(null, resumed: false);
                    }
                    else
                    {
                        ApplyStatus(task.Result.status, task.Result.resumed);
                    }
                }
                finally
                {
                    _statusUpdateInProgress = false;
                }
            }, null);
        });
    }

    private void ApplyStatus(PlayerStatus? status, bool resumed)
    {
        if (ShellDisposed)
        {
            return;
        }

        if (status is null)
        {
            _displaySleepBlocker.SetPlaybackActive(false);
            if (!_isMenuOpen)
            {
                _statusItem.Text = "Status: unavailable";
                _positionItem.Text = "Position: -";
                _notifyIcon.Text = BuildTrayText("status unavailable");
            }

            _lastObservedStatus = null;
            return;
        }

        var stateText = status.State switch
        {
            PlayerState.Playing => "playing",
            PlayerState.Paused => "paused",
            PlayerState.Loading => "loading",
            PlayerState.Idle => "idle",
            _ => "unknown",
        };

        _displaySleepBlocker.SetPlaybackActive(status.State is PlayerState.Playing or PlayerState.Loading);

        var title = string.IsNullOrWhiteSpace(status.MediaTitle) ? "(idle)" : status.MediaTitle;
        if (!_isMenuOpen)
        {
            _statusItem.Text = $"Status: {stateText} - {Shorten(title, 48)}";
            _positionItem.Text = BuildPositionText(status);
            _notifyIcon.Text = BuildTrayText(title);
        }

        RecordNowPlayingRecent(status);
        SurfacePlaybackError();
        if (resumed)
        {
            ShowInfo("Playback dropped unexpectedly. Resuming...");
        }

        _lastObservedStatus = status;
    }

    private void SurfacePlaybackError()
    {
        if (_mpvController.ConsumeForcedAudioDeviceFailure())
        {
            MarkSelectedOutputUnavailable();
            QueueAudioOutputRecovery("mpv reported that the selected audio output failed");
            ShowError("Selected audio output failed. It will be retried automatically when it returns.");
            return;
        }

        var playbackError = _mpvController.LastPlaybackError;
        if (string.IsNullOrWhiteSpace(playbackError))
        {
            _lastPlaybackErrorShown = null;
            return;
        }

        if (string.Equals(playbackError, _lastPlaybackErrorShown, StringComparison.Ordinal))
        {
            return;
        }

        _lastPlaybackErrorShown = playbackError;
        ShowError($"Playback error: {playbackError}");
    }

    private void OnAudioOutputsChanged(object? sender, AudioOutputsChangedEventArgs eventArgs)
    {
        if (_isExiting)
        {
            return;
        }

        if (eventArgs.AvailabilityChanged
            && string.Equals(eventArgs.DeviceId, _settings.SelectedAudioDeviceId, StringComparison.Ordinal))
        {
            MarkSelectedOutputUnavailable();
        }

        QueueAudioOutputRecovery($"Windows audio output {eventArgs.Reason}");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode != PowerModes.Resume || _isExiting)
        {
            return;
        }

        MarkSelectedOutputUnavailable();
        QueueAudioOutputRecovery(
            "Windows resumed",
            wakeDisplay: IsDisplayBackedAudioOutput(_settings.SelectedAudioDeviceName) && IsPlaybackActive());
    }

    private void RefreshAudioOutputsAndRecover()
    {
        MarkSelectedOutputUnavailable();
        RebuildAudioOutputsMenu(reapplyAudioOutput: true);
    }

    private void QueueAudioOutputRecovery(string reason, bool wakeDisplay = false)
    {
        if (_isExiting || !_recoveryWake.TryBegin(wakeDisplay))
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (_isExiting)
            {
                _recoveryWake.Complete();
                return;
            }

            if (_recoveryWake.ConsumeWake())
            {
                _displaySleepBlocker.WakeDisplay();
            }

            while (_recoveryWake.Complete())
            {
                _displaySleepBlocker.WakeDisplay();
            }
            if (_outputRecoveryTimer.Enabled)
            {
                return;
            }

            Log($"Scheduling audio output recovery after {reason}.");
            _outputRecoveryAttemptsRemaining = OutputRecoveryAttemptCount;
            _outputRecoveryTimer.Interval = 350;
            _outputRecoveryTimer.Start();
        }, null);
    }

    private void OnOutputRecoveryTimerTick(object? sender, EventArgs eventArgs)
    {
        if (_isExiting || ShellDisposed)
        {
            _outputRecoveryTimer.Stop();
            return;
        }

        _outputRecoveryTimer.Stop();

        if (_isMenuOpen)
        {
            _audioOutputsMenuDirty = true;
            if (_outputRecoveryAttemptsRemaining > 1)
            {
                _outputRecoveryAttemptsRemaining--;
                _outputRecoveryTimer.Interval = 1000;
                _outputRecoveryTimer.Start();
            }

            return;
        }

        var selectedDevice = RebuildAudioOutputsMenu(reapplyAudioOutput: true);
        var recoveryStillPending = Volatile.Read(ref _selectedOutputUnavailable) == 1;
        if ((!recoveryStillPending && selectedDevice is not null) || _outputRecoveryAttemptsRemaining <= 1)
        {
            return;
        }

        _outputRecoveryAttemptsRemaining--;
        _outputRecoveryTimer.Interval = 1000;
        _outputRecoveryTimer.Start();
    }

    private void MarkSelectedOutputUnavailable()
    {
        if (!string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceId)
            || !string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceName))
        {
            Interlocked.Exchange(ref _selectedOutputUnavailable, 1);
        }
    }

    private bool IsPlaybackActive()
    {
        return _lastObservedStatus?.State is PlayerState.Playing or PlayerState.Loading;
    }

    private void UpdateCurrentOutput(AudioOutputDevice device)
    {
        _currentOutputItem.Text = $"Current output: {device.FriendlyName}";
    }

    private string BuildUnavailableOutputText()
    {
        var savedName = _settings.SelectedAudioDeviceName;
        return string.IsNullOrWhiteSpace(savedName)
            ? "Current output: none selected"
            : $"Current output: {savedName} (unavailable)";
    }

    private static bool IsDisplayBackedAudioOutput(string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName))
        {
            return false;
        }

        return DisplayOutputClassifier.IsDisplayBacked(outputName);
    }

    private void ShowInfo(string message)
    {
        if (ShellDisposed)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(2000, Branding.AppName, message, ToolTipIcon.Info);
    }

    private void ShowError(string message)
    {
        if (ShellDisposed)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(2500, Branding.AppName, message, ToolTipIcon.Error);
    }

    private string CurrentAudioDeviceId
    {
        get => ResolveCurrentAudioDeviceId();
    }

    private string ResolveCurrentAudioDeviceId()
    {
        if (string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceId)
            && string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceName))
        {
            _currentAudioDeviceId = "auto";
            return _currentAudioDeviceId;
        }

        IReadOnlyList<AudioOutputDevice> devices;
        try
        {
            devices = _audioDeviceService.GetOutputDevices();
        }
        catch (Exception ex)
        {
            Log($"Could not refresh audio outputs; keeping saved output selection and using auto for this request: {ex.Message}");
            _currentAudioDeviceId = "auto";
            return _currentAudioDeviceId;
        }

        var selectedDevice = ResolveSelectedAudioDevice(devices);
        if (selectedDevice is not null)
        {
            _currentAudioDeviceId = selectedDevice.MpvAudioDeviceId;
            if (Volatile.Read(ref _selectedOutputUnavailable) == 1)
            {
                QueueAudioOutputRecovery("the saved audio output became available");
            }

            return _currentAudioDeviceId;
        }

        _currentAudioDeviceId = "auto";
        MarkSelectedOutputUnavailable();
        Log($"Saved audio output is unavailable (id={_settings.SelectedAudioDeviceId ?? "(none)"}, name={_settings.SelectedAudioDeviceName ?? "(none)"}); keeping it saved and using auto for this request.");
        RefreshAudioOutputsMenuSoon();
        return _currentAudioDeviceId;
    }

    private AudioOutputDevice? ResolveSelectedAudioDevice(IReadOnlyList<AudioOutputDevice> devices)
    {
        var selectedDevice = !string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceId)
            ? devices.FirstOrDefault(device => device.Id == _settings.SelectedAudioDeviceId)
            : null;
        if (selectedDevice is not null)
        {
            SaveSelectedAudioDevice(selectedDevice);
            return selectedDevice;
        }

        selectedDevice = !string.IsNullOrWhiteSpace(_settings.SelectedAudioDeviceName)
            ? devices.FirstOrDefault(device => string.Equals(device.FriendlyName, _settings.SelectedAudioDeviceName, StringComparison.CurrentCultureIgnoreCase))
            : null;
        if (selectedDevice is not null)
        {
            Log($"Saved audio output ID changed; matched by name '{selectedDevice.FriendlyName}'.");
            SaveSelectedAudioDevice(selectedDevice);
            return selectedDevice;
        }

        return null;
    }

    private void SaveSelectedAudioDevice(AudioOutputDevice device)
    {
        if (string.Equals(_settings.SelectedAudioDeviceId, device.Id, StringComparison.Ordinal)
            && string.Equals(_settings.SelectedAudioDeviceName, device.FriendlyName, StringComparison.Ordinal))
        {
            return;
        }

        _settingsStore.Update(_settings, current =>
        {
            current.SelectedAudioDeviceId = device.Id;
            current.SelectedAudioDeviceName = device.FriendlyName;
        });
    }

    private void RefreshAudioOutputsMenuSoon()
    {
        QueueAudioOutputRecovery("the saved audio output is unavailable");
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

    private async void RestartWebServer()
    {
        try
        {
            var error = await _webLifecycle.RestartAsync(_webServerService, Log);
            if (ShellDisposed)
            {
                return;
            }

            if (error is null)
            {
                ShowInfo($"Web UI/API restarted on port {_settings.ApiPort}.");
            }
            else
            {
                ShowError($"Web UI/API restart failed: {error.Message}");
            }
        }
        catch (Exception ex)
        {
            try
            {
                Log(ex.ToString());
            }
            catch
            {
            }

            if (!ShellDisposed)
            {
                ShowError($"Web UI/API restart failed: {ex.Message}");
            }
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
            var logPath = Path.Combine(logDirectory, "wintuberelay.log");
            LogFileRetention.AppendLine(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
