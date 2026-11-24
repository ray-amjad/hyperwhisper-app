using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication;

namespace HyperWhisper.Linux;

internal static class LinuxTrayMicrophoneSelector
{
    /// <summary>The automatic pick (#2408: a virtual OS default gives way to the first non-virtual
    /// input, in enumeration order, as in MicrophonePickerViewModel). With no device flagged default,
    /// the first by Id, so the result does not depend on enumeration order.</summary>
    internal static AudioInputDevice? SelectDefault(IEnumerable<AudioInputDevice> source)
    {
        var enumerated = source.ToArray();
        if (enumerated.Any(device => device.IsDefault)) return MicrophoneSelection.AutomaticPick(enumerated);
        return enumerated.OrderBy(device => device.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static AudioInputDevice? SelectAdjacent(
        IEnumerable<AudioInputDevice> source,
        string? selectedId,
        int direction)
    {
        var enumerated = source.ToArray();
        var devices = enumerated.OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
        if (devices.Length == 0) return null;
        var selected = Array.FindIndex(devices,
            device => string.Equals(device.Id, selectedId, StringComparison.Ordinal));
        // The automatic pick from ENUMERATION order (#2408), as Default records from it.
        if (selected < 0 && SelectDefault(enumerated) is { } automatic)
            selected = Array.FindIndex(devices, device => string.Equals(device.Id, automatic.Id, StringComparison.Ordinal));
        if (selected < 0) selected = 0;
        var step = direction < 0 ? -1 : 1;
        return devices[(selected + step + devices.Length) % devices.Length];
    }
}
