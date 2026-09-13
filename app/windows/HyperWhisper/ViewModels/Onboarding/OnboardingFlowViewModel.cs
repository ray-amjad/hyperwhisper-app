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

using System.Globalization;
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
                    return CloudKeyIsVerified;

                case OnboardingSourceKind.YourProvider:
                    return ProviderKeyIsVerified;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Has this Cloud access key been shown to work — a working key, not merely a
    /// typed one. Either the licence is already active on this PC, the inline test
    /// passed, or the field still holds the exact key that passed earlier this
    /// session.
    ///
    /// <see cref="KeyValidated"/> is SCOPED (see ValidationScope) and is cleared on
    /// every entry to the Configure step by <see cref="ResetConfigureTestResults"/>,
    /// so on its own it says "an inline test is passing RIGHT NOW", not "this key has
    /// verified". The per-session <see cref="_lastValidatedLicenseKey"/> is what
    /// survives Back navigation, and an active licence is proof on its own: the server
    /// accepted this key on this device.
    ///
    /// The Configure gate has always read exactly this. The "Access key verified" row
    /// on the Setup step read the bare <see cref="KeyValidated"/> instead, so a single
    /// Back-and-forward unticked it while the two rows below — activation and credits —
    /// stayed ticked, and the card claimed the key was unverified on a device whose
    /// account that same key had already activated.
    /// </summary>
    public bool CloudKeyIsVerified
    {
        get
        {
            if (SelectedSource != OnboardingSourceKind.HyperWhisperCloud)
                return false;

            var key = LicenseKeyInput.Trim();
            return _license.IsActive
                || KeyValidated
                || (key.Length > 0 && key == _lastValidatedLicenseKey);
        }
    }

    /// <summary>
    /// The BYOK half of the same question. <see cref="KeyValidated"/> is cleared on
    /// every entry to the Configure step, so the per-session record is what carries a
    /// pass across Back navigation. A key that merely sits in the credential store but
    /// was never probed this session does not count, and neither does a key that
    /// passed and has since been edited.
    ///
    /// The BYOK "API key verified" row on the Setup step had the same defect as the
    /// Cloud one for the same reason, so it reads this rather than a second rule.
    /// </summary>
    public bool ProviderKeyIsVerified
    {
        get
        {
            if (SelectedSource != OnboardingSourceKind.YourProvider)
                return false;

            return KeyValidated || SelectedProviderKeyIsValidated;
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

}
