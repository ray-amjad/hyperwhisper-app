using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.PortableApplication.ViewModels;

public enum MicrophoneChoiceKind
{
    /// <summary>Follow the OS default input. Saves nothing.</summary>
    SystemDefault,
    /// <summary>A connected input device.</summary>
    Device,
    /// <summary>The saved device, which is not connected right now. Picking it changes nothing.</summary>
    Disconnected,
}

/// <summary>One row of the microphone picker. A record, so a refill compares cheaply and a
/// ComboBox finds the selected row by value.</summary>
public sealed record MicrophoneChoice(MicrophoneChoiceKind Kind, string Label, string? DeviceName, string? DeviceId)
{
    public override string ToString() => Label;
}

/// <summary>
/// The ONE microphone choice of the head, shared by the Home "Audio input" box, the Settings → Sound
/// picker, onboarding and the tray (spec: mic picker in Settings, 2026-10-19).
/// </summary>
/// <remarks>
/// The choice is saved by device NAME under <see cref="SettingKey"/>, and "Default" saves nothing:
/// the key is removed and the OS default input is followed. The setting is machine-specific, so
/// the backup export (an allow-list) never carries it.
///
/// <see cref="TranscriptionWorkflowViewModel.SelectedAudioDevice"/> stays the device the next
/// recording opens; this view model only steers it. After every snapshot it resolves the saved name
/// against the current device list — the saved device when connected, else the OS default — and
/// moves the selection there. So an unplugged saved device falls back to the default without
/// losing the choice, and its return is picked up by the next device refresh with no restart.
///
/// Two devices can share a name (two identical USB microphones). The saved value stays the name,
/// but within a session the exact device picked (its id) is remembered, so that device stays
/// selected and is what records. A name matching several devices resolves to the picked one, else
/// to the one currently selected, else to the first.
///
/// Warning: <see cref="Choices"/> is reconciled in place, never cleared and refilled, for the same
/// reason <c>TranscriptionWorkflowViewModel.SyncAudioDevices</c> is: a bound ComboBox drops its
/// selection when its list empties, and a TwoWay binding writes null when its view is torn down.
/// A null written to <see cref="SelectedChoice"/> is therefore never a choice and is ignored.
/// </remarks>
public sealed class MicrophonePickerViewModel : ViewModelBase, IDisposable
{
    public const string SettingKey = "audio.inputDeviceName";

    private readonly TranscriptionWorkflowViewModel _recording;
    private readonly PortableSettingsService _settings;
    private readonly Func<string, string>? _localize;
    private MicrophoneChoice? _selectedChoice;
    private string? _fallbackNotice;
    private bool _reconciling;
    private bool _reconcileAgain;
    private bool _disposed;
    private string? _resolvedDeviceId;
    /// <summary>The id of the device the user picked this session, to tell same-named devices
    /// apart. Not saved: ids are not stable across replugs and reboots, names are.</summary>
    private string? _pickedDeviceId;

    public MicrophonePickerViewModel(
        TranscriptionWorkflowViewModel recording,
        PortableSettingsService settings,
        Func<IStreamingAudioCapture>? testCapture = null,
        TimeProvider? time = null,
        Func<string, string>? localize = null,
        Func<bool>? isRecordingActive = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localize = localize;
        LevelTest = new MicrophoneLevelTestViewModel(
            testCapture, () => _recording.SelectedAudioDevice?.Id, time, localize,
            () => isRecordingActive?.Invoke() == true || IsRecordingState(_recording.State));
        _recording.DevicesChanged += OnRecordingDevicesChanged;
        _recording.PropertyChanged += OnRecordingPropertyChanged;
        Reconcile();
    }

    public ObservableCollection<MicrophoneChoice> Choices { get; } = new();

    /// <summary>The Microphone level test under the picker.</summary>
    public MicrophoneLevelTestViewModel LevelTest { get; }

    /// <summary>The saved device name, or null when following the OS default.</summary>
    public string? SavedDeviceName
    {
        get
        {
            try
            {
                var name = _settings.Get<string>(SettingKey);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>The picked row. A user pick saves it; null is ignored (see the class remarks).</summary>
    public MicrophoneChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (value is null || _reconciling || Equals(value, _selectedChoice)) return;
            // A ComboBox catching up on a refill can write back a row the list no longer holds
            // (the "Default" row before the OS default was known, say). Only a listed row is a pick.
            if (!Choices.Contains(value)) return;
            switch (value.Kind)
            {
                case MicrophoneChoiceKind.SystemDefault: _pickedDeviceId = null; Save(null); break;
                case MicrophoneChoiceKind.Device: _pickedDeviceId = value.DeviceId; Save(value.DeviceName); break;
                // Picking the "(not connected)" row is keeping the choice already made.
                case MicrophoneChoiceKind.Disconnected: break;
            }
            // This setter runs inside the ComboBox's own selection commit, and the reconcile can
            // remove a row (the "(not connected)" one, when a connected device is picked). Changing
            // the list under that commit throws ArgumentOutOfRange in Avalonia's SelectedItems, so
            // on a UI context the reconcile runs just after the commit. The choice is saved already.
            if (SynchronizationContext.Current is { } context) context.Post(_ => Reconcile(), null);
            else Reconcile();
        }
    }

    /// <summary>True while a saved device is not connected and the OS default stands in.</summary>
    public bool IsSavedDeviceMissing => _fallbackNotice is not null;

    /// <summary>"Using &lt;fallback&gt; until &lt;device&gt; is reconnected.", or null.</summary>
    public string? FallbackNotice
    {
        get => _fallbackNotice;
        private set { if (Set(ref _fallbackNotice, value)) Notify(nameof(IsSavedDeviceMissing)); }
    }

    /// <summary>Raised on the UI context when the device the next recording opens changes.</summary>
    public event EventHandler? ResolvedDeviceChanged;

    /// <summary>The onboarding and tray path: a null device follows the OS default.</summary>
    public void ChooseDevice(AudioInputDevice? device)
    {
        _pickedDeviceId = device?.Id;
        Save(device?.Name);
        Reconcile();
    }

    public void ChooseSystemDefault() => ChooseDevice(null);

    /// <summary>Re-reads the saved choice (after a settings reload) and re-resolves.</summary>
    public void Reload() => Reconcile();

    /// <summary>The device a saved <paramref name="savedName"/> resolves to on <paramref name="devices"/>:
    /// a device of that name, else the OS default, else the first device. When several devices
    /// carry the name, the first of <paramref name="preferredIds"/> among them wins, else the first.</summary>
    public static AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string? savedName, params string?[] preferredIds)
    {
        ArgumentNullException.ThrowIfNull(devices);
        AudioInputDevice? named = null;
        if (savedName is not null)
        {
            var matches = devices.Where(device => string.Equals(device.Name, savedName, StringComparison.Ordinal)).ToArray();
            named = preferredIds
                .Where(id => id is not null)
                .Select(id => matches.FirstOrDefault(device => string.Equals(device.Id, id, StringComparison.Ordinal)))
                .FirstOrDefault(device => device is not null)
                ?? matches.FirstOrDefault();
        }
        return named
            ?? devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault();
    }

    private void Save(string? name)
    {
        if (string.Equals(SavedDeviceName, name, StringComparison.Ordinal)) return;
        if (name is null) _settings.Remove(SettingKey);
        else _settings.Set(SettingKey, name);
        _ = _settings.Save();
    }

    private void OnRecordingDevicesChanged(object? sender, EventArgs e) => Reconcile();

    private void OnRecordingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Spec: "a recording starting stops it".
        if (e.PropertyName == nameof(TranscriptionWorkflowViewModel.State) && IsRecordingState(_recording.State))
            LevelTest.Stop();
    }

    private static bool IsRecordingState(string state) => state is "Recording" or "Stopping";

    private void Reconcile()
    {
        if (_disposed) return;
        if (_reconciling) { _reconcileAgain = true; return; }
        _reconciling = true;
        try
        {
            do
            {
                _reconcileAgain = false;
                ReconcileOnce();
            }
            while (_reconcileAgain && !_disposed);
        }
        finally { _reconciling = false; }
    }

    private void ReconcileOnce()
    {
        var devices = _recording.AudioDevices.ToArray();
        var saved = SavedDeviceName;
        var resolved = Resolve(devices, saved, _pickedDeviceId, _recording.SelectedAudioDevice?.Id);
        var savedConnected = saved is not null && devices.Any(device => string.Equals(device.Name, saved, StringComparison.Ordinal));

        // Steer the recording selection. The setter discards a null while the list is not empty,
        // which is the only time Resolve returns null anyway.
        if (resolved is not null && !Equals(_recording.SelectedAudioDevice, resolved))
            _recording.SelectedAudioDevice = resolved;

        var osDefault = devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
        var incoming = new List<MicrophoneChoice>(devices.Length + 2)
        {
            new(MicrophoneChoiceKind.SystemDefault,
                osDefault is null
                    ? Text("linux.settings.sound.microphone.defaultNone", "Default")
                    : Format("linux.settings.sound.microphone.default", "Default · {0}", osDefault.Name),
                null, null),
        };
        foreach (var device in devices)
            incoming.Add(new(MicrophoneChoiceKind.Device, device.Name, device.Name, device.Id));
        MicrophoneChoice? missing = null;
        if (saved is not null && !savedConnected)
        {
            missing = new(MicrophoneChoiceKind.Disconnected,
                Format("linux.settings.sound.microphone.notConnected", "{0} (not connected)", saved), saved, null);
            incoming.Add(missing);
        }

        var selected = saved is null
            ? incoming[0]
            : missing ?? incoming.FirstOrDefault(choice => choice.Kind == MicrophoneChoiceKind.Device
                && string.Equals(choice.DeviceId, resolved?.Id, StringComparison.Ordinal));
        var listChanged = SyncChoices(incoming);
        if (listChanged && Equals(selected, _selectedChoice))
        {
            // As in TranscriptionWorkflowViewModel.ApplySnapshot: the ComboBox dropped its
            // selection when the list moved, and re-notifying an unchanged value would not
            // restore it. Publish null first so the next notification carries a value.
            _selectedChoice = null;
            Notify(nameof(SelectedChoice));
        }
        if (!Equals(selected, _selectedChoice) || listChanged)
        {
            _selectedChoice = selected;
            Notify(nameof(SelectedChoice));
        }

        FallbackNotice = missing is null ? null : FormatTwo(
            "linux.settings.sound.microphone.fallback", "Using {0} until {1} is reconnected.",
            resolved?.Name ?? Text("linux.settings.sound.microphone.defaultNone", "Default"), saved!);

        var resolvedId = resolved?.Id;
        if (!string.Equals(resolvedId, _resolvedDeviceId, StringComparison.Ordinal))
        {
            var wasTesting = LevelTest.IsTesting;
            _resolvedDeviceId = resolvedId;
            // The test always listens to the chosen device: move a running test with the choice.
            if (wasTesting) LevelTest.Start();
            ResolvedDeviceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool SyncChoices(IReadOnlyList<MicrophoneChoice> incoming)
    {
        var changed = false;
        for (var index = 0; index < incoming.Count; index++)
        {
            if (index >= Choices.Count) { Choices.Add(incoming[index]); changed = true; }
            else if (!Equals(Choices[index], incoming[index])) { Choices[index] = incoming[index]; changed = true; }
        }
        while (Choices.Count > incoming.Count)
        {
            Choices.RemoveAt(Choices.Count - 1);
            changed = true;
        }
        return changed;
    }

    private string Text(string key, string fallback)
    {
        var value = _localize?.Invoke(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    private string Format(string key, string fallback, string argument)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Text(key, fallback), argument); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, fallback, argument); }
    }

    private string FormatTwo(string key, string fallback, string first, string second)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Text(key, fallback), first, second); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, fallback, first, second); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recording.DevicesChanged -= OnRecordingDevicesChanged;
        _recording.PropertyChanged -= OnRecordingPropertyChanged;
        LevelTest.Dispose();
    }
}
