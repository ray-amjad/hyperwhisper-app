using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication;

/// <summary>
/// The pure microphone decision of the Linux head (#2408): which input the automatic pick records
/// from, and which inputs are virtual. No I/O, so every capture path and every picker can share it.
/// </summary>
/// <remarks>
/// The automatic pick is what records when the user has NOT chosen a microphone (the "Default"
/// row), and the stand-in while a chosen microphone is unplugged. It is the OS default input,
/// unless that is a virtual device (BlackHole, VB-CABLE, a PulseAudio null-sink or remap source
/// named after one…) and at least one non-virtual input is connected: then it is the first
/// non-virtual input, in enumeration order. A virtual device carries app audio or silence, not the
/// user's voice, so recording from it gives empty dictations. A machine with only virtual inputs
/// still records from the OS default. A device the user chose by hand is never overridden: callers
/// resolve the saved choice first and fall back to <see cref="AutomaticPick"/> only without one.
/// </remarks>
public static class MicrophoneSelection
{
    /// <summary>
    /// Device-name markers of virtual inputs, matched as case-insensitive substrings. Keep this
    /// list, and its order, identical to the sibling heads':
    /// <c>app/macos/hyperwhisper/Managers/AudioRecording/DeviceManagement/MicrophoneSelection.swift</c> and
    /// <c>app/windows/HyperWhisper/Services/MicrophoneSelection.cs</c>.
    /// </summary>
    /// <remarks>
    /// Not virtual on purpose, because they carry the user's own processed voice: Krisp, NVIDIA
    /// Broadcast, Elgato Wave Link, any aggregate device. On Linux the name is the PulseAudio source
    /// description, so a null-sink or remap source a user named "VB-Cable" matches too; sink
    /// monitors never reach this list (<c>PulseAudioInputDeviceService</c> drops them, #627).
    /// </remarks>
    public static IReadOnlyList<string> VirtualNameMarkers { get; } =
    [
        "BlackHole",
        "Soundflower",
        "Loopback Audio",
        "VB-Audio",
        "VB-Cable",
        "CABLE Output",
        "CABLE-A Output",
        "CABLE-B Output",
        "VoiceMeeter",
        "Virtual Audio Cable",
        "ZoomAudioDevice",
        "Microsoft Teams Audio",
        "Background Music",
        "eqMac",
        "Stereo Mix",
        "What U Hear",
        "Wave Out Mix",
    ];

    /// <summary>True when <paramref name="name"/> names a virtual input.</summary>
    public static bool IsVirtual(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && VirtualNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="device"/> is a virtual input, by its name.</summary>
    public static bool IsVirtual(AudioInputDevice? device) => device is not null && IsVirtual(device.Name);

    /// <summary>The OS default input: the device flagged default, else the first device.</summary>
    public static AudioInputDevice? OsDefault(IReadOnlyList<AudioInputDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
    }

    /// <summary>The device the automatic pick records from on <paramref name="devices"/>, or null
    /// when the list is empty. See the class remarks.</summary>
    public static AudioInputDevice? AutomaticPick(IReadOnlyList<AudioInputDevice> devices) =>
        AutomaticPick(devices, out _);

    /// <summary>As <see cref="AutomaticPick(IReadOnlyList{AudioInputDevice})"/>;
    /// <paramref name="skippedVirtualDefault"/> is true when the OS default was virtual and a
    /// non-virtual input was picked instead.</summary>
    public static AudioInputDevice? AutomaticPick(IReadOnlyList<AudioInputDevice> devices, out bool skippedVirtualDefault)
    {
        skippedVirtualDefault = false;
        var osDefault = OsDefault(devices);
        if (osDefault is null || !IsVirtual(osDefault)) return osDefault;
        var physical = devices.FirstOrDefault(device => !IsVirtual(device));
        if (physical is null) return osDefault;
        skippedVirtualDefault = true;
        return physical;
    }
}
