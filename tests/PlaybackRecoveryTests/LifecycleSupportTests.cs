using WinTubeRelay.Tray;

namespace WinTubeRelay.PlaybackTests;

internal static class LifecycleSupportTests
{
    public static Task CorruptSettingsLoadUsesBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinTubeRelayTests", $"settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings
            {
                ApiPort = 4321,
                Favorites = [new SavedUrlEntry { Name = "Keep me", Url = MpvLifecycleTests.TrackUrl }],
            };
            store.Save(settings);
            store.Save(settings);
            File.WriteAllText(store.GetSettingsPath(), "{not-json");
            var loaded = store.Load();
            MpvLifecycleTests.Expect(
                (loaded.Favorites.Count == 1, $"favorites={loaded.Favorites.Count}"),
                (loaded.Favorites[0].Name == "Keep me", $"name={loaded.Favorites.FirstOrDefault()?.Name ?? "null"}"),
                (File.Exists(store.GetSettingsPath() + ".bak"), "backup missing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static Task SecondProcessDoesNotAcquireInstanceMutex()
    {
        var name = $@"Local\WinTubeRelay.Tests.{Guid.NewGuid():N}";
        var first = SingleInstance.TryAcquire(name, out var mutex);
        var second = SingleInstance.TryAcquire(name, out var other);
        mutex?.Dispose();
        other?.Dispose();
        MpvLifecycleTests.Expect(
            (first, "first acquire failed"),
            (!second, "second instance acquired the mutex"));
        return Task.CompletedTask;
    }

    public static Task WakeLockSurvivesIdleStatusDuringPreparation()
    {
        var now = DateTime.UtcNow;
        var blocker = new DisplaySleepBlocker(_ => { }, () => now);
        blocker.WakeDisplay();
        blocker.SetPlaybackActive(false);
        var heldDuringWait = blocker.WakeLockHeld;
        now = now.Add(DisplaySleepBlocker.PreparationHold).AddSeconds(1);
        blocker.SetPlaybackActive(false);
        blocker.Release();
        MpvLifecycleTests.Expect(
            (heldDuringWait, "wake lock dropped while preparation hold was active"),
            (!blocker.WakeLockHeld, "wake lock remained after the hold expired"));
        return Task.CompletedTask;
    }

    public static Task QueuedRecoveryKeepsLaterWake()
    {
        var coalescer = new RecoveryWakeCoalescer();
        var first = coalescer.TryBegin(wakeDisplay: false);
        var second = coalescer.TryBegin(wakeDisplay: true);
        var woke = coalescer.ConsumeWake();
        coalescer.Complete();
        MpvLifecycleTests.Expect(
            (first, "first recovery was not queued"),
            (!second, "second recovery should coalesce"),
            (woke, "wake requested while recovery was already queued was dropped"));
        return Task.CompletedTask;
    }

    public static Task WebUiCanSendConfiguredApiKey()
    {
        var path = FindProductionIndex();
        var html = File.ReadAllText(path);
        MpvLifecycleTests.Expect(
            (html.Contains("id=\"apiKeyInput\"", StringComparison.Ordinal), "API key field missing"),
            (html.Contains("wintuberelay-api-key", StringComparison.Ordinal), "API key storage missing"),
            (html.Contains("x-api-key", StringComparison.Ordinal), "getHeaders does not send x-api-key"));
        return Task.CompletedTask;
    }

    private static string FindProductionIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "static", "index.html");
            if (File.Exists(candidate) && File.ReadAllText(candidate).Contains("wintuberelay-api-key", StringComparison.Ordinal))
            {
                return candidate;
            }

            var sourceCandidate = Path.Combine(dir.FullName, "static", "index.html");
            if (File.Exists(sourceCandidate) && new FileInfo(sourceCandidate).Length > 1000)
            {
                return sourceCandidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Production static/index.html was not found.");
    }
}
