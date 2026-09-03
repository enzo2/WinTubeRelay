using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WinTubeRelay.Tray;

internal sealed record AudioOutputDevice(string Id, string FriendlyName, string MpvAudioDeviceId);

internal sealed class AudioDeviceService : IDisposable
{
    private static readonly Regex GuidPattern = new(
        @"\{(?<guid>[0-9a-fA-F\-]{36})\}$",
        RegexOptions.Compiled);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceNotificationClient _notificationClient;
    private bool _disposed;

    public AudioDeviceService()
    {
        _notificationClient = new DeviceNotificationClient(NotifyOutputDevicesChanged);
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    public event EventHandler<AudioOutputsChangedEventArgs>? OutputDevicesChanged;

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var outputs = new List<AudioOutputDevice>(devices.Count);

        foreach (var device in devices)
        {
            try
            {
                outputs.Add(new AudioOutputDevice(
                    device.ID,
                    device.FriendlyName,
                    BuildMpvAudioDeviceId(device.ID)));
            }
            finally
            {
                // Enumerating on every status update must not retain COM endpoint wrappers.
                device.Dispose();
            }
        }

        return outputs
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        _enumerator.Dispose();
    }

    private void NotifyOutputDevicesChanged(string deviceId, string reason, bool availabilityChanged)
    {
        if (_disposed)
        {
            return;
        }

        OutputDevicesChanged?.Invoke(this, new AudioOutputsChangedEventArgs(deviceId, reason, availabilityChanged));
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

    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly Action<string, string, bool> _notify;

        public DeviceNotificationClient(Action<string, string, bool> notify)
        {
            _notify = notify;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            _notify(deviceId, $"state changed to {newState}", true);
        }

        public void OnDeviceAdded(string deviceId)
        {
            _notify(deviceId, "added", true);
        }

        public void OnDeviceRemoved(string deviceId)
        {
            _notify(deviceId, "removed", true);
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render)
            {
                _notify(defaultDeviceId, $"default output changed for {role}", false);
            }
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            _notify(deviceId, "properties changed", false);
        }
    }
}

internal sealed class AudioOutputsChangedEventArgs : EventArgs
{
    public AudioOutputsChangedEventArgs(string deviceId, string reason, bool availabilityChanged)
    {
        DeviceId = deviceId;
        Reason = reason;
        AvailabilityChanged = availabilityChanged;
    }

    public string DeviceId { get; }

    public string Reason { get; }

    public bool AvailabilityChanged { get; }
}
