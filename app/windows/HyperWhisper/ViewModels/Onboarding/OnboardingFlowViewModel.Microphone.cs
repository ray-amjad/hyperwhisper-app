using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels.Onboarding;

public sealed partial class OnboardingFlowViewModel : ViewModelBase
{
    // =========================================================================
    // MICROPHONE STEP
    // =========================================================================

    private IReadOnlyList<OnboardingInputDevice> _deviceOptions = Array.Empty<OnboardingInputDevice>();

    /// <summary>System Default first, then whatever is connected.</summary>
    public IReadOnlyList<OnboardingInputDevice> DeviceOptions
    {
        get => _deviceOptions;
        private set => SetProperty(ref _deviceOptions, value);
    }

    private string _selectedDeviceId = string.Empty;

    /// <summary>"" means "follow the system default".</summary>
    public string SelectedDeviceId
    {
        get => _selectedDeviceId;
        private set
        {
            if (!SetProperty(ref _selectedDeviceId, value))
                return;

            OnPropertyChanged(nameof(SelectedDeviceName));
            OnPropertyChanged(nameof(MicrophoneSummary));
        }
    }

    private OnboardingDeviceAvailability _deviceAvailability = OnboardingDeviceAvailability.Available;

    /// <summary>
    /// Why the device list is what it is. Four distinct renderings on the step, and
    /// none of them gates Continue.
    /// </summary>
    public OnboardingDeviceAvailability DeviceAvailability
    {
        get => _deviceAvailability;
        private set
        {
            if (!SetProperty(ref _deviceAvailability, value))
                return;

            OnPropertyChanged(nameof(HasUsableMicrophone));
            OnPropertyChanged(nameof(MicrophoneSummary));
        }
    }

    public bool HasUsableMicrophone => DeviceAvailability == OnboardingDeviceAvailability.Available;

    private bool _isLevelMeterActive;

    /// <summary>
    /// False unless a preview is genuinely running, so the meter can render an
    /// explicitly inactive state rather than a dead flat bar that reads as a bug.
    /// </summary>
    public bool IsLevelMeterActive
    {
        get => _isLevelMeterActive;
        private set => SetProperty(ref _isLevelMeterActive, value);
    }

    private float _inputLevel;

    public float InputLevel
    {
        get => _inputLevel;
        private set => SetProperty(ref _inputLevel, value);
    }

    public string SelectedDeviceName
    {
        get
        {
            foreach (var device in DeviceOptions)
            {
                if (device.Id == SelectedDeviceId)
                    return device.Name;
            }

            return _systemDefaultDeviceName;
        }
    }

    /// <summary>
    /// The Done step's microphone row. It must say "none connected" rather than
    /// showing a tick when there is nothing to record with.
    /// </summary>
    public string MicrophoneSummary =>
        DeviceAvailability == OnboardingDeviceAvailability.Available
            ? SelectedDeviceName
            : Loc.S("onboarding.done.mic.noneConnected");

    /// <summary>
    /// True between <see cref="BeginMicrophoneStep"/> and
    /// <see cref="EndMicrophoneStep"/>. The meter's arming rule reads THIS and not
    /// <see cref="Step"/>: the two step hooks are public and the suite drives them
    /// directly, and "the microphone step is open" is the fact the rule is really
    /// about, whichever way the step was entered.
    /// </summary>
    private bool _microphoneStepOpen;

    public void BeginMicrophoneStep()
    {
        if (!_isLive)
            return;

        _microphoneStepOpen = true;
        _audio.RefreshDevices();
        _audio.RefreshMicrophoneAuthorization();
        DeviceAvailability = _audio.Availability;
        RefreshDeviceOptions();
        SyncLevelMeter();
    }

    public void EndMicrophoneStep()
    {
        _microphoneStepOpen = false;
        _audio.StopInputLevelPreview();
        IsLevelMeterActive = false;
    }

    /// <summary>
    /// The ONE rule for when the level meter runs, applied from all three places
    /// that can change its inputs: entering the step, picking a device, and a
    /// device-availability change from the OS.
    ///
    /// It runs when the microphone step is open, a device is available, and the
    /// capture stream actually opened - from the OPEN, not from availability. A
    /// device that enumerates can still refuse to open (another app holds it
    /// exclusively, consent flips between the read and the open, the driver
    /// faults), and lighting the meter on availability alone left 33 bars frozen
    /// under a live "speak to see the level" hint.
    ///
    /// Written as a sync rather than as two one-way hooks because the one-way
    /// version only ever turned the meter OFF: plugging a microphone in while the
    /// step was open flipped the title to "Say something. Watch the bars." over a
    /// meter that stayed dead, and only clicking a device row or leaving and
    /// re-entering the step repaired it. StartInputLevelPreview is idempotent, so
    /// calling this when nothing moved costs nothing.
    /// </summary>
    private void SyncLevelMeter()
    {
        var shouldRun = _isLive
            && _microphoneStepOpen
            && DeviceAvailability == OnboardingDeviceAvailability.Available;

        if (!shouldRun)
        {
            // Only when something is actually running: an unconditional stop would
            // turn every device change on every other step into a gateway call.
            if (IsLevelMeterActive)
            {
                _audio.StopInputLevelPreview();
                IsLevelMeterActive = false;
            }

            return;
        }

        IsLevelMeterActive = _audio.StartInputLevelPreview();
    }

    public void RefreshDeviceOptions()
    {
        if (!_isLive)
            return;

        ApplyDeviceList(_audio.Devices);
    }

    private void ApplyDeviceList(IReadOnlyList<OnboardingInputDevice> devices)
    {
        // "System Default" is always the first option, and an empty id is how the
        // rest of the app already encodes it.
        var options = new List<OnboardingInputDevice>(devices.Count + 1)
        {
            OnboardingInputDevice.SystemDefault(_systemDefaultDeviceName)
        };
        options.AddRange(devices);

        DeviceOptions = options;
        SelectedDeviceId = _audio.SelectedDeviceId ?? string.Empty;
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(MicrophoneSummary));
    }

    [RelayCommand]
    public void SelectDevice(string id)
    {
        if (!_isLive)
            return;

        id ??= string.Empty;

        // Nothing to select, and nothing may be written: with no usable device the
        // step is informational, so "Set Up Later" must have nothing to undo.
        if (DeviceAvailability != OnboardingDeviceAvailability.Available)
            return;

        // A device can vanish between the list being drawn and the pick landing.
        // Rejecting it here keeps a disconnected microphone out of the selection and,
        // more importantly, stops it flipping the pending-write flag for a change
        // that was never applied.
        if (id.Length > 0 && !DeviceOptions.Any(d => d.Id == id))
            return;

        // The device change reaches SettingsService immediately, because the level
        // meter and the Try It recording both have to follow it. Capture what it
        // replaces so Set Up Later restores it (bug 1).
        if (!_didCaptureDevice)
        {
            // Snapshot BOTH writes. The persisted preference and the open device
            // diverge when the remembered microphone is unplugged, so restoring
            // either one alone leaves the other pointing at the onboarding pick.
            _previousDeviceId = _audio.StoredDeviceId;
            _previousOpenDeviceId = _audio.SelectedDeviceId;
            _didCaptureDevice = true;
            RaiseGateChanged();
        }

        SelectedDeviceId = id;
        _audio.SelectDevice(id.Length == 0 ? null : id);

        // Re-point the metering session at the newly selected device, through the
        // one arming rule.
        SyncLevelMeter();
    }

}
