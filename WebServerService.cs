using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace WinTubeRelay.Tray;

internal sealed class WebServerService : IAsyncDisposable
{
    private const int MaxRecentHistory = 10;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<string> _getCurrentAudioDeviceId;
    private readonly IMpvPlayback _mpvController;
    private readonly Action _saveSettings;
    private readonly Action<string> _log;
    private readonly IPlaybackPreparer? _preparePlayback;
    private readonly SettingsStore? _settingsStore;
    private WebApplication? _app;

    public WebServerService(
        Func<AppSettings> getSettings,
        Func<string> getCurrentAudioDeviceId,
        IMpvPlayback mpvController,
        Action saveSettings,
        Action<string> log,
        IPlaybackPreparer? preparePlayback = null,
        SettingsStore? settingsStore = null)
    {
        _getSettings = getSettings;
        _getCurrentAudioDeviceId = getCurrentAudioDeviceId;
        _mpvController = mpvController;
        _saveSettings = saveSettings;
        _log = log;
        _preparePlayback = preparePlayback;
        _settingsStore = settingsStore;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var settings = _getSettings();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{settings.ApiPort}");

        var app = builder.Build();
        ConfigurePipeline(app);
        await RunWithoutCapturedContext(() => app.StartAsync(cancellationToken)).ConfigureAwait(false);
        _app = app;
        _log($"Web UI/API listening on port {settings.ApiPort}.");
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        await RunWithoutCapturedContext(() => _app.StopAsync(cancellationToken)).ConfigureAwait(false);
        await RunWithoutCapturedContext(() => _app.DisposeAsync().AsTask()).ConfigureAwait(false);
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private static Task RunWithoutCapturedContext(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        if (previous is null)
        {
            return start();
        }

        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void ConfigurePipeline(WebApplication app)
    {
        var staticPath = ResolveStaticPath();
        var fileProvider = new PhysicalFileProvider(staticPath);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = "/static",
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
        });

        app.MapGet("/", async context =>
        {
            var indexPath = Path.Combine(staticPath, "index.html");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath);
        });

        app.MapGet("/health", () => Results.Json(new { ok = true }));

        app.MapPost("/play", async (HttpContext context, PlayRequest request) =>
        {
            var unauthorized = ValidateApiKey(context, out var settings);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            if (!UrlValidator.IsValidYoutubeUrl(request.Url))
            {
                return Error(400, "Invalid YouTube URL");
            }

            try
            {
                _mpvController.InvokeExclusive(() =>
                {
                    var prepared = PrepareAudioDevice(request.Enqueue ? PlaybackIntent.Enqueue : PlaybackIntent.Play);
                    _mpvController.Play(settings, request.Url, request.Enqueue, prepared);
                });
                return Results.Json(new { ok = true, url = request.Url, enqueue = request.Enqueue, mpv = (object?)null });
            }
            catch (Exception ex)
            {
                _log(ex.ToString());
                return Error(500, ex.Message);
            }
        });

        app.MapPost("/stop", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Stop(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/pause", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Pause(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/resume", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.InvokeExclusive(() =>
            {
                var prepared = PrepareAudioDevice(PlaybackIntent.Resume);
                _mpvController.Resume(settings, prepared);
            });
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/toggle", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.InvokeExclusive(() =>
            {
                var snapshot = _mpvController.GetApiStatusSnapshot(settings, _getCurrentAudioDeviceId());
                var deviceId = TogglePreparation.NeedsWake(snapshot)
                    ? PrepareAudioDevice(PlaybackIntent.Resume)
                    : _getCurrentAudioDeviceId();
                _mpvController.Toggle(settings, deviceId);
            });
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/skip", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Skip(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/quit", (HttpContext context) => Execute(context, settings =>
        {
            if (!_mpvController.IsPlayerRunning(settings))
            {
                return Results.Json(new { ok = true, message = "mpv not running" });
            }

            var success = _mpvController.QuitPlayer(settings);
            return success
                ? Results.Json(new { ok = true, message = "mpv quit command sent" })
                : Error(500, "Failed to quit mpv");
        }));

        app.MapGet("/status", (HttpContext context) => Execute(context, settings =>
        {
            var snapshot = _mpvController.GetApiStatusSnapshot(settings, _getCurrentAudioDeviceId());
            return Results.Json(new
            {
                ok = true,
                player_state = snapshot.PlayerState,
                props = snapshot.Properties,
                errors = snapshot.Errors.Count == 0 ? null : snapshot.Errors,
                playback_error = _mpvController.LastPlaybackError,
            });
        }));

        app.MapGet("/volume", (HttpContext context) => Execute(context, settings =>
        {
            var snapshot = _mpvController.GetVolumeSnapshot(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, volume = snapshot.Volume, mute = snapshot.Mute });
        }));

        app.MapPost("/volume", (HttpContext context, VolumeRequest request) => Execute(context, settings =>
        {
            _mpvController.SetVolume(settings, _getCurrentAudioDeviceId(), request.Level);
            return Results.Json(new { ok = true, mpv = (object?)null, volume = request.Level });
        }));

        app.MapPost("/mute", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Mute(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/unmute", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Unmute(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapGet("/favorites", (HttpContext context) => Execute(context, settings =>
        {
            return Results.Json(new
            {
                ok = true,
                items = FavoritesOf(settings)
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(item => new
                    {
                        name = item.Name,
                        url = item.Url,
                        last_played_at_utc = item.LastPlayedAtUtc,
                    }),
            });
        }));

        app.MapPost("/favorites", (HttpContext context, FavoriteUpsertRequest request) => Execute(context, settings =>
        {
            if (!UrlValidator.IsValidYoutubeUrl(request.Url))
            {
                return Error(400, "Invalid YouTube URL");
            }

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? BuildFriendlyName(request.Url)
                : request.Name.Trim();
            Mutate(settings, current =>
            {
                var existing = current.Favorites.FirstOrDefault(item =>
                    string.Equals(item.Url, request.Url, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    current.Favorites.Add(new SavedUrlEntry
                    {
                        Name = name,
                        Url = request.Url,
                        LastPlayedAtUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Name = name;
                    existing.LastPlayedAtUtc = DateTime.UtcNow;
                }
            });
            return Results.Json(new { ok = true, name, url = request.Url });
        }));

        app.MapPost("/favorites/remove", (HttpContext context, UrlOnlyRequest request) => Execute(context, settings =>
        {
            Mutate(settings, current => current.Favorites.RemoveAll(item => string.Equals(item.Url, request.Url, StringComparison.OrdinalIgnoreCase)));
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/favorites/clear", (HttpContext context) => Execute(context, settings =>
        {
            Mutate(settings, current => current.Favorites.Clear());
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/recent", (HttpContext context) => Execute(context, settings =>
        {
            return Results.Json(new
            {
                ok = true,
                items = RecentOf(settings)
                    .OrderByDescending(item => item.LastPlayedAtUtc)
                    .Take(MaxRecentHistory)
                    .Select(item => new
                    {
                        name = item.Name,
                        url = item.Url,
                        last_played_at_utc = item.LastPlayedAtUtc,
                    }),
            });
        }));

        app.MapPost("/recent/clear", (HttpContext context) => Execute(context, settings =>
        {
            Mutate(settings, current => current.RecentUrls.Clear());
            return Results.Json(new { ok = true });
        }));
    }

    private void Mutate(AppSettings settings, Action<AppSettings> mutate)
    {
        if (_settingsStore is not null)
        {
            _settingsStore.Update(settings, mutate);
            return;
        }

        mutate(settings);
        _saveSettings();
    }

    private IReadOnlyList<SavedUrlEntry> FavoritesOf(AppSettings settings)
    {
        return _settingsStore?.CopyFavorites(settings) ?? settings.Favorites.ToList();
    }

    private IReadOnlyList<SavedUrlEntry> RecentOf(AppSettings settings)
    {
        return _settingsStore?.CopyRecent(settings) ?? settings.RecentUrls.ToList();
    }

    private string PrepareAudioDevice(PlaybackIntent intent)
    {
        if (_preparePlayback is null)
        {
            return _getCurrentAudioDeviceId();
        }

        return _preparePlayback.Prepare(intent);
    }

    private IResult Execute(HttpContext context, Func<AppSettings, IResult> action)
    {
        var unauthorized = ValidateApiKey(context, out var settings);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            return action(settings);
        }
        catch (Exception ex)
        {
            _log(ex.ToString());
            return Error(500, ex.Message);
        }
    }

    private IResult? ValidateApiKey(HttpContext context, out AppSettings settings)
    {
        settings = _getSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return null;
        }

        if (context.Request.Headers.TryGetValue("x-api-key", out var headerValue)
            && string.Equals(headerValue.ToString(), settings.ApiKey, StringComparison.Ordinal))
        {
            return null;
        }

        return Error(401, "Unauthorized");
    }

    private static IResult Error(int statusCode, string detail)
    {
        return Results.Json(new { detail }, statusCode: statusCode);
    }

    private static string ResolveStaticPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "static"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "static")),
        };

        var staticPath = candidates.FirstOrDefault(Directory.Exists);
        if (staticPath is null)
        {
            throw new DirectoryNotFoundException("The web UI static folder could not be found.");
        }

        return staticPath;
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

    private sealed record PlayRequest(string Url, bool Enqueue);

    private sealed record FavoriteUpsertRequest(string Url, string? Name);

    private sealed record UrlOnlyRequest(string Url);

    private sealed record VolumeRequest(double Level);
}
