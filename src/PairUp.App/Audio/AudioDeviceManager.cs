using NAudio.CoreAudioApi;

namespace PairUp.App.Audio;

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Unplugged);

        var result = new List<AudioDeviceInfo>();
        foreach (var device in devices)
        {
            result.Add(new AudioDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName,
                Kind = ClassifyDevice(device),
                State = device.State
            });
        }

        return result;
    }

    public string GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
    }

    private static DeviceKind ClassifyDevice(MMDevice device)
    {
        var name = device.FriendlyName.ToLowerInvariant();

        if (name.Contains("bluetooth") || name.Contains("hands-free") || name.Contains("bt "))
            return DeviceKind.Bluetooth;

        if (name.Contains("speaker") || name.Contains("soundbar") || name.Contains("hdmi") || name.Contains("optical"))
            return DeviceKind.Speakers;

        if (name.Contains("headphone") || name.Contains("headset") || name.Contains("jack") || name.Contains("line out"))
            return DeviceKind.Wired;

        return DeviceKind.Other;
    }
}
