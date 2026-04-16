namespace WinTubeRelay.Tray;

internal sealed class SavedUrlEntry
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTime LastPlayedAtUtc { get; set; } = DateTime.UtcNow;
}
