using System.Collections.ObjectModel;
using HyperWhisper.Data.Entities;
using HyperWhisper.ModelReadiness;
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
    private bool _selectedModeAvailable;
    private string _testStatus;
    private Mode? _selectedMode;
    private AudioInputDevice? _selectedDevice;

    public LinuxOnboardingViewModel(
        LinuxOnboardingCapabilities capabilities,
        IEnumerable<Mode> modes,
        Mode? selectedMode,
        IEnumerable<AudioInputDevice> devices,
        AudioInputDevice? selectedDevice,
        bool selectedModeAvailable,
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
        _selectedModeAvailable = selectedModeAvailable;
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
        LinuxOnboardingStep.Provider => SelectedMode is not null && IsSelectedModeAvailable,
        LinuxOnboardingStep.Microphone => SelectedDevice is not null,
        LinuxOnboardingStep.Test => IsTestReady && TestSucceeded,
        _ => true,
    };
    public bool IsTestReady => Capabilities.AudioCapture && SelectedMode is not null && SelectedDevice is not null && IsSelectedModeAvailable;
    public bool IsSelectedModeAvailable => SelectedMode is not null && _selectedModeAvailable;
    public bool TestSucceeded { get => _testSucceeded; private set => Set(ref _testSucceeded, value); }
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }
    public Mode? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            _selectedModeAvailable = false;
            TestSucceeded = false;
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
            TestSucceeded = false;
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
        TestSucceeded = succeeded;
        Notify(nameof(CanGoNext));
    }

    public void SetSelectedModeAvailable(bool available)
    {
        _selectedModeAvailable = available;
        if (!available) TestSucceeded = false;
        NotifyReadiness();
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

internal sealed class LinuxOnboardingModeReadiness(
    IProviderCredentialSource credentials,
    ILocalModelReadinessSource localModels,
    IReadOnlyList<ModelCapability>? capabilities = null)
{
    private readonly IProviderCredentialSource _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly ILocalModelReadinessSource _localModels = localModels ?? throw new ArgumentNullException(nameof(localModels));
    private readonly IReadOnlyList<ModelCapability> _capabilities = capabilities ?? UnifiedModelCatalog.LoadBundled();

    public async ValueTask<bool> IsReadyAsync(Mode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(mode.ProviderType, "local", StringComparison.OrdinalIgnoreCase))
        {
            var parakeet = string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase);
            var modelId = parakeet
                ? mode.LocalParakeetModel ?? mode.Model ?? "parakeet-v3"
                : mode.ModelType ?? mode.Model ?? "base";
            var provider = parakeet ? "parakeet" : "localWhisper";
            var capability = _capabilities.FirstOrDefault(item =>
                item.Deployment == ModelDeployment.Local
                && item.Workload == ModelWorkload.Voice
                && item.Surface == ModelSurface.BatchTranscription
                && string.Equals(item.ProviderId, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ModelId, modelId, StringComparison.Ordinal));
            return capability is not null
                && await _localModels.IsInstalledAsync(capability, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(mode.CloudProvider)) return false;
        var providerId = NormalizeCloudProvider(mode.CloudProvider);
        var knownProvider = string.Equals(providerId, "hyperwhisper", StringComparison.Ordinal)
            || _capabilities.Any(item => item.Deployment == ModelDeployment.Cloud
                && item.Workload == ModelWorkload.Voice
                && item.Surface == ModelSurface.BatchTranscription
                && string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(mode.CloudTranscriptionModel)
                    || string.Equals(item.ModelId, mode.CloudTranscriptionModel, StringComparison.Ordinal)));
        if (!knownProvider) return false;
        var credential = await _credentials.GetCredentialAsync(
            UnifiedModelCatalog.CredentialAccountFor(providerId), cancellationToken).ConfigureAwait(false);
        return credential?.IsPresent == true;
    }

    private static string NormalizeCloudProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "microsoftazurespeech" => "azure-mai",
        "googlespeech" => "google-chirp",
        "xai" => "grok",
        _ => provider.Trim().ToLowerInvariant(),
    };
}
