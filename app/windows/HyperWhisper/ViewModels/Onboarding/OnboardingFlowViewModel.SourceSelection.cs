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

}
