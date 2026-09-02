// PRESENTATION LAYER FOR FIRST-RUN ONBOARDING
//
// A unit-testable view model that owns the eight-step machine, the per-source
// configuration, the validation gates, and every side-effecting action the flow can
// take. The WPF pages bind to it and hold no policy of their own.
//
// This is a C# mirror of app/macos/hyperwhisper/Views/Onboarding/OnboardingFlowModel.swift.
// The four production defects that file fixes are fixed here too, by the same
// mechanisms - the pixels are the smaller half of the port:
//
//   1. Set Up Later used to leave the default Mode rewritten. Every source
//      configuration is STAGED on this model. The only writes to production state go
//      through IOnboardingSourceCommitter, and the flow always holds a restore point
//      so DeferSetup() puts the app back exactly as it was.
//   2. Parakeet download failures were invisible because only the Whisper manager
//      exposed its error to the setup screen. Both engines now feed the single
//      SetupErrorMessage, keyed on the SELECTED model's engine.
//   3. Cloud activation ran in an untracked task that could land after the sheet
//      closed. Every asynchronous action is owned by the task box, cancelled on
//      teardown, and its result is dropped unless the flow is still live.
//   4. There was no meaningful coverage. Everything below is reachable from
//      HyperWhisper.SmokeTests through the narrow interfaces in OnboardingSeams.cs.
//
// Windows-only state the macOS model has no counterpart for - the shortcut row, the
// credits figure, the four-case device availability and the sample-clip Try It - is
// marked where it appears.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels.Onboarding;

/// <summary>
/// The first-run flow. Constructed once per onboarding window, from seven seams;
/// singletons are resolved at the one composition point (Services/Onboarding), never
/// here.
/// </summary>
public sealed partial class OnboardingFlowViewModel : ViewModelBase
{
    // =========================================================================
    // DEPENDENCIES
    // =========================================================================

    private readonly IOnboardingPermissions _permissions;
    private readonly IOnboardingModelCatalog _catalog;
    private readonly IOnboardingLicenseGateway _license;
    private readonly IOnboardingCreditsGateway _credits;
    private readonly IOnboardingProviderKeyGateway _providerKeys;
    private readonly IOnboardingAudioGateway _audio;
    private readonly IOnboardingSourceCommitter _committer;
    private readonly string _systemDefaultDeviceName;

    // =========================================================================
    // PRIVATE STATE
    // =========================================================================

    private readonly OnboardingTaskBox _taskBox = new();

    private OnboardingDownloadErrors _downloadErrors = OnboardingDownloadErrors.None;
    private string? _activationErrorMessage;
    private string? _providerErrorMessage;

    /// <summary>
    /// Captured before the first write to production state so deferral can undo it
    /// exactly. null means production state has not been touched at all.
    /// </summary>
    private IOnboardingRestorePoint? _restorePoint;

    /// <summary>
    /// Bug 1, BYOK branch. "Test API key" has to write the candidate key to the
    /// credential store before it can be trusted, so the value it overwrites is
    /// captured here first. Only the FIRST capture per provider counts, so repeated
    /// tests still roll back to the pre-onboarding key. "" encodes "no key", which
    /// Persist turns into a delete, so the rollback is exact either way.
    /// </summary>
    private readonly Dictionary<CloudTranscriptionProvider, string> _providerKeyRestorePoints = new();

    /// <summary>
    /// Providers whose pre-onboarding key could NOT be put back by the last
    /// rollback. Empty on a clean one. The window reads it and tells the user, so
    /// a credential the flow overwrote and then failed to restore is never reported
    /// as a successful deferral.
    /// </summary>
    private readonly List<CloudTranscriptionProvider> _unrestoredProviderKeys = new();

    /// <summary>
    /// The exact trimmed key that passed a probe AND a credential write THIS
    /// session, per provider.
    ///
    /// It is keyed on the KEY, not just the provider. A per-provider flag survived
    /// an edit of the field, so validating key A and then typing key B left
    /// Continue enabled and the Done step reporting B as saved while Credential
    /// Manager still held A. Same semantics as the licence branch's
    /// <see cref="_lastValidatedLicenseKey"/>.
    ///
    /// It survives ResetConfigureTestResults() so Back navigation does not shut the
    /// gate on a key that was just verified, while a pre-existing stored key that
    /// was never probed here stays untrusted.
    /// </summary>
    private readonly Dictionary<CloudTranscriptionProvider, string> _validatedProviderKeys = new();

    /// <summary>
    /// The exact trimmed key whose licence probe last passed this session. Editing
    /// the field closes the gate through the string mismatch; retyping the validated
    /// key reopens it, mirroring the BYOK stored-key semantics.
    /// </summary>
    private string? _lastValidatedLicenseKey;

    /// <summary>
    /// Bug 1, microphone step. SelectDevice writes the app's input-device setting
    /// immediately, so the values it replaces are captured on the first change. null
    /// is a real value here ("follow the system default"), hence the separate flag.
    /// </summary>
    private bool _didCaptureDevice;
    private string? _previousDeviceId;
    private string? _previousOpenDeviceId;

    /// <summary>
    /// The guarded commit boundary (bug 3). Flipped false the moment the flow is
    /// finished, so a late continuation can never write onboarding state.
    /// </summary>
    private bool _isLive = true;

    // =========================================================================
    // INIT
    // =========================================================================

    public OnboardingFlowViewModel(
        IOnboardingPermissions permissions,
        IOnboardingModelCatalog catalog,
        IOnboardingLicenseGateway license,
        IOnboardingCreditsGateway credits,
        IOnboardingProviderKeyGateway providerKeys,
        IOnboardingAudioGateway audio,
        IOnboardingSourceCommitter committer,
        string? systemDefaultDeviceName = null)
    {
        _permissions = permissions;
        _catalog = catalog;
        _license = license;
        _credits = credits;
        _providerKeys = providerKeys;
        _audio = audio;
        _committer = committer;
        _systemDefaultDeviceName = string.IsNullOrEmpty(systemDefaultDeviceName)
            ? Loc.S("onboarding.mic.device.systemDefault")
            : systemDefaultDeviceName;

        _catalog.DownloadErrorsChanged += OnDownloadErrorsChanged;
        _catalog.DownloadActivity += OnDownloadActivity;
        _permissions.ShortcutChanged += OnShortcutChanged;
        _credits.CreditsChanged += OnCreditsChanged;
        _audio.DevicesChanged += OnDevicesChanged;
        _audio.IsRecordingChanged += OnIsRecordingChanged;
        _audio.TranscriptChanged += OnTranscriptChanged;
        _audio.TranscriptWarningChanged += OnTranscriptWarningChanged;
        _audio.InputLevelChanged += OnInputLevelChanged;

        DeviceAvailability = _audio.Availability;
        ApplyShortcutState();
        ApplyCredits();
        RefreshPermissions();
    }

    /// <summary>
    /// Detach from the seams and cancel anything in flight. Called by the window on
    /// close, AFTER Complete() or DeferSetup() has decided what to do with the
    /// staged configuration. It commits nothing on its own.
    /// </summary>
    public void Cleanup()
    {
        _isLive = false;
        _taskBox.CancelAll();

        _catalog.DownloadErrorsChanged -= OnDownloadErrorsChanged;
        _catalog.DownloadActivity -= OnDownloadActivity;
        _permissions.ShortcutChanged -= OnShortcutChanged;
        _credits.CreditsChanged -= OnCreditsChanged;
        _audio.DevicesChanged -= OnDevicesChanged;
        _audio.IsRecordingChanged -= OnIsRecordingChanged;
        _audio.TranscriptChanged -= OnTranscriptChanged;
        _audio.TranscriptWarningChanged -= OnTranscriptWarningChanged;
        _audio.InputLevelChanged -= OnInputLevelChanged;
    }

    // =========================================================================
    // STEP MACHINE
    // =========================================================================

    private OnboardingStep _step = OnboardingStep.Welcome;

    /// <summary>The step currently on screen.</summary>
    public OnboardingStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value))
                return;

            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(ShowsSetUpLater));
            RaiseGateChanged();
        }
    }

    /// <summary>The footer shows Back on every step but the first.</summary>
    public bool CanGoBack => Step != OnboardingSteps.First;

    /// <summary>The footer shows "Set Up Later" on every step but the last.</summary>
    public bool ShowsSetUpLater => Step != OnboardingSteps.Last;

    /// <summary>
    /// The single source of primary-button enablement. Mirrors
    /// OnboardingFlowModel.swift:796-830. Credits and device availability are
    /// deliberately absent: neither ever gates the flow.
    /// </summary>
    public bool CanContinue => Step switch
    {
        OnboardingStep.Welcome => true,
        OnboardingStep.Permissions => HasMicrophoneAccess,
        OnboardingStep.Source => SelectedSource is not null,
        OnboardingStep.Configure => ConfigureGateIsOpen,
        OnboardingStep.Setup => IsSelectedSourceUsable,
        _ => true
    };

    private bool ConfigureGateIsOpen
    {
        get
        {
            if (SelectedSource is not { } source)
                return false;

            switch (source)
            {
                case OnboardingSourceKind.OnDevice:
                    return SelectedModel is not null;

                case OnboardingSourceKind.HyperWhisperCloud:
                    // A working key, not merely a typed one. Either the licence is
                    // already active on this PC, the inline test passed, or the field
                    // still holds the exact key that passed earlier this session
                    // (Back navigation clears KeyValidated, not the fact that the key
                    // was verified).
                    var key = LicenseKeyInput.Trim();
                    return _license.IsActive
                        || KeyValidated
                        || (key.Length > 0 && key == _lastValidatedLicenseKey);

                case OnboardingSourceKind.YourProvider:
                    // KeyValidated is cleared every time this step appears, so the
                    // per-session record keeps the gate open across Back navigation.
                    // A key that merely sits in the credential store but was never
                    // probed this session does not count, and neither does a key
                    // that passed and has since been edited.
                    return KeyValidated || SelectedProviderKeyIsValidated;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The mandatory gate on the setup step: is the chosen source genuinely usable
    /// right now.
    /// </summary>
    public bool IsSelectedSourceUsable
    {
        get
        {
            if (SelectedSource is not { } source)
                return false;

            return source switch
            {
                OnboardingSourceKind.OnDevice =>
                    SelectedModel is not null && _catalog.IsInstalled(SelectedModel),

                // Activation, not a passing probe.
                OnboardingSourceKind.HyperWhisperCloud => _license.IsActive,

                // Stored AND verified this session, for the key that is in the field
                // NOW: an unprobed pre-existing key must not read as "validated" on
                // the setup checklist, and neither must a superseded one.
                OnboardingSourceKind.YourProvider =>
                    _providerKeys.HasKey(SelectedProvider) && SelectedProviderKeyIsValidated,

                _ => false
            };
        }
    }

    /// <summary>
    /// True when the key currently in the field is the exact key that passed a
    /// probe and a credential write for the selected provider this session.
    /// </summary>
    private bool SelectedProviderKeyIsValidated
    {
        get
        {
            if (!_validatedProviderKeys.TryGetValue(SelectedProvider, out var validated))
                return false;

            return validated.Length > 0 && validated == ApiKeyInput.Trim();
        }
    }

    /// <summary>Move to the next step if the gate is open. Returns false when it is not.</summary>
    public bool Advance()
    {
        if (!CanContinue)
            return false;

        var next = (int)Step + 1;
        if (next > (int)OnboardingSteps.Last)
            return false;

        StepWillLeave(Step);
        Step = (OnboardingStep)next;
        StepDidChange();
        return true;
    }

    /// <summary>Move to the previous step. Returns false at the first step.</summary>
    public bool Back()
    {
        var previous = (int)Step - 1;
        if (previous < (int)OnboardingSteps.First)
            return false;

        StepWillLeave(Step);
        Step = (OnboardingStep)previous;
        StepDidChange();
        return true;
    }

    /// <summary>
    /// The step-exit hooks. macOS runs these from each step view's .onDisappear; a
    /// WPF Frame gives no equivalent guarantee, so the machine owns them.
    /// </summary>
    private void StepWillLeave(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.Microphone:
                EndMicrophoneStep();
                break;

            case OnboardingStep.TryIt:
                EndTryItStep();
                break;
        }
    }

    /// <summary>
    /// The step-entry hooks, which are also what makes the event-based seams fire at
    /// least once. macOS runs these from each step view's .onAppear, which fires in
    /// BOTH directions, so this runs from Back as well as Advance.
    /// </summary>
    private void StepDidChange()
    {
        switch (Step)
        {
            case OnboardingStep.Permissions:
                RefreshPermissions();
                RefreshShortcutRegistration();
                break;

            case OnboardingStep.Configure:
                ResetConfigureTestResults();
                if (SelectedSource == OnboardingSourceKind.HyperWhisperCloud)
                    RefreshCredits(force: false);
                break;

            case OnboardingStep.Setup:
                RefreshSetupError();
                if (SelectedSource == OnboardingSourceKind.HyperWhisperCloud)
                    RefreshCredits(force: false);
                break;

            case OnboardingStep.Microphone:
                BeginMicrophoneStep();
                break;

            case OnboardingStep.TryIt:
                // The Try It step has to record through the source the user just set
                // up, so this is the one place production state is written before
                // completion. It is fully reversible: DeferSetup() restores the
                // captured point.
                ApplyStagedSourceReversibly();
                BeginTryItStep();
                break;
        }
    }

    // =========================================================================
    // PERMISSIONS
    // =========================================================================

    private bool _hasMicrophoneAccess;

    /// <summary>The one permission that gates the flow.</summary>
    public bool HasMicrophoneAccess
    {
        get => _hasMicrophoneAccess;
        private set
        {
            if (SetProperty(ref _hasMicrophoneAccess, value))
                RaiseGateChanged();
        }
    }

    private OnboardingMicrophoneAuthorization _microphoneAuthorization = OnboardingMicrophoneAuthorization.Undetermined;

    public OnboardingMicrophoneAuthorization MicrophoneAuthorization
    {
        get => _microphoneAuthorization;
        private set => SetProperty(ref _microphoneAuthorization, value);
    }

    /// <summary>Non-null when the last permission request was refused. Drives the alert.</summary>
    [ObservableProperty]
    private string? _permissionErrorMessage;

    // --- The shortcut row (Windows-only; macOS shows Accessibility here) --------

    private string _shortcutDisplay = string.Empty;

    /// <summary>
    /// The configured toggle shortcut, already formatted. The UI splits it on "+"
    /// to draw keycaps.
    /// </summary>
    public string ShortcutDisplay
    {
        get => _shortcutDisplay;
        private set => SetProperty(ref _shortcutDisplay, value);
    }

    private OnboardingShortcutStatus _shortcutStatus = OnboardingShortcutStatus.Unknown;

    /// <summary>
    /// Whether the shortcut is registered. Unknown is a real state, not a failure,
    /// and none of the three ever gates Continue.
    /// </summary>
    public OnboardingShortcutStatus ShortcutStatus
    {
        get => _shortcutStatus;
        private set => SetProperty(ref _shortcutStatus, value);
    }

    private string? _shortcutFailureReason;

    /// <summary>A user-facing sentence produced by the adapter, never a Win32 code.</summary>
    public string? ShortcutFailureReason
    {
        get => _shortcutFailureReason;
        private set => SetProperty(ref _shortcutFailureReason, value);
    }

    /// <summary>
    /// Re-read both permissions. Called on entry to the step and, by the window, on
    /// activation, so a trip to Windows Settings is picked up.
    /// </summary>
    public void RefreshPermissions()
    {
        MicrophoneAuthorization = _permissions.MicrophoneAuthorization;
        HasMicrophoneAccess = MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Authorized;
        // Keep the audio gateway's own preview guard from holding stale state after
        // the user returns from Windows Settings.
        _audio.RefreshMicrophoneAuthorization();
        DeviceAvailability = _audio.Availability;
        ApplyShortcutState();
    }

    /// <summary>
    /// Re-run the registration check. This is the Windows replacement for macOS's
    /// polling waitForAccessibilityPermission: cheap, on demand, no timer.
    /// </summary>
    public void RefreshShortcutRegistration()
    {
        _permissions.RefreshShortcutRegistration();
        ApplyShortcutState();
    }

    private void ApplyShortcutState()
    {
        var state = _permissions.Shortcut;
        ShortcutDisplay = state.DisplayText;
        ShortcutStatus = state.Status;
        ShortcutFailureReason = state.Status == OnboardingShortcutStatus.Failed ? state.FailureReason : null;
    }

    /// <summary>
    /// The microphone row's action. Windows cannot re-prompt, so anything other than
    /// Undetermined deep-links Windows Settings.
    /// </summary>
    [RelayCommand]
    public void HandleMicrophoneAction()
    {
        if (_permissions.MicrophoneAuthorization == OnboardingMicrophoneAuthorization.Undetermined)
        {
            RequestMicrophoneAccess();
            return;
        }

        _permissions.OpenMicrophonePrivacySettings();
    }

    /// <summary>Ask for microphone access. Kept for shape parity with macOS.</summary>
    public void RequestMicrophoneAccess()
    {
        RunTracked(OnboardingTaskKeys.MicrophonePermission, RequestMicrophoneAccessCoreAsync);
    }

    private async Task RequestMicrophoneAccessCoreAsync(CancellationToken cancellationToken)
    {
        var granted = await _permissions.RequestMicrophoneAccessAsync();
        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        HasMicrophoneAccess = granted;
        MicrophoneAuthorization = granted
            ? OnboardingMicrophoneAuthorization.Authorized
            : OnboardingMicrophoneAuthorization.Denied;

        if (!granted)
            PermissionErrorMessage = Loc.S("onboarding.error.microphone.denied");

        _taskBox.Clear(OnboardingTaskKeys.MicrophonePermission);
    }

    /// <summary>
    /// Open the shortcut editor and re-check when we come back. The row never gates
    /// Continue, so this is an offer, not a requirement.
    /// </summary>
    [RelayCommand]
    public void ChooseDifferentShortcut()
    {
        _permissions.OpenShortcutSettings();
        RefreshShortcutRegistration();
    }

    // =========================================================================
    // SOURCE SELECTION (STAGED ONLY)
    // =========================================================================

    private OnboardingSourceKind? _selectedSource;

    public OnboardingSourceKind? SelectedSource
    {
        get => _selectedSource;
        private set
        {
            if (SetProperty(ref _selectedSource, value))
                RaiseGateChanged();
        }
    }

    private OnboardingModelSelection? _selectedModel;

    public OnboardingModelSelection? SelectedModel
    {
        get => _selectedModel;
        private set
        {
            if (!SetProperty(ref _selectedModel, value))
                return;

            OnPropertyChanged(nameof(IsSelectedModelInstalled));
            OnPropertyChanged(nameof(IsSelectedModelDownloading));
            OnPropertyChanged(nameof(SelectedModelProgress));
            RaiseGateChanged();
        }
    }

    private CloudTranscriptionProvider _selectedProvider = CloudTranscriptionProvider.OpenAI;

    public CloudTranscriptionProvider SelectedProvider
    {
        get => _selectedProvider;
        private set
        {
            if (SetProperty(ref _selectedProvider, value))
                RaiseGateChanged();
        }
    }

    /// <summary>
    /// The HyperWhisper Cloud access key. Editing it invalidates any pass, so a
    /// stale result can never open the gate.
    /// </summary>
    [ObservableProperty]
    private string _licenseKeyInput = string.Empty;

    partial void OnLicenseKeyInputChanged(string value) => InvalidateLicenseValidation();

    /// <summary>The BYOK API key for the selected provider.</summary>
    [ObservableProperty]
    private string _apiKeyInput = string.Empty;

    partial void OnApiKeyInputChanged(string value) => InvalidateProviderValidation();

    [RelayCommand]
    public void SelectSource(OnboardingSourceKind source)
    {
        if (SelectedSource == source)
            return;

        SelectedSource = source;
        KeyValidated = false;
        LicenseTestPassed = null;
        ProviderTestHealth = null;
        _activationErrorMessage = null;
        _providerErrorMessage = null;

        if (source == OnboardingSourceKind.OnDevice && SelectedModel is null)
        {
            SelectedModel = _catalog.Models.FirstOrDefault(m => m.IsRecommended)
                ?? _catalog.Models.FirstOrDefault();
        }

        RefreshSetupError();
    }

    [RelayCommand]
    public void SelectModel(OnboardingModelSelection model)
    {
        if (SelectedModel == model)
            return;

        SelectedModel = model;
        RefreshSetupError();
    }

    [RelayCommand]
    public void SelectProvider(CloudTranscriptionProvider provider)
    {
        if (SelectedProvider == provider)
            return;

        SelectedProvider = provider;
        // A masked key typed for one provider must never be saved under another.
        ApiKeyInput = string.Empty;
        InvalidateProviderValidation();
    }

    private void InvalidateLicenseValidation()
    {
        LicenseTestPassed = null;
        _activationErrorMessage = null;
        if (SelectedSource == OnboardingSourceKind.HyperWhisperCloud)
            KeyValidated = false;
        RefreshSetupError();
    }

    private void InvalidateProviderValidation()
    {
        ProviderTestHealth = null;
        _providerErrorMessage = null;
        if (SelectedSource == OnboardingSourceKind.YourProvider)
            KeyValidated = false;
        RefreshSetupError();
    }

    /// <summary>
    /// Clears any inline test result so a pass from a previous visit cannot be read
    /// as a pass for whatever is in the field now. Runs on every entry to the
    /// Configure step, in both directions. It deliberately does NOT clear the
    /// per-session validation records - that exclusion is the whole reason they exist.
    /// </summary>
    public void ResetConfigureTestResults()
    {
        IsTestingKey = false;
        LicenseTestPassed = null;
        ProviderTestHealth = null;
        _activationErrorMessage = null;
        _providerErrorMessage = null;
        KeyValidated = false;
        RefreshSetupError();
    }

    // =========================================================================
    // VALIDATION
    // =========================================================================

    private bool _keyValidated;

    /// <summary>
    /// True only while the inline test has a passing result for the CURRENT key and
    /// provider. Cleared by every edit so a stale pass cannot open the gate.
    /// </summary>
    public bool KeyValidated
    {
        get => _keyValidated;
        private set
        {
            if (SetProperty(ref _keyValidated, value))
                RaiseGateChanged();
        }
    }

    private bool _isTestingKey;

    public bool IsTestingKey
    {
        get => _isTestingKey;
        private set => SetProperty(ref _isTestingKey, value);
    }

    private bool? _licenseTestPassed;

    public bool? LicenseTestPassed
    {
        get => _licenseTestPassed;
        private set => SetProperty(ref _licenseTestPassed, value);
    }

    private ProviderHealth? _providerTestHealth;

    public ProviderHealth? ProviderTestHealth
    {
        get => _providerTestHealth;
        private set => SetProperty(ref _providerTestHealth, value);
    }

    /// <summary>Read-only licence check. Account state is untouched until activation.</summary>
    [RelayCommand]
    public void TestAccessKey()
    {
        var key = LicenseKeyInput.Trim();
        if (key.Length == 0)
            return;

        IsTestingKey = true;
        LicenseTestPassed = null;
        _activationErrorMessage = null;
        RunTracked(OnboardingTaskKeys.LicenseTest, ct => TestAccessKeyCoreAsync(key, ct));
    }

    private async Task TestAccessKeyCoreAsync(string key, CancellationToken cancellationToken)
    {
        OnboardingLicenseOutcome outcome;
        try
        {
            outcome = await _license.ProbeAsync(key, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        // Drop a result that arrived for a key the user has since edited.
        if (LicenseKeyInput.Trim() != key)
        {
            IsTestingKey = false;
            _taskBox.Clear(OnboardingTaskKeys.LicenseTest);
            return;
        }

        LicenseTestPassed = outcome.IsValid;
        _activationErrorMessage = outcome.IsValid ? null : outcome.ErrorMessage;
        KeyValidated = outcome.IsValid;

        if (outcome.IsValid)
        {
            _lastValidatedLicenseKey = key;
        }
        else if (_lastValidatedLicenseKey == key)
        {
            // A revoked key that fails a re-probe must not stay remembered.
            _lastValidatedLicenseKey = null;
        }

        IsTestingKey = false;
        RefreshSetupError();
        _taskBox.Clear(OnboardingTaskKeys.LicenseTest);
    }

    /// <summary>
    /// Probe the candidate key, then accept it only once the credential store
    /// confirms the write. A passing network round trip on its own is not a pass.
    /// </summary>
    [RelayCommand]
    public void TestProviderKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0)
            return;

        var provider = SelectedProvider;
        IsTestingKey = true;
        ProviderTestHealth = null;
        _providerErrorMessage = null;
        RunTracked(OnboardingTaskKeys.ProviderTest, ct => TestProviderKeyCoreAsync(provider, key, ct));
    }

    private async Task TestProviderKeyCoreAsync(
        CloudTranscriptionProvider provider,
        string key,
        CancellationToken cancellationToken)
    {
        ProviderHealth health;
        try
        {
            health = await _providerKeys.ProbeAsync(provider, key, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        // Drop a result the user has since superseded BEFORE the persist: a stale
        // probe must never write the credential store or set a restore point (which
        // would also wrongly flag a pending production write).
        if (SelectedProvider != provider || ApiKeyInput.Trim() != key)
        {
            IsTestingKey = false;
            _taskBox.Clear(OnboardingTaskKeys.ProviderTest);
            return;
        }

        var persisted = false;
        if (health == ProviderHealth.Healthy)
        {
            // Snapshot whatever this provider had BEFORE overwriting it, so Set Up
            // Later can put the user's original key back (bug 1).
            CaptureProviderKeyRestorePoint(provider);
            persisted = _providerKeys.Persist(key, provider);
        }

        if (health == ProviderHealth.Healthy && !persisted)
        {
            ProviderTestHealth = null;
            _providerErrorMessage = _providerKeys.ValidationError
                ?? Loc.S("onboarding.setup.provider.saveFailed");
            KeyValidated = false;
            // A key that failed its write must not stay remembered as validated,
            // exactly as a revoked licence key does not (see TestAccessKeyCoreAsync).
            _validatedProviderKeys.Remove(provider);
        }
        else
        {
            ProviderTestHealth = health;
            _providerErrorMessage = null;
            KeyValidated = health == ProviderHealth.Healthy && persisted;
            if (KeyValidated)
                _validatedProviderKeys[provider] = key;
        }

        IsTestingKey = false;
        RefreshSetupError();
        _taskBox.Clear(OnboardingTaskKeys.ProviderTest);
    }

    /// <summary>
    /// Records the credential value a subsequent write is about to replace. Only the
    /// FIRST capture per provider counts, so repeated tests still roll back to what
    /// the user had before onboarding rather than to an intermediate key.
    /// </summary>
    private void CaptureProviderKeyRestorePoint(CloudTranscriptionProvider provider)
    {
        if (_providerKeyRestorePoints.ContainsKey(provider))
            return;

        _providerKeyRestorePoints[provider] = _providerKeys.CurrentKey(provider);
        RaiseGateChanged();
    }

    // =========================================================================
    // SETUP STEP
    // =========================================================================

    private bool _isActivatingLicense;

    public bool IsActivatingLicense
    {
        get => _isActivatingLicense;
        private set => SetProperty(ref _isActivatingLicense, value);
    }

    private string? _setupErrorMessage;

    /// <summary>
    /// Bug 2: the single error surface for the setup step, fed by Whisper AND
    /// Parakeet download failures, licence activation failures, and credential write
    /// failures, whichever matches the selected source.
    /// </summary>
    public string? SetupErrorMessage
    {
        get => _setupErrorMessage;
        private set
        {
            if (SetProperty(ref _setupErrorMessage, value))
                OnPropertyChanged(nameof(HasSetupError));
        }
    }

    public bool HasSetupError => !string.IsNullOrEmpty(SetupErrorMessage);

    /// <summary>The curated on-device shortlist, resolved live from the catalog.</summary>
    public IReadOnlyList<OnboardingModelSelection> AvailableModels => _catalog.Models;

    public bool IsInstalled(OnboardingModelSelection model) => _catalog.IsInstalled(model);

    public bool IsSelectedModelInstalled => SelectedModel is not null && _catalog.IsInstalled(SelectedModel);

    public bool IsSelectedModelDownloading => SelectedModel is not null && _catalog.IsDownloading(SelectedModel);

    public double SelectedModelProgress => SelectedModel is null ? 0 : _catalog.Progress(SelectedModel);

    [RelayCommand]
    public void StartSelectedModelDownload()
    {
        if (SelectedModel is not { } model)
            return;

        _catalog.StartDownload(model);
        RefreshSetupError();
        OnDownloadActivity(this, EventArgs.Empty);
    }

    /// <summary>
    /// Bug 3. The activation task is owned, replaces any earlier one, and its result
    /// is discarded unless the flow is still live. Activation is the user's single
    /// explicit account action, so entitlement stays server enforced; nothing here
    /// shortcuts or fakes it.
    /// </summary>
    [RelayCommand]
    public void ActivateCloudLicense()
    {
        var key = LicenseKeyInput.Trim();
        if (key.Length == 0 || IsActivatingLicense)
            return;

        IsActivatingLicense = true;
        _activationErrorMessage = null;
        RunTracked(OnboardingTaskKeys.Activation, ct => ActivateCloudLicenseCoreAsync(key, ct));
    }

    private async Task ActivateCloudLicenseCoreAsync(string key, CancellationToken cancellationToken)
    {
        OnboardingLicenseOutcome outcome;
        try
        {
            outcome = await _license.ActivateAsync(key, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        IsActivatingLicense = false;
        _activationErrorMessage = outcome.IsValid ? null : outcome.ErrorMessage;
        if (outcome.IsValid)
            KeyValidated = true;

        RefreshSetupError();
        _taskBox.Clear(OnboardingTaskKeys.Activation);
    }

    [RelayCommand]
    public void SaveProviderKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0)
            return;

        CaptureProviderKeyRestorePoint(SelectedProvider);
        var persisted = _providerKeys.Persist(key, SelectedProvider);
        _providerErrorMessage = persisted
            ? null
            : (_providerKeys.ValidationError ?? Loc.S("onboarding.setup.provider.saveFailed"));
        RefreshSetupError();
    }

    /// <summary>
    /// One error property for the setup step, per selected source. The on-device
    /// branch reads whichever engine the SELECTED model belongs to, which is what
    /// makes Parakeet failures visible.
    ///
    /// Everything here is produced INSIDE this flow. The licence manager's last error
    /// and the API key service's global validation state are app-wide, long-lived and
    /// unobserved, so falling back to them would render an unrelated failure from an
    /// earlier session before the user had done anything on the step. A credits fetch
    /// failure is not part of this funnel either.
    /// </summary>
    private void RefreshSetupError()
    {
        SetupErrorMessage = SelectedSource switch
        {
            OnboardingSourceKind.OnDevice =>
                SelectedModel is null ? null : _downloadErrors.Message(SelectedModel.Kind),
            OnboardingSourceKind.HyperWhisperCloud => _activationErrorMessage,
            OnboardingSourceKind.YourProvider => _providerErrorMessage,
            _ => null
        };

        RaiseGateChanged();
    }

    // =========================================================================
    // CREDITS (Windows-only seam; macOS reads the singleton from its views)
    // =========================================================================

    private string _creditsFormatted = "…";

    /// <summary>
    /// The balance, or an ellipsis while it is unknown. Display only: it never gates
    /// Continue, and a failed fetch is not a setup error.
    /// </summary>
    public string CreditsFormatted
    {
        get => _creditsFormatted;
        private set => SetProperty(ref _creditsFormatted, value);
    }

    private bool _hasCredits;

    public bool HasCredits
    {
        get => _hasCredits;
        private set => SetProperty(ref _hasCredits, value);
    }

    private bool _isFetchingCredits;

    public bool IsFetchingCredits
    {
        get => _isFetchingCredits;
        private set => SetProperty(ref _isFetchingCredits, value);
    }

    /// <summary>Kick a balance refresh. Failures are swallowed into "unknown".</summary>
    public void RefreshCredits(bool force)
    {
        RunTracked(OnboardingTaskKeys.CreditsRefresh, ct => RefreshCreditsCoreAsync(force, ct));
    }

    private async Task RefreshCreditsCoreAsync(bool force, CancellationToken cancellationToken)
    {
        ApplyCredits();

        try
        {
            await _credits.RefreshAsync(force, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Display only. A network failure here must never surface as a setup
            // error or close the gate; the figure simply stays unknown.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        ApplyCredits();
        _taskBox.Clear(OnboardingTaskKeys.CreditsRefresh);
    }

    private void ApplyCredits()
    {
        var credits = _credits.Credits;
        HasCredits = credits is not null;
        CreditsFormatted = credits?.FormattedBalance ?? "…";
        IsFetchingCredits = _credits.IsFetching;
    }

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

    public void BeginMicrophoneStep()
    {
        _audio.RefreshDevices();
        _audio.RefreshMicrophoneAuthorization();
        DeviceAvailability = _audio.Availability;
        RefreshDeviceOptions();

        // From the OPEN, not from availability. A device that enumerates can still
        // refuse to open (another app holds it exclusively, consent flips between
        // the read and the open, the driver faults), and lighting the meter on
        // availability alone left 33 bars frozen under a live "speak to see the
        // level" hint.
        IsLevelMeterActive = DeviceAvailability == OnboardingDeviceAvailability.Available
            && _audio.StartInputLevelPreview();
    }

    public void EndMicrophoneStep()
    {
        _audio.StopInputLevelPreview();
        IsLevelMeterActive = false;
    }

    public void RefreshDeviceOptions() => ApplyDeviceList(_audio.Devices);

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

        // Re-point the metering session at the newly selected device. Same rule as
        // BeginMicrophoneStep: the flag follows the open, not the availability.
        IsLevelMeterActive = _audio.StartInputLevelPreview();
    }

    // =========================================================================
    // TRY IT STEP
    // =========================================================================

    private bool _isRecording;

    public bool IsRecording
    {
        get => _isRecording;
        private set => SetProperty(ref _isRecording, value);
    }

    private string _transcript = string.Empty;

    public string Transcript
    {
        get => _transcript;
        private set
        {
            if (!SetProperty(ref _transcript, value))
                return;

            OnPropertyChanged(nameof(TranscriptIsError));
            OnPropertyChanged(nameof(TranscriptBody));
            OnPropertyChanged(nameof(HasTranscript));
        }
    }

    public bool HasTranscript => Transcript.Length > 0;

    /// <summary>
    /// Recording failures arrive through the same channel as transcripts with an
    /// "Error:" sentinel, so the view can render them differently.
    /// </summary>
    public bool TranscriptIsError => Transcript.StartsWith("Error:", StringComparison.Ordinal);

    public string TranscriptBody =>
        TranscriptIsError ? Transcript["Error:".Length..].Trim() : Transcript;

    private OnboardingTryItMode _tryItMode = OnboardingTryItMode.Record;

    /// <summary>
    /// Which primary control the step offers. On a machine with no capture device the
    /// Record button would only ever produce an error, so the bundled sample clip
    /// takes its place.
    /// </summary>
    public OnboardingTryItMode TryItMode
    {
        get => _tryItMode;
        private set => SetProperty(ref _tryItMode, value);
    }

    private bool _transcriptCameFromSample;

    /// <summary>So the result line can say truthfully which of the two happened.</summary>
    public bool TranscriptCameFromSample
    {
        get => _transcriptCameFromSample;
        private set => SetProperty(ref _transcriptCameFromSample, value);
    }

    private bool _isTranscribingSample;

    public bool IsTranscribingSample
    {
        get => _isTranscribingSample;
        private set => SetProperty(ref _isTranscribingSample, value);
    }

    private bool _isTranscribingTestRecording;

    /// <summary>
    /// The microphone path's equivalent of <see cref="IsTranscribingSample"/>. Set
    /// the moment Stop is pressed and cleared when the transcript lands, so the
    /// step can say "transcribing" rather than showing "Nothing here yet" beside a
    /// live Record button for the whole of a local model's run.
    /// </summary>
    public bool IsTranscribingTestRecording
    {
        get => _isTranscribingTestRecording;
        private set => SetProperty(ref _isTranscribingTestRecording, value);
    }

    private string? _transcriptWarning;

    /// <summary>
    /// A non-fatal warning about the current transcript, or null. Post-processing
    /// that was SKIPPED (a 401, a timeout) still returns text, and five of the six
    /// seeded Modes post-process through a cloud LLM, so without this the user
    /// reads a raw transcript under full success chrome and concludes the source
    /// works. The GUI's toast handler deliberately drops the Onboarding call site,
    /// because a toast behind a modal cannot be seen.
    /// </summary>
    public string? TranscriptWarning
    {
        get => _transcriptWarning;
        private set
        {
            if (SetProperty(ref _transcriptWarning, value))
                OnPropertyChanged(nameof(HasTranscriptWarning));
        }
    }

    public bool HasTranscriptWarning => !string.IsNullOrEmpty(TranscriptWarning);

    public bool HasSampleClip => _audio.HasSampleClip;

    public void BeginTryItStep()
    {
        TryItMode = DeviceAvailability != OnboardingDeviceAvailability.Available && _audio.HasSampleClip
            ? OnboardingTryItMode.Sample
            : OnboardingTryItMode.Record;
        TranscriptCameFromSample = false;
        _audio.ClearTranscript();
    }

    public void EndTryItStep()
    {
        // Cancel the owned transcription before tearing the recorder down, so a
        // running orchestrator call is not left billing against a disposed gateway.
        _taskBox.Cancel(OnboardingTaskKeys.TestRecording);
        _audio.StopRecordingForExit();
        _audio.ClearTranscript();
        IsTranscribingSample = false;
        IsTranscribingTestRecording = false;
    }

    /// <summary>
    /// Start the capture, or stop it and transcribe.
    ///
    /// The stop half runs under the SAME task box as the sample-clip path. The
    /// first cut let the gateway fire it as a discarded task with
    /// CancellationToken.None, which cost three things at once: the step showed
    /// "Nothing here yet" beside a live Record button for the whole of a local
    /// model's run, a second press started an overlapping capture into the same
    /// transcript channel, and "Set Up Later" disposed the gateway and the recorder
    /// out from under a running, billable orchestrator call.
    /// </summary>
    [RelayCommand]
    public void ToggleTestRecording()
    {
        // Re-entrancy: no second capture while the last one is still transcribing.
        if (IsTranscribingTestRecording || IsTranscribingSample)
            return;

        TranscriptCameFromSample = false;

        if (!IsRecording)
        {
            _audio.StartTestRecording();
            return;
        }

        IsTranscribingTestRecording = true;
        RunTracked(OnboardingTaskKeys.TestRecording, StopAndTranscribeCoreAsync);
    }

    private async Task StopAndTranscribeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audio.StopAndTranscribeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The gateway publishes its own failures on the transcript channel with
            // the "Error:" sentinel. A throw that escapes it must still leave the
            // step usable rather than tearing the flow down.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        IsTranscribingTestRecording = false;
        _taskBox.Clear(OnboardingTaskKeys.TestRecording);
    }

    /// <summary>
    /// Run the bundled clip through the configured source. It exercises model load,
    /// provider routing, post-processing and the transcript render; only capture is
    /// different.
    /// </summary>
    [RelayCommand]
    public void TranscribeSampleClip()
    {
        if (!_audio.HasSampleClip || IsTranscribingSample || IsTranscribingTestRecording)
            return;

        IsTranscribingSample = true;
        TranscriptCameFromSample = true;
        RunTracked(OnboardingTaskKeys.SampleClip, TranscribeSampleClipCoreAsync);
    }

    private async Task TranscribeSampleClipCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audio.TranscribeSampleClipAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The gateway publishes its own failures on the transcript channel with
            // the "Error:" sentinel. A throw that escapes it must still leave the
            // step usable rather than tearing the flow down.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        IsTranscribingSample = false;
        _taskBox.Clear(OnboardingTaskKeys.SampleClip);
    }

    // =========================================================================
    // STAGING AND COMMIT
    // =========================================================================

    /// <summary>
    /// The staged configuration, or null while the user has not chosen a source.
    /// Computed on demand: producing it touches nothing.
    /// </summary>
    public OnboardingStagedSource? StagedSource
    {
        get
        {
            if (SelectedSource is not { } source)
                return null;

            return source switch
            {
                // Fully offline: local model, post-processing off.
                OnboardingSourceKind.OnDevice => new OnboardingStagedSource(
                    OnboardingSourceKind.OnDevice,
                    SelectedModel?.Id ?? "base",
                    null,
                    0,
                    null),

                OnboardingSourceKind.HyperWhisperCloud => new OnboardingStagedSource(
                    OnboardingSourceKind.HyperWhisperCloud,
                    "cloud",
                    "hyperwhisper",
                    1,
                    CloudAccuracyTier.ElevenLabsScribeV2.ToStorageValue()),

                // Post-processing off by default so first run never fails on a
                // missing post-processing key.
                OnboardingSourceKind.YourProvider => new OnboardingStagedSource(
                    OnboardingSourceKind.YourProvider,
                    "cloud",
                    SelectedProvider.GetIdentifier(),
                    0,
                    null),

                _ => null
            };
        }
    }

    /// <summary>
    /// True once production state has been written and not yet restored. Covers all
    /// three reversible writes: the default Mode, the credential store, and the
    /// selected input device.
    /// </summary>
    public bool HasPendingProductionWrite =>
        _restorePoint is not null || _providerKeyRestorePoints.Count > 0 || _didCaptureDevice;

    /// <summary>
    /// Providers whose pre-onboarding API key the last rollback could not put back.
    /// Empty unless the credential store refused the write twice. The window
    /// surfaces this rather than closing silently over a lost key.
    /// </summary>
    public IReadOnlyList<CloudTranscriptionProvider> UnrestoredProviderKeys => _unrestoredProviderKeys;

    private void ApplyStagedSourceReversibly()
    {
        if (StagedSource is not { } staged)
            return;

        if (_restorePoint is null)
        {
            _restorePoint = _committer.CaptureRestorePoint();
            RaiseGateChanged();
        }

        _committer.Apply(staged);
    }

    /// <summary>
    /// Explicit completion. The staged configuration becomes production state and
    /// there is nothing left to roll back.
    /// </summary>
    [RelayCommand]
    public void Complete()
    {
        if (!_isLive)
            return;

        ApplyStagedSourceReversibly();
        _restorePoint = null;
        _providerKeyRestorePoints.Clear();
        _didCaptureDevice = false;
        _previousDeviceId = null;
        _previousOpenDeviceId = null;
        Finish(markCompleted: true);
    }

    /// <summary>
    /// Set Up Later. Bug 1: every reversible write this flow made is put back, so the
    /// default Mode, the active mode selection, the provider API keys, and the
    /// selected input device are exactly what they were before the window opened.
    /// Downloaded models are deliberately kept (harmless, and the user paid the
    /// bytes), as is an activated HyperWhisper Cloud licence: activation is a
    /// server-side account action, not local state this flow can un-write.
    ///
    /// This is an EXPLICIT decision by the user, so it closes first run for good,
    /// exactly as macOS's <c>deferSetup()</c> does (it reaches the same
    /// <c>markOnboardingCompleted()</c> as <c>complete()</c>). A close that is NOT
    /// a decision goes to <see cref="AbandonSetup"/> instead.
    /// </summary>
    /// <remarks>
    /// Read <see cref="UnrestoredProviderKeys"/> afterwards. A reversible write
    /// that could not be put back has to be REPORTED: silently closing over a lost
    /// credential is what that list exists to prevent. The method stays void
    /// because [RelayCommand] only generates for void and Task.
    /// </remarks>
    [RelayCommand]
    public void DeferSetup()
    {
        if (!_isLive)
            return;

        Rollback();
        Finish(markCompleted: true);
    }

    /// <summary>
    /// The window went away without the user deciding anything: Alt+F4, the
    /// taskbar, tray Quit, or the OS ending the session for an update.
    ///
    /// It rolls back exactly like <see cref="DeferSetup"/> but does NOT mark first
    /// run complete, so <c>SettingsService.OnboardingPending</c> survives and the
    /// interrupted run is re-offered on the next launch. That is macOS's behaviour
    /// too: its sheet is <c>.interactiveDismissDisabled()</c> and has no close
    /// button, so a process that dies mid-flow never reaches
    /// <c>markOnboardingCompleted()</c> and both of its flags stay put. Windows has
    /// an OS-supplied caption X and a real shutdown path, which macOS does not, so
    /// the distinction has to be made in code rather than by the frame.
    /// </summary>
    public void AbandonSetup()
    {
        if (!_isLive)
            return;

        Rollback();
        Finish(markCompleted: false);
    }

    /// <summary>
    /// The footer primary: Continue everywhere, "Done Onboarding" on the last step.
    /// </summary>
    [RelayCommand]
    public void Continue()
    {
        if (Step == OnboardingSteps.Last)
        {
            Complete();
            return;
        }

        Advance();
    }

    [RelayCommand]
    public void GoBack() => Back();

    /// <summary>
    /// Put every reversible write back.
    /// </summary>
    /// <returns>
    /// True when everything went back. The credential store is the one sink here
    /// that can REFUSE a write (Windows Credential Manager returns a Win32 error;
    /// <c>Persist</c> reports it as false), and a rollback that drops that answer
    /// turns "your original key is gone" into a clean deferral.
    /// </returns>
    private bool Rollback()
    {
        if (_restorePoint is { } point)
        {
            _committer.Restore(point);
            _restorePoint = null;
        }

        // "Test API key" writes to the credential store before any commit boundary,
        // so deferral has to put the previous value back. "" is how the store encodes
        // "no key", so a provider that had nothing ends up with nothing.
        _unrestoredProviderKeys.Clear();
        var restoredProviders = new List<CloudTranscriptionProvider>();

        foreach (var entry in _providerKeyRestorePoints)
        {
            // One retry: the common failure is a transient Credential Manager lock,
            // and the alternative to a second attempt is losing the key outright.
            var persisted = _providerKeys.Persist(entry.Value, entry.Key)
                || _providerKeys.Persist(entry.Value, entry.Key);

            if (persisted)
            {
                restoredProviders.Add(entry.Key);
                continue;
            }

            _unrestoredProviderKeys.Add(entry.Key);
            // Fully qualified: this file is presentation and deliberately imports no
            // Services namespace, so the one place it needs the logger names it.
            HyperWhisper.Services.LoggingService.Error(
                "OnboardingFlowViewModel: could not restore the pre-onboarding API key for "
                + $"{entry.Key.GetIdentifier()}: {_providerKeys.ValidationError ?? "no reason reported"}");
        }

        // Only what actually went back is forgotten. A provider whose key could not
        // be restored keeps its restore point, so HasPendingProductionWrite stays
        // honest and a second attempt still has the value to write.
        foreach (var provider in restoredProviders)
        {
            _providerKeyRestorePoints.Remove(provider);
        }

        if (_didCaptureDevice)
        {
            _audio.RestoreDevice(_previousDeviceId, _previousOpenDeviceId);
            _didCaptureDevice = false;
            _previousDeviceId = null;
            _previousOpenDeviceId = null;
        }

        RaiseGateChanged();
        return _unrestoredProviderKeys.Count == 0;
    }

    /// <param name="markCompleted">
    /// True only when the user made an explicit decision (Done Onboarding, or Set
    /// Up Later). False when the window merely went away, which must leave
    /// OnboardingPending set so first run is re-offered.
    /// </param>
    private void Finish(bool markCompleted)
    {
        // Close the commit boundary FIRST so any in-flight continuation that is
        // already past its cancellation check still cannot write onboarding state.
        _isLive = false;
        _taskBox.CancelAll();
        _audio.StopRecordingForExit();
        _audio.StopInputLevelPreview();
        IsLevelMeterActive = false;

        if (markCompleted)
            _committer.MarkOnboardingCompleted();

        _committer.ReturnToHome();
    }

    // =========================================================================
    // SEAM EVENTS
    // =========================================================================

    private void OnDownloadErrorsChanged(object? sender, OnboardingDownloadErrors errors)
    {
        _downloadErrors = errors;
        RefreshSetupError();
    }

    private void OnDownloadActivity(object? sender, EventArgs e)
    {
        // The catalog's download state is read through plain method calls, so this
        // tick is the only thing that tells the binding layer to re-read it.
        OnPropertyChanged(nameof(SelectedModelProgress));
        OnPropertyChanged(nameof(IsSelectedModelDownloading));
        OnPropertyChanged(nameof(IsSelectedModelInstalled));
        RaiseGateChanged();
    }

    private void OnShortcutChanged(object? sender, EventArgs e) => ApplyShortcutState();

    private void OnCreditsChanged(object? sender, EventArgs e) => ApplyCredits();

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // Availability is step-independent: the Done step's summary reads it too.
        DeviceAvailability = _audio.Availability;

        // Reconcile the meter with reality. Unplugging the microphone mid-step used
        // to leave the flag true and the bars frozen at their last heights.
        if (DeviceAvailability != OnboardingDeviceAvailability.Available)
            IsLevelMeterActive = false;

        // The list itself only belongs to the microphone step, exactly as on macOS.
        if (Step == OnboardingStep.Microphone)
            ApplyDeviceList(_audio.Devices);
    }

    private void OnIsRecordingChanged(object? sender, EventArgs e) => IsRecording = _audio.IsRecording;

    private void OnTranscriptChanged(object? sender, EventArgs e) => Transcript = _audio.Transcript;

    private void OnTranscriptWarningChanged(object? sender, EventArgs e) =>
        TranscriptWarning = _audio.TranscriptWarning;

    private void OnInputLevelChanged(object? sender, float level) => InputLevel = level;

    // =========================================================================
    // INTERNALS
    // =========================================================================

    private void RaiseGateChanged()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(IsSelectedSourceUsable));
        OnPropertyChanged(nameof(StagedSource));
        OnPropertyChanged(nameof(HasPendingProductionWrite));
    }

    /// <summary>
    /// Start an asynchronous action under a task-box key. The key is registered
    /// BEFORE the body runs: a C# async method can complete synchronously, and a body
    /// that cleared a key which had not been stored yet would leave the box
    /// permanently non-empty.
    /// </summary>
    private void RunTracked(string key, Func<CancellationToken, Task> body)
    {
        var source = new CancellationTokenSource();
        _taskBox.Store(key, source);
        var task = body(source.Token);
        LastAsyncTaskForTesting = task;
    }

    // ----- Test seams --------------------------------------------------------
    // Internal, so they reach HyperWhisper.SmokeTests through InternalsVisibleTo and
    // nothing else. They grant no capability: there is no way to bypass validation or
    // entitlement here.

    internal bool HasInFlightWorkForTesting => !_taskBox.IsEmpty;

    internal bool IsLiveForTesting => _isLive;

    /// <summary>
    /// The most recently spawned asynchronous action, so a test can await the exact
    /// task instead of yielding an arbitrary number of times.
    /// </summary>
    internal Task? LastAsyncTaskForTesting { get; private set; }
}
