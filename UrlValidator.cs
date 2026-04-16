using System.Text.RegularExpressions;

namespace WinTubeRelay.Tray;

internal static partial class UrlValidator
{
    [GeneratedRegex(@"^(https?://)?(www\.)?(youtube\.com|music\.youtube\.com|youtu\.be)/.+$", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeRegex();

    public static bool IsValidYoutubeUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) && YoutubeRegex().IsMatch(url);
    }
}
