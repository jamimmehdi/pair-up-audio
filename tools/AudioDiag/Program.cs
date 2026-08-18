using NAudio.CoreAudioApi;

// Lists every active output device with its driver-reported period floors AND how PairUp's
// name-based ClassifyDevice would categorise it — which decides the render buffer it gets.

static string Classify(string friendlyName)
{
    var name = friendlyName.ToLowerInvariant();
    if (name.Contains("bluetooth") || name.Contains("hands-free") || name.Contains("bt "))
        return "Bluetooth";
    if (name.Contains("speaker") || name.Contains("soundbar") || name.Contains("hdmi") || name.Contains("optical"))
        return "Speakers";
    if (name.Contains("headphone") || name.Contains("headset") || name.Contains("jack") || name.Contains("line out"))
        return "Wired";
    return "Other";
}

using var enumerator = new MMDeviceEnumerator();
foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
{
    try
    {
        var c = d.AudioClient;
        var kind = Classify(d.FriendlyName);
        var buffer = kind == "Bluetooth" ? 100 : 10;
        Console.WriteLine($"{d.FriendlyName}");
        Console.WriteLine($"   PairUp classifies as : {kind}  -> render buffer {buffer} ms");
        Console.WriteLine($"   default period       : {c.DefaultDevicePeriod / 10000.0:0.00} ms");
        Console.WriteLine($"   minimum period       : {c.MinimumDevicePeriod / 10000.0:0.00} ms\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{d.FriendlyName}\n   query failed: {ex.Message}\n");
    }
}
