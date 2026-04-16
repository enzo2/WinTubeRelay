namespace WinTubeRelay.Tray;

internal sealed class AppSettings
{
    public string? SelectedAudioDeviceId { get; set; }

    public string MpvPath { get; set; } = string.Empty;

    public string? YtDlpPath { get; set; }

    public string? YtDlpBrowser { get; set; } = "firefox";

    public string MpvPipeName { get; set; } = "wintuberelay-mpv";

    public string? MpvLogFilePath { get; set; } = "wintuberelay_mpv.log";

    public int ApiPort { get; set; } = 8765;

    public string? ApiKey { get; set; }

    public int VolumeStep { get; set; } = 10;

    public int MaxRecentUrls { get; set; } = 8;

    public List<SavedUrlEntry> Favorites { get; set; } = [];

    public List<SavedUrlEntry> RecentUrls { get; set; } = [];
}
