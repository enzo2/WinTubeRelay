using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace WinTubeRelay.Tray;

internal sealed record AudioOutputDevice(string Id, string FriendlyName, string MpvAudioDeviceId);

internal sealed class AudioDeviceService : IDisposable
{
    private static readonly Regex GuidPattern = new(
        @"\{(?<guid>[0-9a-fA-F\-]{36})\}$",
        RegexOptions.Compiled);

    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices
            .Select(device => new AudioOutputDevice(
                device.ID,
                device.FriendlyName,
                BuildMpvAudioDeviceId(device.ID)))
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }

    private static string BuildMpvAudioDeviceId(string endpointId)
    {
        var match = GuidPattern.Match(endpointId);
        if (!match.Success)
        {
            return "auto";
        }

        return $"wasapi/{{{match.Groups["guid"].Value.ToLowerInvariant()}}}";
    }
}
