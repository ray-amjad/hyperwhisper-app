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

        // StepDidChange fetches the balance on ENTRY to this step, which on a first-run machine
        // happens while the app is still unlicensed, so that fetch asks about the device id and
        // comes back "Invalid license key". Nothing re-fetched after the probe, so the big number
        // above "credits available" stayed on its "…" placeholder for good on a perfectly valid
        // key. The probe does not STORE the key -- only activation does -- so the key is passed
        // explicitly; without it this fetch would ask about the device id again and fail again.
        if (outcome.IsValid)
            RefreshCredits(force: true, licenseKeyOverride: key);
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

}
