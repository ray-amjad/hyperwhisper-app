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
    /// True when the LAST rollback could not put the default Mode back. The
    /// Mode's counterpart to <see cref="_unrestoredProviderKeys"/>: one
    /// mechanism, two sinks, both reported by the window rather than logged and
    /// forgotten.
    /// </summary>
    private bool _modeRestoreFailed;

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
                    //
                    // KeyValidated is now SCOPED (see ValidationScope): it can only
                    // read true for a pass recorded against this source and this
                    // licence text, so a probe that lands after the user switched
                    // source cannot open this branch's half of the gate.
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
    /// True when the key that would actually be USED is the exact key that passed a
    /// probe and a credential write for the selected provider this session.
    ///
    /// A non-empty field must match: that is the fix for a remembered pass
    /// surviving an edit, where validating key A and typing key B left Continue
    /// enabled while the credential store still held A.
    ///
    /// An EMPTY field falls back to the credential store, because emptying it is
    /// the flow's own doing and not the user's: <see cref="SelectProvider"/> clears
    /// it on every provider change so a masked key typed for one vendor can never
    /// be saved under another. Switching away and back must not throw away a pass
    /// the user has already paid a network round trip for, and the stored key is
    /// what the next transcription would use.
    /// </summary>
    private bool SelectedProviderKeyIsValidated
    {
        get
        {
            if (!_validatedProviderKeys.TryGetValue(SelectedProvider, out var validated))
                return false;

            if (validated.Length == 0)
                return false;

            var typed = ApiKeyInput.Trim();
            return typed.Length == 0
                ? _providerKeys.CurrentKey(SelectedProvider) == validated
                : typed == validated;
        }
    }

    /// <summary>
    /// Move to the next step if the gate is open. Returns false when it is not, and
    /// on a flow that has already finished.
    /// </summary>
    /// <remarks>
    /// The liveness guard is the same one Complete(), DeferSetup() and AbandonSetup()
    /// carry, applied to the two methods that were missing it. Stepping INTO Try It
    /// calls ApplyStagedSourceReversibly(), which writes the default Mode - so an
    /// Advance() after the flow finished silently undid the rollback that Set Up
    /// Later had just performed, on a flow whose MarkOnboardingCompleted() had
    /// already fired. Every exit currently Close()s the window synchronously, so
    /// nothing reaches this today; the invariant is that a dead flow writes nothing,
    /// and it belongs on every entry point rather than on three of five.
    /// </remarks>
    public bool Advance()
    {
        if (!_isLive || !CanContinue)
            return false;

        var next = (int)Step + 1;
        if (next > (int)OnboardingSteps.Last)
            return false;

        if (!ApplyStagedSourceIfEntering((OnboardingStep)next))
            return false;

        StepWillLeave(Step);
        Step = (OnboardingStep)next;
        StepDidChange();
        return true;
    }

    /// <summary>
    /// Move to the previous step. Returns false at the first step, and on a flow that
    /// has already finished.
    /// </summary>
    public bool Back()
    {
        if (!_isLive)
            return false;

        var previous = (int)Step - 1;
        if (previous < (int)OnboardingSteps.First)
            return false;

        if (!ApplyStagedSourceIfEntering((OnboardingStep)previous))
            return false;

        StepWillLeave(Step);
        Step = (OnboardingStep)previous;
        StepDidChange();
        return true;
    }

    /// <summary>
    /// The Try It step records through the source the user just set up, so it is
    /// the one place production state is written before completion. The write runs
    /// BEFORE the step changes, not from StepDidChange, because a failure has to
    /// leave the user on the step they are still looking at: entering Try It first
    /// and then discovering the Mode was never written gives a page whose only
    /// control cannot work, with no way to say why.
    ///
    /// Both directions, because Done -> Back re-enters Try It.
    /// </summary>
    private bool ApplyStagedSourceIfEntering(OnboardingStep step) =>
        step != OnboardingStep.TryIt || ApplyStagedSourceReversibly();

    /// <summary>
    /// The step-exit hooks. macOS runs these from each step view's .onDisappear; a
    /// WPF Frame gives no equivalent guarantee, so the machine owns them.
    /// </summary>
    private void StepWillLeave(OnboardingStep step)
    {
        switch (step)
        {
            // Leaving the step that started a credential check ends it. The result
            // would be about a screen the user is no longer on, and leaving it
            // running is what let the two spinners strand: IsTestingKey until the
            // next Configure entry cleared it, IsActivatingLicense for good.
            case OnboardingStep.Configure:
            case OnboardingStep.Setup:
                CancelCredentialValidation();
                break;

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
                // The staged source is already in production state: the write is a
                // PRECONDITION of arriving here (see ApplyStagedSourceIfEntering),
                // so this step is never entered over a Mode that was not written.
                // It is fully reversible: DeferSetup() restores the captured point.
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
        if (!_isLive)
            return;

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
        if (!_isLive)
            return;

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
        if (!_isLive)
            return;

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
        if (!_isLive)
            return;

        RunTracked(OnboardingTaskKeys.MicrophonePermission, RequestMicrophoneAccessCoreAsync);
    }

    private async Task RequestMicrophoneAccessCoreAsync(CancellationToken cancellationToken)
    {
        bool granted;
        try
        {
            granted = await _permissions.RequestMicrophoneAccessAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A throwing consent probe is a refusal, not a crash. Same rule as the
            // two credential checks: no seam may leave the step in a state the user
            // cannot get out of.
            granted = false;
        }

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
    /// Store a shortcut the user recorded inline on the Permissions step, then
    /// re-check. The row never gates Continue, so this is an offer, not a
    /// requirement - but it is now an offer that works: it used to deep-link the
    /// Shortcuts settings section, and this window is application modal, so the page
    /// it raised could be looked at and not typed into.
    ///
    /// The argument is the persisted string rather than a WPF key, so this file and
    /// its whole suite stay WPF-free. See the seam for why this one write is
    /// deliberately not rolled back by "Set Up Later".
    /// </summary>
    /// <returns>false if the seam refused it; the recorder has already validated it.</returns>
    public bool ApplyToggleShortcut(string persistedShortcut)
    {
        if (!_isLive || string.IsNullOrWhiteSpace(persistedShortcut))
            return false;

        var stored = _permissions.SetToggleShortcut(persistedShortcut);

        // Refresh either way. A refused write still has to leave the row showing
        // what is actually configured rather than what the user just typed.
        RefreshShortcutRegistration();
        return stored;
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
        if (!_isLive || SelectedSource == source)
            return;

        // The card is not on the Source step when this is false (see
        // SourceOptions), so this is the same rule stated where it is enforceable:
        // a machine with no local engine cannot stage the on-device branch, whether
        // the ask comes from the UI or from a test.
        if (source == OnboardingSourceKind.OnDevice && !IsOnDeviceAvailable)
            return;

        SelectedSource = source;

        // The thing being validated has just changed underneath any check that is
        // still in flight. Stop it and put the spinners back; the scope check in
        // each continuation is what makes a result that still lands harmless.
        CancelCredentialValidation();

        ClearValidationPass();
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
        if (!_isLive || SelectedModel == model)
            return;

        SelectedModel = model;
        RefreshSetupError();
    }

    [RelayCommand]
    public void SelectProvider(CloudTranscriptionProvider provider)
    {
        if (!_isLive || SelectedProvider == provider)
            return;

        SelectedProvider = provider;

        // Same rule as a source change: a probe in flight was about the OLD
        // provider, so stop it rather than let it land under the new one.
        CancelCredentialValidation();

        // A masked key typed for one provider must never be saved under another.
        ApiKeyInput = string.Empty;
        InvalidateProviderValidation();
    }

    // KeyValidated is derived from the scope comparison, so editing either field or
    // changing provider closes it with no help from these two. They exist to clear
    // the inline RESULT surface (the tick, the health pill, the error line), which is
    // display state and genuinely stored.
    private void InvalidateLicenseValidation()
    {
        LicenseTestPassed = null;
        _activationErrorMessage = null;
        RefreshSetupError();
    }

    private void InvalidateProviderValidation()
    {
        ProviderTestHealth = null;
        _providerErrorMessage = null;
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
        if (!_isLive)
            return;

        IsTestingKey = false;
        LicenseTestPassed = null;
        ProviderTestHealth = null;
        _activationErrorMessage = null;
        _providerErrorMessage = null;
        ClearValidationPass();
        RefreshSetupError();
    }

    // =========================================================================
    // VALIDATION
    // =========================================================================

    /// <summary>
    /// What a credential check was ABOUT: the source branch it belongs to, the
    /// provider it named, and the exact trimmed credential it tested.
    ///
    /// A single "the key validated" bool was read by BOTH branches of
    /// <see cref="ConfigureGateIsOpen"/>, so a licence probe that landed after the
    /// user changed source opened the BYOK gate with an empty API-key field (and
    /// the mirror direction did the same). Each continuation checked one half of
    /// its own identity - the licence path re-read the licence text, the provider
    /// path re-read the provider and the key - and neither re-read the source.
    ///
    /// Scoping the recorded fact removes the whole class: a pass is only ever a
    /// pass FOR a scope, and <see cref="KeyValidated"/> compares it against what is
    /// on screen now rather than trusting that nothing moved.
    /// </summary>
    private readonly record struct ValidationScope(
        OnboardingSourceKind Source,
        CloudTranscriptionProvider Provider,
        string Credential);

    /// <summary>The scope whose inline check last passed, or null.</summary>
    private ValidationScope? _passedValidation;

    /// <summary>
    /// What an inline check started NOW would be about, or null on a source that
    /// has no credential (on-device, or nothing selected yet).
    /// </summary>
    private ValidationScope? CurrentValidationScope => SelectedSource switch
    {
        OnboardingSourceKind.HyperWhisperCloud => new ValidationScope(
            OnboardingSourceKind.HyperWhisperCloud,
            // The licence is not a per-provider credential; pin the provider field
            // so a provider change can never invalidate a licence pass.
            CloudTranscriptionProvider.OpenAI,
            LicenseKeyInput.Trim()),

        OnboardingSourceKind.YourProvider => new ValidationScope(
            OnboardingSourceKind.YourProvider,
            SelectedProvider,
            ApiKeyInput.Trim()),

        _ => null
    };

    /// <summary>
    /// True only while the inline test has a passing result for the CURRENT source,
    /// provider and credential. Derived, not stored: an edit, a provider change or a
    /// source change closes it by making the scopes differ, and a late continuation
    /// can only ever record a pass against the scope it actually tested.
    /// </summary>
    public bool KeyValidated =>
        _passedValidation is { } passed
        && CurrentValidationScope is { } current
        && passed == current;

    /// <summary>
    /// Record the outcome of a check that was about <paramref name="scope"/>. A pass
    /// is remembered as belonging to that scope; a failure only forgets a pass that
    /// was about the same scope, so a licence failure cannot erase a BYOK pass.
    /// </summary>
    private void RecordValidationOutcome(ValidationScope scope, bool passed)
    {
        if (passed)
            _passedValidation = scope;
        else if (_passedValidation == scope)
            _passedValidation = null;

        RaiseGateChanged();
    }

    /// <summary>Forget any inline pass, whatever it was about.</summary>
    private void ClearValidationPass()
    {
        if (_passedValidation is null)
            return;

        _passedValidation = null;
        RaiseGateChanged();
    }

    /// <summary>
    /// Cancel any credential check that is still in flight and put the two spinners
    /// back. Called whenever the thing being validated changes underneath the check -
    /// a source change, a provider change, or leaving the step that started it - so a
    /// result can never arrive describing something the user has moved on from.
    ///
    /// The scope check in each continuation makes this belt AND braces: cancellation
    /// stops the wasted work and the stale spinner, the scope check is what makes a
    /// result that still lands harmless.
    /// </summary>
    private void CancelCredentialValidation()
    {
        _taskBox.Cancel(OnboardingTaskKeys.LicenseTest);
        _taskBox.Cancel(OnboardingTaskKeys.ProviderTest);
        _taskBox.Cancel(OnboardingTaskKeys.Activation);
        IsTestingKey = false;
        IsActivatingLicense = false;
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
        if (!_isLive)
            return;

        var key = LicenseKeyInput.Trim();
        if (key.Length == 0)
            return;

        // What this check is ABOUT, captured before the await. Everything the
        // continuation writes is guarded on this still describing the screen.
        var scope = new ValidationScope(
            OnboardingSourceKind.HyperWhisperCloud, CloudTranscriptionProvider.OpenAI, key);

        IsTestingKey = true;
        LicenseTestPassed = null;
        _activationErrorMessage = null;
        RunTracked(OnboardingTaskKeys.LicenseTest, ct => TestAccessKeyCoreAsync(scope, key, ct));
    }

    private async Task TestAccessKeyCoreAsync(
        ValidationScope scope,
        string key,
        CancellationToken cancellationToken)
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
        catch (Exception ex)
        {
            // Anything the gateway lets escape - an HttpRequestException, a JSON
            // fault - has to leave the step usable. The spinner is cleared on the
            // same terms as a landed result, so the button can be pressed again.
            if (cancellationToken.IsCancellationRequested || !_isLive || CurrentValidationScope != scope)
                return;

            LicenseTestPassed = false;
            _activationErrorMessage = ex.Message;
            RecordValidationOutcome(scope, false);
            IsTestingKey = false;
            RefreshSetupError();
            _taskBox.Clear(OnboardingTaskKeys.LicenseTest);
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        // Drop a result that no longer describes what is on screen: the licence text
        // was edited, OR the user switched to another source. The second half is the
        // one that used to be missing, and it let this result open the BYOK gate.
        if (CurrentValidationScope != scope)
        {
            IsTestingKey = false;
            _taskBox.Clear(OnboardingTaskKeys.LicenseTest);
            return;
        }

        LicenseTestPassed = outcome.IsValid;
        _activationErrorMessage = outcome.IsValid ? null : outcome.ErrorMessage;
        RecordValidationOutcome(scope, outcome.IsValid);

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
    public void TestProviderKey() => ProbeAndPersistProviderKey();

    /// <summary>
    /// The one probe-then-persist path. Both the Configure step's "Test API key" and
    /// the Setup step's "Save API key" come here: a credential is only ever written
    /// after a passing probe, and a write is only ever recorded as a pass.
    /// </summary>
    private void ProbeAndPersistProviderKey()
    {
        if (!_isLive)
            return;

        var key = ApiKeyInput.Trim();
        if (key.Length == 0 || IsTestingKey)
            return;

        var provider = SelectedProvider;
        var scope = new ValidationScope(OnboardingSourceKind.YourProvider, provider, key);

        IsTestingKey = true;
        ProviderTestHealth = null;
        _providerErrorMessage = null;
        RunTracked(OnboardingTaskKeys.ProviderTest, ct => TestProviderKeyCoreAsync(scope, provider, key, ct));
    }

    private async Task TestProviderKeyCoreAsync(
        ValidationScope scope,
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
        catch (Exception ex)
        {
            // A throwing health seam must not strand the spinner, and must never
            // reach the persist below with no answer.
            if (cancellationToken.IsCancellationRequested || !_isLive || CurrentValidationScope != scope)
                return;

            ProviderTestHealth = null;
            _providerErrorMessage = ex.Message;
            RecordValidationOutcome(scope, false);
            IsTestingKey = false;
            RefreshSetupError();
            _taskBox.Clear(OnboardingTaskKeys.ProviderTest);
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        // Drop a result the user has since superseded BEFORE the persist: a stale
        // probe must never write the credential store or set a restore point (which
        // would also wrongly flag a pending production write). The scope covers the
        // provider, the key AND the source - the last of which used to be missing,
        // and let this result open the HyperWhisper Cloud gate.
        if (CurrentValidationScope != scope)
        {
            IsTestingKey = false;
            _taskBox.Clear(OnboardingTaskKeys.ProviderTest);
            return;
        }

        // ACCEPTED, which is not the same as Healthy.
        //
        // A vendor with no content-free validation endpoint answers Unknown for
        // every key, valid or not - see CloudTranscriptionProviderExtensions
        // .SupportsKeyHealthProbe, which is where CloudProviderHealthService's
        // unconditional Unknown comes from. Meta MuseSTT is the only one today, and
        // it is on the chip strip: waiting for Healthy there meant the key was
        // never written, Continue was disabled for good, and nothing on screen
        // changed at all, because every pill needs an exact enum match. So Unknown
        // from a vendor that can only ever answer Unknown is a pass - a CONFIGURED
        // key, said in those words on its own pill, not a validated one.
        var unverifiable = !provider.SupportsKeyHealthProbe();
        var accepted = health == ProviderHealth.Healthy
            || (unverifiable && health == ProviderHealth.Unknown);

        var persisted = false;
        if (accepted)
        {
            // Snapshot whatever this provider had BEFORE overwriting it, so Set Up
            // Later can put the user's original key back (bug 1).
            CaptureProviderKeyRestorePoint(provider);
            persisted = _providerKeys.Persist(key, provider);
        }

        if (accepted && !persisted)
        {
            ProviderTestHealth = null;
            _providerErrorMessage = _providerKeys.ValidationError
                ?? Loc.S("onboarding.setup.provider.saveFailed");
            RecordValidationOutcome(scope, false);
            // A key that failed its write must not stay remembered as validated,
            // exactly as a revoked licence key does not (see TestAccessKeyCoreAsync).
            _validatedProviderKeys.Remove(provider);
        }
        else
        {
            ProviderTestHealth = health;
            // A rejected or unreachable provider now gets a REASON on the single
            // error funnel as well as the health pill. The Configure step renders
            // the pill and is unchanged (ShowsProviderTestError needs a null
            // health); the Setup step has only the funnel, so without this its
            // "Save API key" button failed in silence.
            _providerErrorMessage = accepted
                ? null
                : health switch
                {
                    ProviderHealth.Unauthorized => Loc.S("onboarding.configure.test.unauthorized"),
                    _ => Loc.S("onboarding.configure.test.unreachable")
                };

            var passed = accepted && persisted;
            RecordValidationOutcome(scope, passed);
            if (passed)
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
        if (!_isLive || SelectedModel is not { } model)
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
        if (!_isLive)
            return;

        var key = LicenseKeyInput.Trim();
        if (key.Length == 0 || IsActivatingLicense)
            return;

        var scope = new ValidationScope(
            OnboardingSourceKind.HyperWhisperCloud, CloudTranscriptionProvider.OpenAI, key);

        IsActivatingLicense = true;
        _activationErrorMessage = null;
        RunTracked(OnboardingTaskKeys.Activation, ct => ActivateCloudLicenseCoreAsync(scope, key, ct));
    }

    private async Task ActivateCloudLicenseCoreAsync(
        ValidationScope scope,
        string key,
        CancellationToken cancellationToken)
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
        catch (Exception ex)
        {
            // The sibling spinner, IsTestingKey, was already recoverable because
            // every entry to the Configure step clears it. This one was not: a
            // throwing ActivateAsync left the button reading "Activating…" and
            // disabled for good, with Continue gated on an activation that could
            // never be retried. Both are now cleared on the same terms.
            if (cancellationToken.IsCancellationRequested || !_isLive || CurrentValidationScope != scope)
                return;

            IsActivatingLicense = false;
            _activationErrorMessage = ex.Message;
            RefreshSetupError();
            _taskBox.Clear(OnboardingTaskKeys.Activation);
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        // KEY-A's "Activation limit reached." must not be shown under KEY-B. The
        // licence TEST continuation has always dropped a superseded result; this one
        // did not, so the error line was misattributed to whatever the field held
        // when it landed. Note the activation itself is NOT undone - it reached the
        // server for KEY-A and _license.IsActive is the honest record of that.
        if (CurrentValidationScope != scope)
        {
            IsActivatingLicense = false;
            _taskBox.Clear(OnboardingTaskKeys.Activation);
            return;
        }

        IsActivatingLicense = false;
        _activationErrorMessage = outcome.IsValid ? null : outcome.ErrorMessage;
        if (outcome.IsValid)
            RecordValidationOutcome(scope, true);

        RefreshSetupError();
        _taskBox.Clear(OnboardingTaskKeys.Activation);
    }

    /// <summary>
    /// The Setup step's "Save API key".
    ///
    /// It used to capture a restore point and persist WITHOUT probing, and recorded
    /// nothing in the per-session validation table - so it overwrote the user's real
    /// Credential Manager entry with an unverified key and still could not make
    /// <see cref="IsSelectedSourceUsable"/> true. The button renders exactly when
    /// that property is false, so pressing it was a dead end that cost the user their
    /// stored credential.
    ///
    /// It is now the same probe-then-persist action as "Test API key": the write only
    /// happens once the provider has accepted the key, and when it happens it opens
    /// the gate. A rejected or unreachable provider leaves the stored key untouched
    /// and puts the reason on the step's error line.
    /// </summary>
    [RelayCommand]
    public void SaveProviderKey() => ProbeAndPersistProviderKey();

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
        if (!_isLive)
            return;

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
        if (!_isLive)
            return;

        TryItMode = DeviceAvailability != OnboardingDeviceAvailability.Available && _audio.HasSampleClip
            ? OnboardingTryItMode.Sample
            : OnboardingTryItMode.Record;
        TranscriptCameFromSample = false;
        _audio.ClearTranscript();
    }

    public void EndTryItStep()
    {
        // Cancel BOTH owned transcriptions before tearing the recorder down, so a
        // running orchestrator call is not left billing against a disposed gateway.
        //
        // The sample clip is a SEPARATE task-box key from the microphone
        // recording, and cancelling only the microphone one let a sample
        // transcription started here survive Back: it kept running with no
        // chrome, and because walking forward into Try It again resets
        // TranscriptCameFromSample to false, its result then rendered as the
        // user's own recording, complete with the device name and the "recorded"
        // pill. The defer and complete paths were already safe because Finish()
        // calls CancelAll(); only Back leaked.
        _taskBox.Cancel(OnboardingTaskKeys.TestRecording);
        _taskBox.Cancel(OnboardingTaskKeys.SampleClip);
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
        if (!_isLive || IsTranscribingTestRecording || IsTranscribingSample)
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
        if (!_isLive || !_audio.HasSampleClip || IsTranscribingSample || IsTranscribingTestRecording)
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

    /// <summary>
    /// True when the last rollback could not put the pre-onboarding default Mode
    /// back. The restore point is retained in that case, so
    /// <see cref="HasPendingProductionWrite"/> stays true and the window reports
    /// it beside any lost credential.
    /// </summary>
    public bool ModeRestoreFailed => _modeRestoreFailed;

    /// <summary>
    /// True when the last attempt to write the staged source into production state
    /// failed. The mirror of <see cref="ModeRestoreFailed"/>, on the apply side:
    /// the restore point is kept, the step does not change, the flow does not
    /// close, and the window reports it through the same path.
    /// </summary>
    public bool SourceApplyFailed => _sourceApplyFailed;

    private bool _sourceApplyFailed;

    /// <summary>
    /// Write the staged source into production state, reversibly.
    /// </summary>
    /// <returns>
    /// True when the write went in, and on a flow with nothing staged (there is
    /// then nothing that can fail). False when the Modes database refused.
    ///
    /// The Restore mirror at LiveOnboardingSourceCommitter.Restore has been fully
    /// wrapped since it was written; this was not, and ModeService.SaveMode
    /// RETHROWS DbUpdateException. The path from the footer button is
    /// PrimaryButton_Click -> Advance/Complete -> ApplyStagedSourceReversibly with
    /// no try/catch anywhere on it, so a locked SQLite file (the Local API's own
    /// DbContext, an antivirus handle, a full disk) surfaced as App.xaml.cs's raw
    /// unhandled-exception box on top of the first-run window - and left the flow
    /// on an unarmed Try It page, or with the window never closing at all.
    /// </returns>
    private bool ApplyStagedSourceReversibly()
    {
        _sourceApplyFailed = false;

        if (StagedSource is not { } staged)
            return true;

        // Captured BEFORE the write, and kept if the write fails: a throw can
        // still leave a half-applied Mode behind, so the snapshot is the only way
        // back and HasPendingProductionWrite must stay true over it.
        if (_restorePoint is null)
        {
            _restorePoint = _committer.CaptureRestorePoint();
            RaiseGateChanged();
        }

        try
        {
            _committer.Apply(staged);
            return true;
        }
        catch (Exception ex)
        {
            _sourceApplyFailed = true;
            HyperWhisper.Services.LoggingService.Error(
                "OnboardingFlowViewModel: could not write the staged source into production state; "
                + $"the restore point is kept so the change is still reversible: {ex.Message}",
                ex);
            RaiseGateChanged();
            return false;
        }
    }

    /// <summary>
    /// Explicit completion. The staged configuration becomes production state and
    /// there is nothing left to roll back.
    /// </summary>
    /// <returns>
    /// True when the flow closed. False when the final write refused, in which case
    /// NOTHING has happened: first run is not marked complete, the restore point is
    /// kept, and the window stays open so the user can retry or defer.
    /// <see cref="SourceApplyFailed"/> says why.
    ///
    /// No longer a [RelayCommand]: the generator only accepts void and Task, and
    /// nothing bound to CompleteCommand - the footer button is a Click handler in
    /// OnboardingWindow, which is the caller that needs the answer.
    /// </returns>
    public bool Complete()
    {
        if (!_isLive)
            return false;

        // Discarding the restore points below is what makes this irreversible, so
        // it may only happen over a write that actually landed.
        if (!ApplyStagedSourceReversibly())
            return false;

        _restorePoint = null;
        _providerKeyRestorePoints.Clear();
        _didCaptureDevice = false;
        _previousDeviceId = null;
        _previousOpenDeviceId = null;
        Finish(markCompleted: true);
        return true;
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
        // The Mode path now matches the credential path below: the restore point
        // is discarded only when the write actually went back.
        //
        // Restore() swallows database failures; discarding the snapshot
        // regardless turned "your default Mode is still the one onboarding
        // staged" into a clean deferral, with the pre-onboarding row gone from
        // memory and no way to retry. A transient EF failure while deferring
        // after the Try It step is enough.
        _modeRestoreFailed = false;
        if (_restorePoint is { } point)
        {
            if (_committer.Restore(point))
            {
                _restorePoint = null;
            }
            else
            {
                _modeRestoreFailed = true;
                HyperWhisper.Services.LoggingService.Error(
                    "OnboardingFlowViewModel: could not restore the pre-onboarding default Mode; "
                    + "the restore point is kept so the change is still reversible");
            }
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
        return _unrestoredProviderKeys.Count == 0 && !_modeRestoreFailed;
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

        // The list itself only belongs to the microphone step, exactly as on macOS.
        if (Step == OnboardingStep.Microphone)
            ApplyDeviceList(_audio.Devices);

        // Reconcile the meter with reality, in BOTH directions. Unplugging the
        // microphone mid-step used to leave the flag true and the bars frozen at
        // their last heights; plugging one in left them dead under a prompt that
        // had already gone back to asking for speech. After the list, so a
        // recovery meters the device the step is now showing.
        SyncLevelMeter();
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
        // KeyValidated is derived from the recorded scope AND the live one, so every
        // input that can move either - both key fields, the provider, the source -
        // has to re-raise it. All of them already come through here.
        OnPropertyChanged(nameof(KeyValidated));
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
