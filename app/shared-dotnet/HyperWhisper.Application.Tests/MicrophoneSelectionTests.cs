using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #2408: virtual inputs (BlackHole, VB-CABLE, a PulseAudio null-sink or remap source named
// after one…) are left out of the automatic microphone pick, but a microphone the user chose by
// hand is never overridden. The pure half: MicrophoneSelection and MicrophonePickerViewModel.Resolve.
static class MicrophoneSelectionTests
{
    private static readonly AudioInputDevice BuiltIn = new("alsa_input.pci-0000_00_1f.3.analog-stereo", "Built-in Audio Analog Stereo");
    private static readonly AudioInputDevice Webcam = new("alsa_input.usb-046d_C920-02.analog-stereo", "HD Pro Webcam C920");
    private static readonly AudioInputDevice Scarlett = new("alsa_input.usb-Focusrite_Scarlett_2i2_USB-00.analog-stereo", "Scarlett 2i2 USB");
    // A PulseAudio null-sink / remap source: pactl reports the user's description as the name.
    private static readonly AudioInputDevice Cable = new("vb_cable_source", "VB-Cable Virtual Source");
    private static readonly AudioInputDevice BlackHole = new("remap_blackhole", "BlackHole 2ch");

    public static void Run()
    {
        DetectionMatchesTheListByName();
        VirtualDefaultWithAPhysicalInputPicksThePhysical();
        OnlyVirtualInputsKeepTheOsDefault();
        PhysicalDefaultIsUnchanged();
        AHandPickedVirtualDeviceIsNeverOverridden();
        AnUnpluggedChoiceWithAVirtualDefaultStandsInWithAPhysical();
        Console.WriteLine("Microphone selection tests passed.");
    }

    private static void DetectionMatchesTheListByName()
    {
        string[] virtualNames =
        [
            "BlackHole 2ch", "Soundflower (2ch)", "Loopback Audio", "VB-Audio Virtual Cable", "vb-cable",
            "CABLE Output (VB-Audio Virtual Cable)", "CABLE-A Output (VB-Audio Cable A)", "CABLE-B Output (VB-Audio Cable B)",
            "VoiceMeeter Output", "Line 1 (Virtual Audio Cable)", "ZoomAudioDevice", "Microsoft Teams Audio",
            "Background Music", "eqMac", "Stereo Mix (Realtek Audio)", "What U Hear (Sound Blaster)", "Wave Out Mix",
            // Windows WaveIn cuts names at 31 characters; the list still matches.
            "CABLE Output (VB-Audio Virtual ",
        ];
        foreach (var name in virtualNames)
            Check(MicrophoneSelection.IsVirtual(name), $"'{name}' was not detected as virtual");
        string[] physicalNames =
        [
            "Krisp Microphone", "Microphone (NVIDIA Broadcast)", "Wave Link MicrophoneFX", "Elgato Wave:3",
            "Aggregate Device", "Built-in Audio Analog Stereo", "Scarlett 2i2 USB", "",
        ];
        foreach (var name in physicalNames)
            Check(!MicrophoneSelection.IsVirtual(name), $"'{name}' was detected as virtual");
        Check(!MicrophoneSelection.IsVirtual((string?)null), "a null name was detected as virtual");
        Check(MicrophoneSelection.VirtualNameMarkers.Count == 17, "the virtual list drifted from the sibling heads'");
    }

    private static void VirtualDefaultWithAPhysicalInputPicksThePhysical()
    {
        AudioInputDevice[] devices = [Cable with { IsDefault = true }, BuiltIn, Webcam];
        var picked = MicrophoneSelection.AutomaticPick(devices, out var skipped);
        Check(picked == BuiltIn && skipped, $"a virtual default with a physical input picked '{picked?.Name}'");
        Check(MicrophonePickerViewModel.Resolve(devices, null) == BuiltIn, "Default resolved to the virtual OS default");
        // Enumeration order, not the default's position, decides which physical input.
        AudioInputDevice[] reordered = [Webcam, BlackHole, Cable with { IsDefault = true }, BuiltIn];
        Check(MicrophoneSelection.AutomaticPick(reordered) == Webcam, "the first non-virtual input in enumeration order was not picked");
    }

    private static void OnlyVirtualInputsKeepTheOsDefault()
    {
        AudioInputDevice[] devices = [BlackHole, Cable with { IsDefault = true }];
        var picked = MicrophoneSelection.AutomaticPick(devices, out var skipped);
        Check(picked?.Id == Cable.Id && !skipped, $"an all-virtual machine picked '{picked?.Name}'");
        Check(MicrophonePickerViewModel.Resolve(devices, null)?.Id == Cable.Id, "an all-virtual machine did not keep the OS default");
        Check(MicrophoneSelection.AutomaticPick([]) is null, "an empty list produced a pick");
    }

    private static void PhysicalDefaultIsUnchanged()
    {
        AudioInputDevice[] devices = [Cable, BuiltIn, Scarlett with { IsDefault = true }];
        var picked = MicrophoneSelection.AutomaticPick(devices, out var skipped);
        Check(picked?.Id == Scarlett.Id && !skipped, $"a physical default was replaced by '{picked?.Name}'");
        // No device flagged default: the first device, as before.
        Check(MicrophoneSelection.AutomaticPick([BuiltIn, Webcam]) == BuiltIn, "no flagged default did not pick the first device");
    }

    private static void AHandPickedVirtualDeviceIsNeverOverridden()
    {
        AudioInputDevice[] devices = [BuiltIn with { IsDefault = true }, Cable];
        Check(MicrophonePickerViewModel.Resolve(devices, Cable.Name) == Cable, "a hand-picked virtual device was overridden");
        AudioInputDevice[] virtualDefault = [Cable with { IsDefault = true }, BuiltIn];
        Check(MicrophonePickerViewModel.Resolve(virtualDefault, Cable.Name)?.Id == Cable.Id,
            "a hand-picked virtual device that is also the OS default was overridden");
    }

    private static void AnUnpluggedChoiceWithAVirtualDefaultStandsInWithAPhysical()
    {
        AudioInputDevice[] devices = [Cable with { IsDefault = true }, BuiltIn];
        Check(MicrophonePickerViewModel.Resolve(devices, Scarlett.Name) == BuiltIn,
            "an unplugged choice with a virtual default did not stand in with the physical input");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Microphone selection: {message}");
    }
}
