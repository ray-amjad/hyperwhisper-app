using System.Collections.ObjectModel;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.ViewModels;

namespace HyperWhisper.Linux;

internal enum LinuxOnboardingStep { Welcome, Capabilities, Provider, Microphone, Test }

internal sealed record LinuxOnboardingCapabilities(
    bool AudioCapture,
    bool Clipboard,
    bool UInput,
    bool GlobalShortcuts,
    bool DesktopPortal,
    bool LocalWhisper,
    bool LocalParakeet);

internal sealed class LinuxOnboardingViewModel : ViewModelBase
{
    private readonly Func<bool, bool> _persistDecision;
    private readonly Action<Mode?> _selectMode;
    private readonly Action<AudioInputDevice?> _selectDevice;
    private LinuxOnboardingStep _step;
    private bool _isVisible;
    private bool _testSucceeded;
    private string _testStatus;
    private Mode? _selectedMode;
    private AudioInputDevice? _selectedDevice;

    public LinuxOnboardingViewModel(
        LinuxOnboardingCapabilities capabilities,
        IEnumerable<Mode> modes,
        Mode? selectedMode,
        IEnumerable<AudioInputDevice> devices,
        AudioInputDevice? selectedDevice,
        Func<bool, bool> persistDecision,
        Action<Mode?> selectMode,
        Action<AudioInputDevice?> selectDevice,
        Func<string, string> text)
    {
        Capabilities = capabilities;
        Modes = new(modes);
        Devices = new(devices);
        _selectedMode = selectedMode ?? Modes.FirstOrDefault();
        _selectedDevice = selectedDevice ?? Devices.FirstOrDefault();
        _persistDecision = persistDecision;
        _selectMode = selectMode;
        _selectDevice = selectDevice;
        _testStatus = text("linux.onboarding.test.not_started");
    }

    public LinuxOnboardingCapabilities Capabilities { get; }
    public ObservableCollection<Mode> Modes { get; }
    public ObservableCollection<AudioInputDevice> Devices { get; }
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }
    public LinuxOnboardingStep Step { get => _step; private set { if (Set(ref _step, value)) NotifyStep(); } }
    public bool IsWelcome => Step == LinuxOnboardingStep.Welcome;
    public bool IsCapabilities => Step == LinuxOnboardingStep.Capabilities;
    public bool IsProvider => Step == LinuxOnboardingStep.Provider;
    public bool IsMicrophone => Step == LinuxOnboardingStep.Microphone;
    public bool IsTest => Step == LinuxOnboardingStep.Test;
    public bool CanGoBack => Step != LinuxOnboardingStep.Welcome;
    public bool CanGoNext => Step switch
    {
        LinuxOnboardingStep.Provider => SelectedMode is not null,
        LinuxOnboardingStep.Microphone => SelectedDevice is not null,
        LinuxOnboardingStep.Test => IsTestReady,
        _ => true,
    };
    public bool IsTestReady => Capabilities.AudioCapture && SelectedMode is not null && SelectedDevice is not null && IsSelectedModeAvailable;
    public bool IsSelectedModeAvailable => SelectedMode is { } mode &&
        (!string.Equals(mode.ProviderType, "local", StringComparison.OrdinalIgnoreCase)
         || (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
             ? Capabilities.LocalParakeet : Capabilities.LocalWhisper));
    public bool TestSucceeded { get => _testSucceeded; private set => Set(ref _testSucceeded, value); }
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }
    public Mode? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            _selectMode(value);
            NotifyReadiness();
        }
    }
    public AudioInputDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            _selectDevice(value);
            NotifyReadiness();
        }
    }

    public void Show() { Step = LinuxOnboardingStep.Welcome; IsVisible = true; }
    public void Back() { if (Step > LinuxOnboardingStep.Welcome) Step--; }
    public void Next()
    {
        if (!CanGoNext) return;
        if (Step < LinuxOnboardingStep.Test) { Step++; return; }
        Complete(skipped: false);
    }
    public void Skip() => Complete(skipped: true);
    public void SetTestStatus(string status, bool succeeded = false)
    {
        TestStatus = status;
        if (succeeded) TestSucceeded = true;
    }

    private void Complete(bool skipped)
    {
        if (_persistDecision(skipped)) IsVisible = false;
    }
    private void NotifyStep()
    {
        Notify(nameof(IsWelcome)); Notify(nameof(IsCapabilities)); Notify(nameof(IsProvider));
        Notify(nameof(IsMicrophone)); Notify(nameof(IsTest)); Notify(nameof(CanGoBack)); Notify(nameof(CanGoNext));
    }
    private void NotifyReadiness()
    {
        Notify(nameof(IsSelectedModeAvailable)); Notify(nameof(IsTestReady)); Notify(nameof(CanGoNext));
    }
}
