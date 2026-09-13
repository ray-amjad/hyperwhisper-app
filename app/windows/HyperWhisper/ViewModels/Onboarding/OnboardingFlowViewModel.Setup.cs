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

        // StepDidChange fetches the balance on entry to Configure and to Setup, and
        // both of those run BEFORE this activation. On a first-run machine the licence
        // only goes active right here, so every earlier fetch happened unlicensed and
        // came back unknown - "Credits confirmed" stayed unticked and the Done summary
        // fell back to a bare source name, on a perfectly good key. Nothing else
        // refreshed it, so this is the fetch that fills it in.
        //
        // force: true because the cloud manager caches, and the cached miss was taken
        // while the machine was still unlicensed.
        if (outcome.IsValid)
            RefreshCredits(force: true);
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

}
