using HyperWhisper.ModelManagement;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed partial class ModesViewModel
{
    // =====================================================================================
    // MODE EDITOR — segmented sources, conditional reveals and live Save validation.
    //
    // Windows' ModeEditorWindow.xaml.cs drives ~94 Visibility switches from code-behind. The
    // same rules live here as computed properties so the Avalonia dialog can bind IsVisible
    // straight to them. Each rule cites the Windows line it mirrors.
    // =====================================================================================

    // ---- Transcription source segments -------------------------------------------------
    // Windows carries three radios in one group (ModeEditorWindow.xaml:81-95). "On-device" is
    // the legacy "local" ProviderType; both cloud segments map to ProviderType "cloud", with
    // CloudProvider distinguishing HyperWhisper Cloud from a BYOK vendor.
    // A false assignment is ignored: the RadioButton group already unchecks the old segment,
    // and acting on it would fight the setter that is turning the new one on.
    public bool IsOnDeviceSource
    {
        get => !string.Equals(_providerType, "cloud", StringComparison.Ordinal);
        set { if (value) ProviderType = "local"; }
    }
    public bool IsHwCloudSource
    {
        get => string.Equals(_providerType, "cloud", StringComparison.Ordinal)
            && string.Equals(_cloudProvider, "hyperwhisper", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;
            CloudProvider = "hyperwhisper";
            ProviderType = "cloud";
        }
    }
    public bool IsYourProviderSource
    {
        get => string.Equals(_providerType, "cloud", StringComparison.Ordinal)
            && !string.Equals(_cloudProvider, "hyperwhisper", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;
            // Windows selects the first BYOK vendor when the combo has no selection, because
            // HyperWhisper is removed from that list (ModeEditorWindow.xaml.cs:1184-1185), and then
            // refills the model combo with no preferred id (:1186) — which is the ProviderType
            // setter's NormalizeCloudModel call below.
            if (string.Equals(_cloudProvider, "hyperwhisper", StringComparison.OrdinalIgnoreCase))
                CloudProvider = "openai";
            ProviderType = "cloud";
        }
    }

    /// <summary>Local Model picker. Windows: LocalModelPanel, visible for "ondevice" (:1225).</summary>
    public bool ShowLocalModelPanel => IsOnDeviceSource;
    /// <summary>BYOK vendor combo. Windows: CloudProviderPanel, visible for "yourprovider" (:1226).</summary>
    public bool ShowCloudProviderPanel => IsYourProviderSource;
    /// <summary>Engine (accuracy tier) + tier model. Windows: CloudAccuracyPanel (:656, :1236).</summary>
    public bool ShowCloudAccuracyPanel => IsHwCloudSource;
    /// <summary>Windows shows the medical toggle for AssemblyAI only (:889).</summary>
    public bool ShowMedicalDomain => IsHwCloudSource
        && string.Equals(_cloudAccuracyTier, "assemblyAI", StringComparison.OrdinalIgnoreCase) && !IsDictation;
    /// <summary>BYOK model box. Windows: CloudModelPanel (:697, :1293).</summary>
    public bool ShowCloudModelPanel => IsYourProviderSource;
    /// <summary>Windows reveals the Gemini prompt only for BYOK Gemini (:1326).</summary>
    public bool ShowGeminiPrompt => IsYourProviderSource
        && (string.Equals(_cloudProvider, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_cloudProvider, "geminitranscribe", StringComparison.OrdinalIgnoreCase));
    /// <summary>
    /// Windows' Nova3Warning: vocabulary boosting is dropped when a Nova model runs on
    /// auto-detect (mode.editor.warning.nova3AutoDetect).
    /// </summary>
    public bool ShowNova3Warning => IsYourProviderSource
        && string.Equals(_cloudProvider, "deepgram", StringComparison.OrdinalIgnoreCase)
        && _transcriptionModel.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase)
        && (_language.Length == 0 || string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase));
    /// <summary>
    /// Windows' ParakeetLanguageWarning: the Parakeet engines are English-only in this build,
    /// so a non-English mode on Parakeet loses its language (:1605).
    /// </summary>
    public bool ShowParakeetLanguageWarning => IsOnDeviceSource
        && string.Equals(_localEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
        && _language.Length > 0
        && !string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase);

    // ---- Post-processing ---------------------------------------------------------------
    /// <summary>Windows' PostProcessingCheck; reveals PostProcessingSettingsPanel (:1836).</summary>
    public bool PostProcessingEnabled
    {
        get => !string.Equals(_postProcessingMode, "off", StringComparison.Ordinal);
        set
        {
            if (value == PostProcessingEnabled) return;
            // Windows' create default is cloud post-processing on HyperWhisper Cloud
            // (ModeEditorWindow.xaml.cs:53-55), which is what enabling the checkbox lands on.
            PostProcessingMode = value ? "cloud" : "off";
            if (value && string.Equals(_postProcessingProvider, "openai", StringComparison.Ordinal))
                PostProcessingProvider = "hyperwhispercloud";
        }
    }
    public bool IsPpOnDeviceSource
    {
        get => string.Equals(_postProcessingMode, "local", StringComparison.Ordinal);
        set { if (value) PostProcessingMode = "local"; }
    }
    public bool IsPpHwCloudSource
    {
        get => string.Equals(_postProcessingMode, "cloud", StringComparison.Ordinal)
            && string.Equals(_postProcessingProvider, "hyperwhispercloud", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;
            PostProcessingProvider = "hyperwhispercloud";
            PostProcessingMode = "cloud";
        }
    }
    public bool IsPpYourProviderSource
    {
        get => string.Equals(_postProcessingMode, "cloud", StringComparison.Ordinal)
            && !string.Equals(_postProcessingProvider, "hyperwhispercloud", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value) return;
            if (string.Equals(_postProcessingProvider, "hyperwhispercloud", StringComparison.OrdinalIgnoreCase))
                PostProcessingProvider = "openai";
            PostProcessingMode = "cloud";
        }
    }
    /// <summary>BYOK LLM vendor combo. Windows: PostProcessingProviderPanel (:1388).</summary>
    public bool ShowPostProcessingProviderPanel => PostProcessingEnabled && IsPpYourProviderSource;
    /// <summary>HyperWhisper Cloud engine + model pair. Windows: CloudPostProcessingModelPanel (:1381).</summary>
    public bool ShowCloudPostProcessingPanel => PostProcessingEnabled && IsPpHwCloudSource;
    /// <summary>The BYOK model box; Windows hides its model row for HW Cloud (:1380/:1390).</summary>
    public bool ShowPostProcessingModelPanel => PostProcessingEnabled && IsPpYourProviderSource;
    /// <summary>The local GGUF filename, which is Windows' model row under the on-device source.</summary>
    public bool ShowLocalPostProcessingModel => PostProcessingEnabled && IsPpOnDeviceSource;
    /// <summary>Linux-only: the inline custom OpenAI-compatible endpoint fields.</summary>
    public bool ShowCustomEndpointPanel => ShowPostProcessingProviderPanel
        && string.Equals(_postProcessingProvider, "custom", StringComparison.OrdinalIgnoreCase);
    /// <summary>Windows shows the spelling variant for English or auto-detect only (:1708).</summary>
    public bool ShowEnglishSpelling => PostProcessingEnabled
        && (_language.Length == 0
            || string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase));
    /// <summary>Windows reveals Custom Instructions only for the Custom preset (:1644).</summary>
    public bool ShowCustomInstructions => PostProcessingEnabled
        && string.Equals(_preset, "custom", StringComparison.OrdinalIgnoreCase);
    /// <summary>Windows' UserPromptCheck → UserPromptPanel (:1745).</summary>
    public bool UserPromptEnabled
    {
        get => _userPromptEnabled;
        set { if (Set(ref _userPromptEnabled, value)) Notify(nameof(ShowUserPrompt)); }
    }
    public bool ShowUserPrompt => PostProcessingEnabled && _userPromptEnabled;
    /// <summary>Windows hides both punctuation checkboxes while post-processing is off (:1354-1355).</summary>
    public bool ShowPunctuationOptions => PostProcessingEnabled;
    /// <summary>Windows reveals "Remove trailing period" only under a ticked Add punctuation (:1728).</summary>
    public bool ShowRemoveTrailingPeriod => PostProcessingEnabled && _punctuation;

    /// <summary>
    /// Windows disables Save live while the name is blank or the chosen on-device / local-LLM
    /// model cannot be used (UpdateSaveButtonState, :318). The model rule here is the same
    /// catalog check <see cref="SaveAsync"/> already enforces, so Save can no longer be pressed
    /// into a failure the dialog could have shown up front.
    /// </summary>
    public bool CanSave
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_name)) return false;
            if (IsOnDeviceSource)
            {
                var kind = string.Equals(_localEngine, "parakeet", StringComparison.Ordinal)
                    ? ManagedModelKind.Parakeet : ManagedModelKind.Whisper;
                if (!PortableModelCatalog.All.Any(model => model.Kind == kind
                    && string.Equals(model.Id, _transcriptionModel, StringComparison.Ordinal))) return false;
            }
            if (IsPpOnDeviceSource
                && (string.IsNullOrWhiteSpace(_localPostProcessingModel)
                    || !_localPostProcessingModel.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
    }

    private void NotifyEditorReveals()
    {
        Notify(nameof(IsOnDeviceSource)); Notify(nameof(IsHwCloudSource)); Notify(nameof(IsYourProviderSource));
        Notify(nameof(ShowLocalModelPanel)); Notify(nameof(LocalModels)); Notify(nameof(CloudModels));
        Notify(nameof(LocalTranscriptionModel)); Notify(nameof(CloudTranscriptionModel));
        Notify(nameof(ShowCloudProviderPanel));
        Notify(nameof(ShowCloudAccuracyPanel)); Notify(nameof(ShowMedicalDomain));
        Notify(nameof(CloudVendor)); Notify(nameof(CloudTierModels));
        Notify(nameof(CloudTierModel)); Notify(nameof(IsDictation)); Notify(nameof(IsNotDictation));
        Notify(nameof(DictationLanguage));
        Notify(nameof(ShowCloudModelPanel)); Notify(nameof(ShowGeminiPrompt));
        Notify(nameof(ShowNova3Warning)); Notify(nameof(ShowParakeetLanguageWarning));
        Notify(nameof(PostProcessingEnabled));
        Notify(nameof(IsPpOnDeviceSource)); Notify(nameof(IsPpHwCloudSource)); Notify(nameof(IsPpYourProviderSource));
        Notify(nameof(ShowPostProcessingProviderPanel)); Notify(nameof(ShowCloudPostProcessingPanel));
        Notify(nameof(ShowPostProcessingModelPanel)); Notify(nameof(ShowLocalPostProcessingModel));
        Notify(nameof(ShowCustomEndpointPanel)); Notify(nameof(ShowEnglishSpelling));
        Notify(nameof(ShowCustomInstructions)); Notify(nameof(ShowUserPrompt));
        Notify(nameof(ShowPunctuationOptions)); Notify(nameof(ShowRemoveTrailingPeriod));
        Notify(nameof(CanSave));
    }
}
