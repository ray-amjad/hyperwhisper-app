// MICROPHONE SELECTION
//
// The pure half of "which microphone does HyperWhisper record from". No NAudio,
// no COM, no WPF: MainViewModel feeds it the WaveIn device list, the saved
// choice and the OS default endpoint, and gets back the device to open. The
// Settings picker builds its rows from the same answer, so the tray, onboarding
// and Settings can never disagree about what is selected.
//
// THE ONE SAVED CHOICE is SettingsService.LastSelectedMicrophone, a WaveIn
// product NAME (device numbers shift on every plug/unplug). Null means "follow
// the Windows default input". A saved name that is not connected is KEPT, and
// recording falls back to the Windows default until it comes back.
//
// NAMES. WaveIn (WinMM) truncates a product name to 31 characters (MAXPNAMELEN
// is 32 including the terminator), while Core Audio's FriendlyName is full
// length. So the OS default endpoint's FriendlyName only prefix-matches its
// WaveIn row once the name is long, and a saved name may have been written by
// an older build from either source. FindByName handles both.
//
// SAME-NAMED DEVICES. Two identical USB microphones have the same WaveIn name,
// so a saved name can match several rows. Within a session the exact device
// the user picked (its DeviceNumber) is what records and what is shown
// selected: MainViewModel passes it, then the current device, as the preferred
// device numbers, and the first match is used only when neither is among them.
// What is SAVED stays the name.

namespace HyperWhisper.Services;

/// <summary>Where the device HyperWhisper records from came from.</summary>
public enum MicrophoneSource
{
    /// <summary>No input device at all.</summary>
    None,

    /// <summary>The user's saved choice, and it is connected.</summary>
    Saved,

    /// <summary>Nothing is saved: following the Windows default input.</summary>
    SystemDefault,

    /// <summary>The saved choice is not connected; the Windows default stands in.</summary>
    SavedMissingUsingDefault,

    /// <summary>
    /// The saved choice is not connected and no other input is either, so
    /// nothing records until a microphone comes back. The choice is still kept
    /// and shown "(not connected)".
    /// </summary>
    SavedMissingNoDevice,
}

/// <summary>The resolved microphone, and why.</summary>
public sealed record MicrophoneResolution(
    AudioDeviceService.AudioDevice? Device,
    MicrophoneSource Source,
    string? SavedName,
    AudioDeviceService.AudioDevice? DefaultDevice)
{
    public bool IsSavedMissing =>
        Source is MicrophoneSource.SavedMissingUsingDefault or MicrophoneSource.SavedMissingNoDevice;
}

/// <summary>
/// One row of the Settings microphone picker. <see cref="Name"/> is what is
/// saved when the row is picked: null for the Default row.
/// <see cref="DeviceNumber"/> is the exact WaveIn device of a connected row, so
/// two same-named devices are two different rows; null for the Default and
/// "(not connected)" rows.
/// </summary>
public sealed record MicrophoneOption(string? Name, string Label, bool IsDefault, bool IsMissing, int? DeviceNumber = null);

public static class MicrophoneSelection
{
    /// <summary>WinMM's MAXPNAMELEN minus the terminator.</summary>
    public const int WaveInNameLimit = 31;

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> name the same
    /// device: equal ignoring case, or one is the other cut at the WaveIn
    /// 31-character limit.
    /// </summary>
    public static bool NamesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        a = a.Trim();
        b = b.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= WaveInNameLimit
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The device named <paramref name="name"/>, or null. An exact (ordinal)
    /// match wins: when several devices carry that exact name, the first of
    /// <paramref name="preferredDeviceNumbers"/> among them, else the first.
    /// Then a case-insensitive or truncation match: a preferred one, else it
    /// must be unique, so two long names sharing their first 31 characters
    /// never pick one at random.
    /// </summary>
    public static AudioDeviceService.AudioDevice? FindByName(
        IReadOnlyList<AudioDeviceService.AudioDevice> devices,
        string? name,
        params int?[] preferredDeviceNumbers)
    {
        if (string.IsNullOrWhiteSpace(name) || devices.Count == 0)
            return null;

        var exact = devices.Where(d => string.Equals(d.Name, name, StringComparison.Ordinal)).ToList();
        if (exact.Count > 0)
            return Preferred(exact, preferredDeviceNumbers) ?? exact[0];

        var loose = devices.Where(d => NamesMatch(d.Name, name)).ToList();
        if (loose.Count == 0)
            return null;

        return Preferred(loose, preferredDeviceNumbers)
            ?? (loose.Count == 1 ? loose[0] : null); // ambiguous
    }

    /// <summary>The first of <paramref name="preferredDeviceNumbers"/> found in <paramref name="matches"/>.</summary>
    private static AudioDeviceService.AudioDevice? Preferred(
        IReadOnlyList<AudioDeviceService.AudioDevice> matches,
        int?[]? preferredDeviceNumbers)
    {
        if (preferredDeviceNumbers == null)
            return null;

        foreach (var number in preferredDeviceNumbers)
        {
            if (number is not { } n)
                continue;
            var match = matches.FirstOrDefault(d => d.DeviceNumber == n);
            if (match != null)
                return match;
        }

        return null;
    }

    /// <summary>
    /// The WaveIn row for the Windows default capture endpoint.
    ///
    /// By name first (<paramref name="defaultFriendlyName"/>, truncation-aware);
    /// then by its position in the active capture endpoint list, which WinMM
    /// enumerates in the same order (the assumption
    /// AudioRecorderService.ResolveCaptureDevice already relies on); and only
    /// then the first device, which is what every launch used to take.
    /// </summary>
    public static AudioDeviceService.AudioDevice? ResolveDefault(
        IReadOnlyList<AudioDeviceService.AudioDevice> devices,
        string? defaultFriendlyName,
        int defaultEndpointOrdinal)
    {
        if (devices.Count == 0)
            return null;

        var byName = FindByName(devices, defaultFriendlyName, defaultEndpointOrdinal >= 0 ? defaultEndpointOrdinal : null);
        if (byName != null)
            return byName;

        if (defaultEndpointOrdinal >= 0)
        {
            var byOrdinal = devices.FirstOrDefault(d => d.DeviceNumber == defaultEndpointOrdinal);
            if (byOrdinal != null)
                return byOrdinal;
        }

        return devices[0];
    }

    /// <summary>
    /// Saved device if connected, else the Windows default. The saved name is
    /// passed through untouched so a disconnected choice is never forgotten.
    /// <paramref name="preferredDeviceNumbers"/> picks among devices that share
    /// the saved name (see <see cref="FindByName"/>).
    /// </summary>
    public static MicrophoneResolution Resolve(
        IReadOnlyList<AudioDeviceService.AudioDevice> devices,
        string? savedName,
        AudioDeviceService.AudioDevice? defaultDevice,
        params int?[] preferredDeviceNumbers)
    {
        var saved = string.IsNullOrWhiteSpace(savedName) ? null : savedName;

        // No input at all. A saved choice is still a choice: it is reported
        // missing (so Settings shows "<name> (not connected)" and says why),
        // not dropped to None, which reads as "nothing was ever chosen".
        if (devices.Count == 0)
            return new MicrophoneResolution(
                null,
                saved == null ? MicrophoneSource.None : MicrophoneSource.SavedMissingNoDevice,
                saved,
                null);

        if (saved != null)
        {
            var match = FindByName(devices, saved, preferredDeviceNumbers);
            if (match != null)
                return new MicrophoneResolution(match, MicrophoneSource.Saved, saved, defaultDevice);

            return new MicrophoneResolution(defaultDevice, MicrophoneSource.SavedMissingUsingDefault, saved, defaultDevice);
        }

        return new MicrophoneResolution(defaultDevice, MicrophoneSource.SystemDefault, null, defaultDevice);
    }

    /// <summary>
    /// The picker rows: "Default · &lt;current default&gt;", each device, and, when
    /// the saved device is unplugged, a "&lt;name&gt; (not connected)" row so the
    /// choice stays visible. Also returns the row that is selected.
    /// </summary>
    public static (IReadOnlyList<MicrophoneOption> Options, MicrophoneOption Selected) BuildOptions(
        IReadOnlyList<AudioDeviceService.AudioDevice> devices,
        MicrophoneResolution resolution)
    {
        var defaultRow = new MicrophoneOption(null, DefaultLabel(resolution.DefaultDevice?.Name), IsDefault: true, IsMissing: false);
        var options = new List<MicrophoneOption>(devices.Count + 2) { defaultRow };
        var selected = defaultRow;

        foreach (var device in devices)
        {
            var row = new MicrophoneOption(device.Name, device.Name, IsDefault: false, IsMissing: false, device.DeviceNumber);
            options.Add(row);
            if (resolution.Source == MicrophoneSource.Saved && resolution.Device == device)
                selected = row;
        }

        if (resolution.IsSavedMissing && resolution.SavedName != null)
        {
            var missing = new MicrophoneOption(resolution.SavedName, NotConnectedLabel(resolution.SavedName), IsDefault: false, IsMissing: true);
            options.Add(missing);
            selected = missing;
        }

        return (options, selected);
    }

    /// <summary>"Default · Microphone (Realtek)", or "System Default" when Windows names none.</summary>
    public static string DefaultLabel(string? defaultDeviceName) =>
        string.IsNullOrWhiteSpace(defaultDeviceName)
            ? Localization.Loc.S("onboarding.mic.device.systemDefault")
            : Localization.Loc.S("settings.sound.microphone.default", defaultDeviceName);

    /// <summary>"Scarlett 2i2 USB (not connected)".</summary>
    public static string NotConnectedLabel(string savedName) =>
        Localization.Loc.S("settings.sound.microphone.notConnected", savedName);

    /// <summary>
    /// The amber line under a disconnected choice, or null when there is none:
    /// "Using &lt;fallback&gt; until &lt;device&gt; is reconnected.", or, when no
    /// microphone is connected at all, "No microphone is connected. HyperWhisper
    /// will use &lt;device&gt; when it is reconnected."
    /// </summary>
    public static string? FallbackMessage(MicrophoneResolution resolution)
    {
        if (!resolution.IsSavedMissing || resolution.SavedName == null)
            return null;

        if (resolution.Source == MicrophoneSource.SavedMissingNoDevice)
            return Localization.Loc.S("settings.sound.microphone.fallbackNoDevice", resolution.SavedName);

        var fallback = resolution.Device?.Name;
        if (string.IsNullOrWhiteSpace(fallback))
            fallback = Localization.Loc.S("onboarding.mic.device.systemDefault");

        return Localization.Loc.S("settings.sound.microphone.fallback", fallback, resolution.SavedName);
    }
}
