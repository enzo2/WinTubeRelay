using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace WinTubeRelay.Tray;

internal sealed class WebServerService : IAsyncDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<string> _getCurrentAudioDeviceId;
    private readonly MpvController _mpvController;
    private readonly Action _saveSettings;
    private readonly Action<string> _recordSuccessfulPlay;
    private readonly Action<string> _log;
    private WebApplication? _app;

    public WebServerService(
        Func<AppSettings> getSettings,
        Func<string> getCurrentAudioDeviceId,
        MpvController mpvController,
        Action saveSettings,
        Action<string> recordSuccessfulPlay,
        Action<string> log)
    {
        _getSettings = getSettings;
        _getCurrentAudioDeviceId = getCurrentAudioDeviceId;
        _mpvController = mpvController;
        _saveSettings = saveSettings;
        _recordSuccessfulPlay = recordSuccessfulPlay;
        _log = log;
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
        await app.StartAsync(cancellationToken);
        _app = app;
        _log($"Web UI/API listening on port {settings.ApiPort}.");
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
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
                _mpvController.Play(settings, request.Url, request.Enqueue, _getCurrentAudioDeviceId());
                _recordSuccessfulPlay(request.Url);
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
            _mpvController.Resume(settings, _getCurrentAudioDeviceId());
            return Results.Json(new { ok = true, mpv = (object?)null });
        }));

        app.MapPost("/toggle", (HttpContext context) => Execute(context, settings =>
        {
            _mpvController.Toggle(settings, _getCurrentAudioDeviceId());
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
                items = settings.Favorites
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
            var existing = settings.Favorites.FirstOrDefault(item =>
                string.Equals(item.Url, request.Url, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                settings.Favorites.Add(new SavedUrlEntry
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

            _saveSettings();
            return Results.Json(new { ok = true, name, url = request.Url });
        }));

        app.MapPost("/favorites/remove", (HttpContext context, UrlOnlyRequest request) => Execute(context, settings =>
        {
            settings.Favorites.RemoveAll(item => string.Equals(item.Url, request.Url, StringComparison.OrdinalIgnoreCase));
            _saveSettings();
            return Results.Json(new { ok = true });
        }));

        app.MapPost("/favorites/clear", (HttpContext context) => Execute(context, settings =>
        {
            settings.Favorites.Clear();
            _saveSettings();
            return Results.Json(new { ok = true });
        }));

        app.MapGet("/recent", (HttpContext context) => Execute(context, settings =>
        {
            return Results.Json(new
            {
                ok = true,
                items = settings.RecentUrls
                    .OrderByDescending(item => item.LastPlayedAtUtc)
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
            settings.RecentUrls.Clear();
            _saveSettings();
            return Results.Json(new { ok = true });
        }));
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
