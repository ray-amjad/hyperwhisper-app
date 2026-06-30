using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;
using HyperWhisper.Utilities;
using HyperWhisper.Views.Pages.Settings;

namespace HyperWhisper.Views.Windows;

public partial class ModeEditorWindow : Window
{
    private readonly Mode _mode;
    private readonly bool _isCreateMode;
    private bool _isLoading;
    private bool _isUpdatingCloudModels;
    private bool _isUpdatingCloudTierModels;

    public ModeEditorWindow(Mode mode)
    {
        InitializeComponent();
        _mode = mode;
        _isCreateMode = false;

        Loaded += OnLoaded;
    }

    public ModeEditorWindow(bool isCreateMode)
    {
        InitializeComponent();
        _isCreateMode = isCreateMode;

        // Create a new mode with sensible defaults
        var existingModes = ModeService.Instance.GetAllModes();
        _mode = new Mode
        {
            Id = Guid.NewGuid(),
            Name = "",
            Preset = "hyper",
            ProviderType = "cloud",
            Model = "cloud",
            ModelType = "base",
            CloudProvider = "hyperwhisper",
            CloudAccuracyTier = CloudAccuracyTier.ElevenLabsScribeV2.ToStorageValue(),
            Language = "auto",
            IsDefault = false,
            IsSystemProvided = false,
            SortOrder = existingModes.Count,
            Punctuation = true,
            Capitalization = true,
            PostProcessingMode = 1,
            PostProcessingProvider = PostProcessingProvider.HyperWhisperCloud.ToStringValue(),
            CloudPostProcessingModel = CloudPostProcessingModel.ClaudeHaiku.ToStorageValue(),
            CreatedDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Configure UI for ARM64 architecture before loading data
        ConfigureForArchitecture();

        LoadLocalModels();
        LoadLanguages();
        LoadPostProcessingProviders();
        // Note: Cloud models are loaded dynamically in LoadModeIntoEditor
        // based on the selected cloud provider
        LoadModeIntoEditor(_mode);

        // Configure UI for create vs edit mode
        if (_isCreateMode)
        {
            Title = Loc.S("mode.editor.title.create");
            DeleteModeButton.Visibility = Visibility.Collapsed;
            SaveModeButton.Content = Loc.S("modes.button.create");
        }

        // Disable name field for Default mode
        ModeNameBox.IsEnabled = !_mode.IsDefault;

        // Update save button state based on name
        UpdateSaveButtonState();
    }

    /// <summary>
    /// Configures local provider availability for the current architecture.
    /// Local transcription remains visible when any native local engine is
    /// available, such as the ARM64 sherpa-onnx daemon.
    /// </summary>
    private void ConfigureForArchitecture()
    {
        if (!PlatformHelper.SupportsLocalTranscription)
        {
            // Show ARM64 info banner
            Arm64InfoBanner.Visibility = Visibility.Visible;

            // Hide the On-device source segment when no local engine can load.
            SourceOnDeviceSegment.Visibility = Visibility.Collapsed;

            // If the current mode uses an unavailable local engine, switch to cloud.
            if (_mode.ProviderType == "local" && !IsLocalEngineSupported(_mode.LocalEngine))
            {
                _mode.ProviderType = "cloud";
                _mode.CloudProvider = "hyperwhisper"; // Default to HyperWhisper Cloud (no API key needed)
            }
        }
        else if (_mode.ProviderType == "local" && !IsLocalEngineSupported(_mode.LocalEngine))
        {
            _mode.ProviderType = "cloud";
            _mode.CloudProvider = "hyperwhisper";
        }

        if (!PlatformHelper.SupportsLocalLlmPostProcessing)
        {
            // Hide the On-device post-processing segment and remove Local LLM from
            // the BYOK provider list.
            PpSourceOnDeviceSegment.Visibility = Visibility.Collapsed;

            var localLlmItem = PostProcessingProviderCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == PostProcessingProvider.LocalLlm.ToStringValue());
            if (localLlmItem != null)
            {
                PostProcessingProviderCombo.Items.Remove(localLlmItem);
            }

            if (_mode.PostProcessingProvider == PostProcessingProvider.LocalLlm.ToStringValue())
            {
                _mode.PostProcessingProvider = PostProcessingProvider.HyperWhisperCloud.ToStringValue();
                _mode.LocalPostProcessingModel = null;
            }
        }

        // The "Your provider" transcription list is BYOK-only — HyperWhisper Cloud is
        // reached via its own source segment. Azure/Google were never in the list.
        var hwTranscriptionItem = CloudProviderCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == "hyperwhisper");
        if (hwTranscriptionItem != null)
        {
            CloudProviderCombo.Items.Remove(hwTranscriptionItem);
        }

        // The BYOK post-processing list excludes HyperWhisper Cloud (its own segment)
        // and Local LLM (the On-device segment).
        var hwPpItem = PostProcessingProviderCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == "hyperwhispercloud");
        if (hwPpItem != null)
        {
            PostProcessingProviderCombo.Items.Remove(hwPpItem);
        }
        var localLlmByokItem = PostProcessingProviderCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == PostProcessingProvider.LocalLlm.ToStringValue());
        if (localLlmByokItem != null)
        {
            PostProcessingProviderCombo.Items.Remove(localLlmByokItem);
        }

        // Populate the HyperWhisper Cloud post-processing engine combo (Cerebras /
        // Anthropic / Grok). Anthropic is tagged "(Recommended)".
        LoadCloudPostProcessingEngines();

        // Tag the recommended transcription engine (ElevenLabs Scribe v2) in the
        // accuracy/engine combo with "(Recommended)".
        TagRecommendedTranscriptionEngine();
    }

    /// <summary>
    /// Appends "(Recommended)" to the ElevenLabs Scribe v2 entry in the HyperWhisper
    /// Cloud engine (accuracy tier) combo. Idempotent — only appends once.
    /// </summary>
    private void TagRecommendedTranscriptionEngine()
    {
        var item = CloudAccuracyCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == "elevenLabsScribeV2");
        if (item == null) return;

        var baseLabel = Loc.S("modes.cloudAccuracy.elevenLabsScribeV2.label");
        item.Content = Loc.S("mode.editor.engine.recommendedLabel", baseLabel);
    }

    // =========================================================================
    // SOURCE SEGMENT HELPERS (3-way: On-device / HyperWhisper Cloud / Your provider)
    // =========================================================================

    /// <summary>
    /// The currently selected transcription source segment tag
    /// ("ondevice" / "hwcloud" / "yourprovider").
    /// </summary>
    private string SelectedTranscriptionSource()
    {
        if (SourceOnDeviceSegment.IsChecked == true) return "ondevice";
        if (SourceYourProviderSegment.IsChecked == true) return "yourprovider";
        return "hwcloud";
    }

    /// <summary>
    /// Legacy provider-type string ("local" / "cloud") derived from the selected
    /// transcription source segment. Methods that branch on local-vs-cloud use this
    /// in place of the removed ProviderTypeCombo.
    /// </summary>
    private string SelectedProviderType()
        => SelectedTranscriptionSource() == "ondevice" ? "local" : "cloud";

    /// <summary>Selects the transcription source segment for the given source tag.</summary>
    private void SetTranscriptionSourceSegment(string source)
    {
        switch (source)
        {
            case "ondevice": SourceOnDeviceSegment.IsChecked = true; break;
            case "yourprovider": SourceYourProviderSegment.IsChecked = true; break;
            default: SourceHwCloudSegment.IsChecked = true; break;
        }
    }

    /// <summary>The currently selected post-processing source segment tag.</summary>
    private string SelectedPostProcessingSource()
    {
        if (PpSourceOnDeviceSegment.IsChecked == true) return "ondevice";
        if (PpSourceYourProviderSegment.IsChecked == true) return "yourprovider";
        return "hwcloud";
    }

    /// <summary>Selects the post-processing source segment for the given source tag.</summary>
    private void SetPostProcessingSourceSegment(string source)
    {
        switch (source)
        {
            case "ondevice": PpSourceOnDeviceSegment.IsChecked = true; break;
            case "yourprovider": PpSourceYourProviderSegment.IsChecked = true; break;
            default: PpSourceHwCloudSegment.IsChecked = true; break;
        }
    }

    // =========================================================================
    // NAME VALIDATION
    // =========================================================================

    private void ModeNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        UpdateSaveButtonState();
    }

    private void UpdateSaveButtonState()
    {
        // Name is required
        if (string.IsNullOrWhiteSpace(ModeNameBox.Text))
        {
            SaveModeButton.IsEnabled = false;
            return;
        }

        // If local provider selected, check if the selected model is downloaded
        var providerType = SelectedProviderType();
        if (providerType == "local")
        {
            var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var (engine, modelId) = ParseModelTag(tag);

            if (engine == "parakeet")
            {
                var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
                if (model != null)
                {
                    var modelService = new ParakeetModelService();
                    if (!modelService.IsModelDownloaded(model))
                    {
                        SaveModeButton.IsEnabled = false;
                        return;
                    }
                }
            }
            else
            {
                var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == modelId);
                if (model != null)
                {
                    var modelService = new WhisperModelService();
                    if (!modelService.IsModelDownloaded(model))
                    {
                        SaveModeButton.IsEnabled = false;
                        return;
                    }
                }
            }
        }

        if (PostProcessingCheck.IsChecked == true &&
            IsLocalLlmPostProcessingSelected())
        {
            var modelId = (PostProcessingModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var localModel = LocalLlmModelInfo.GetById(modelId);
            if (localModel != null)
            {
                var modelService = new LocalLlmModelService();
                if (!modelService.IsModelDownloaded(localModel))
                {
                    SaveModeButton.IsEnabled = false;
                    return;
                }
            }
        }

        SaveModeButton.IsEnabled = true;
    }

    private bool IsLocalLlmPostProcessingSelected()
    {
        // Local LLM post-processing is now selected via the On-device source segment.
        return SelectedPostProcessingSource() == "ondevice";
    }

    // =========================================================================
    // TAG HELPERS
    // =========================================================================

    /// <summary>
    /// Parses a unified model combo tag like "whisper:base" or "parakeet:parakeet-v2"
    /// into (engine, modelId).
    /// </summary>
    private static (string engine, string modelId) ParseModelTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return ("whisper", "base");

        var colonIndex = tag.IndexOf(':');
        if (colonIndex < 0)
            return ("whisper", tag);

        return (tag[..colonIndex], tag[(colonIndex + 1)..]);
    }

    // =========================================================================
    // DATA LOADING
    // =========================================================================

    private void LoadLocalModels()
    {
        LocalModelCombo.Items.Clear();

        // Whisper models (skip when the current architecture/runtime cannot load Whisper.net)
        if (PlatformHelper.SupportsWhisperTranscription)
        {
            var whisperService = new WhisperModelService();
            foreach (var model in WhisperModelInfo.AllModels)
            {
                var isDownloaded = whisperService.IsModelDownloaded(model);
                var item = new ComboBoxItem
                {
                    Content = $"{model.DisplayName} ({model.Size}){(isDownloaded ? "" : Loc.S("mode.editor.model.notDownloaded"))}",
                    Tag = $"whisper:{model.Type}"
                };
                LocalModelCombo.Items.Add(item);
            }
        }

        // Parakeet-family models (Parakeet, Qwen3 ASR, Nemotron) hosted by the sherpa-onnx daemon.
        if (PlatformHelper.SupportsParakeetTranscription)
        {
            var parakeetService = new ParakeetModelService();
            foreach (var model in ParakeetModelInfo.AllModels)
            {
                var isDownloaded = parakeetService.IsModelDownloaded(model);
                var langInfo = model.IsEnglishOnly ? "English" : "Multilingual";
                var item = new ComboBoxItem
                {
                    Content = $"{model.ProviderDisplayName} - {model.DisplayName} ({model.Size}, {langInfo}){(isDownloaded ? "" : Loc.S("mode.editor.parakeetModel.notDownloaded"))}",
                    Tag = $"parakeet:{model.Id}"
                };
                LocalModelCombo.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// Loads cloud models for a specific provider into the CloudModelCombo dropdown.
    /// Called when the cloud provider selection changes.
    /// </summary>
    private void LoadCloudModels(CloudTranscriptionProvider provider, string? preferredModelId = null)
    {
        CloudModelCombo.Items.Clear();

        var allModels = CloudTranscriptionModels.GetModelsForProvider(provider);
        var canonicalPreferredModelId = !string.IsNullOrEmpty(preferredModelId)
            ? CloudTranscriptionModels.ResolveModelAlias(preferredModelId, provider)
            : preferredModelId;

        if (!string.IsNullOrEmpty(canonicalPreferredModelId) &&
            CloudModelShowAllCheck.IsChecked != true &&
            !CloudTranscriptionModels.GetPopularModelsForProvider(provider)
                .Any(model => model.Id.Equals(canonicalPreferredModelId, StringComparison.OrdinalIgnoreCase)))
        {
            SetCloudModelShowAll(true);
        }

        var showAll = CloudModelShowAllCheck.IsChecked == true;
        var models = showAll ? allModels : CloudTranscriptionModels.GetPopularModelsForProvider(provider);
        foreach (var model in models)
        {
            var item = new ComboBoxItem
            {
                Content = showAll || !model.IsPopular
                    ? model.DisplayName
                    : Loc.S("mode.editor.cloudModel.popularLabel", model.DisplayName),
                Tag = model.Id
            };
            CloudModelCombo.Items.Add(item);
        }

        if (!string.IsNullOrEmpty(canonicalPreferredModelId))
        {
            foreach (ComboBoxItem item in CloudModelCombo.Items)
            {
                if (item.Tag?.ToString()?.Equals(canonicalPreferredModelId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    CloudModelCombo.SelectedItem = item;
                    return;
                }
            }
        }

        if (CloudModelCombo.Items.Count > 0)
        {
            CloudModelCombo.SelectedIndex = 0;
        }
    }

    private void CloudModelShowAllCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isUpdatingCloudModels) return;

        var providerTag = (CloudProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var provider = CloudTranscriptionProviderExtensions.FromIdentifier(providerTag);
        var selectedModelId = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        LoadCloudModels(provider, selectedModelId);
    }

    private void SetCloudModelShowAll(bool showAll)
    {
        _isUpdatingCloudModels = true;
        try
        {
            CloudModelShowAllCheck.IsChecked = showAll;
        }
        finally
        {
            _isUpdatingCloudModels = false;
        }
    }

    /// <summary>
    /// Handles cloud model selection change to update description text.
    /// </summary>
    private void CloudModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudModelCombo.SelectedItem is ComboBoxItem selected)
        {
            var modelId = selected.Tag?.ToString();
            var providerTag = (CloudProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(providerTag);
            var model = CloudTranscriptionModels.GetById(modelId, provider);

            // Show description with pricing if available
            if (model != null)
            {
                var description = model.Description;
                if (model.PricePerMinute.HasValue)
                {
                    if (provider == CloudTranscriptionProvider.AssemblyAI
                        && model.Id.EndsWith("-medical", StringComparison.Ordinal))
                    {
                        description += $" - base ${model.PricePerMinute:F4}/min + separate Medical Mode add-on";
                    }
                    else
                    {
                        description += $" - ${model.PricePerMinute:F4}/min";
                    }
                }
                CloudModelDescText.Text = description;
            }
            else
            {
                CloudModelDescText.Text = "";
            }
        }
        else
        {
            CloudModelDescText.Text = "";
        }

        if (!_isLoading)
        {
            AutoSelectEnglishForModel();
            UpdateLanguagesForSelectedModel();
            UpdateAllWarnings();
        }
    }

    /// <summary>
    /// Handles cloud provider selection change.
    /// Updates the model dropdown, accuracy tier panel, and API key warning for the new provider.
    /// </summary>
    private void CloudProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        ApplySelectedCloudProviderPanels();
        UpdateLanguagesForSelectedModel();
        UpdateAllWarnings();
    }

    private static bool IsLocalEngineSupported(string? localEngine)
    {
        return string.Equals(localEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            ? PlatformHelper.SupportsParakeetTranscription
            : PlatformHelper.SupportsWhisperTranscription;
    }

    /// <summary>
    /// Resolves the effective routed STT provider + model id from the current
    /// cloud picker selection. HyperWhisper Cloud is a tier wrapper: the outer
    /// provider is "hyperwhisper", but the real upstream provider is the selected
    /// accuracy tier's X-STT-Provider value and the model is the selected tier
    /// model (CloudTierModelCombo). For every other (BYOK) provider the model is
    /// the CloudModelCombo selection. Tiers whose upstream provider has no enum
    /// mapping (azure-mai / google-chirp / gemini → None) resolve to
    /// <see cref="CloudTranscriptionProvider.None"/>, so callers fall through to
    /// their default (full language list) branch. <paramref name="modelId"/> is
    /// never null (empty string when unselected, matching the X-STT-Model
    /// "provider default" convention).
    /// </summary>
    private void ResolveEffectiveCloudProviderAndModel(out CloudTranscriptionProvider provider, out string modelId)
    {
        // HyperWhisper Cloud is now its own source segment (not a CloudProviderCombo
        // entry): resolve the upstream provider + model from the accuracy tier and
        // tier model combo. Otherwise it's a BYOK provider from CloudProviderCombo.
        if (SelectedTranscriptionSource() == "hwcloud")
        {
            var tierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var sttProvider = Services.AppClassification.CloudSttCatalog.Shared.SttProviderForId(tierId);
            provider = CloudTranscriptionProviderExtensions.FromIdentifier(sttProvider);
            modelId = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            return;
        }

        var providerTag = (CloudProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        provider = CloudTranscriptionProviderExtensions.FromIdentifier(providerTag);
        modelId = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Shows the HyperWhisper Cloud transcription panels: the Engine (accuracy tier)
    /// picker + the per-tier Model picker. Used by the "HyperWhisper Cloud" source
    /// segment. The BYOK provider/model panels and the Gemini prompt are hidden.
    /// </summary>
    private void ApplyHwCloudTranscriptionPanels()
    {
        CloudProviderPanel.Visibility = Visibility.Collapsed;
        CloudModelPanel.Visibility = Visibility.Collapsed;
        GeminiCustomPromptPanel.Visibility = Visibility.Collapsed;

        CloudAccuracyPanel.Visibility = Visibility.Visible;

        var tierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        // Toggling the Source segment away and back must NOT reseed the tier model
        // or clear the domain — the user's prior HW Cloud selection is still held in
        // the (hidden) combos. Preserve and restore the current model + medical-domain
        // state so a non-default cloud model/domain isn't silently lost on a round-trip.
        // Only seed the catalog default when there was genuinely no prior selection.
        var preservedModelId = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var preservedMedicalChecked = MedicalDomainCheck.IsChecked == true;

        LoadCloudTierModels(tierId, preferredModelId: preservedModelId);
        ApplyMedicalDomainVisibility(tierId, isCheckedFromStorage: preservedMedicalChecked);
        MedicalDomainCheck.IsChecked = preservedMedicalChecked
            && string.Equals(tierId, "assemblyAI", StringComparison.OrdinalIgnoreCase);
        UpdateCloudAccuracyDescription();

        // HyperWhisper Cloud needs no BYOK API key.
        UpdateApiKeyWarning("cloud", CloudTranscriptionProvider.HyperWhisperCloud);
    }

    /// <summary>
    /// Shows the BYOK ("Your provider") transcription panels for the provider selected
    /// in <see cref="CloudProviderCombo"/> (HyperWhisper Cloud is excluded from that
    /// list — it has its own source segment).
    /// </summary>
    private void ApplySelectedCloudProviderPanels()
    {
        CloudAccuracyPanel.Visibility = Visibility.Collapsed;

        if (CloudProviderCombo.SelectedItem is ComboBoxItem selected)
        {
            var providerTag = selected.Tag?.ToString();
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(providerTag);

            if (provider == CloudTranscriptionProvider.Grok)
            {
                // Grok has a single implicit model — hide the model picker.
                CloudModelPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                CloudModelPanel.Visibility = Visibility.Visible;
                SetCloudModelShowAll(false);
                LoadCloudModels(provider);
            }

            // Show Gemini custom prompt panel only for Gemini provider
            GeminiCustomPromptPanel.Visibility = provider == CloudTranscriptionProvider.Gemini
                ? Visibility.Visible : Visibility.Collapsed;

            UpdateApiKeyWarning("cloud", provider);
        }
    }

    /// <summary>
    /// Handles cloud accuracy tier selection change.
    /// Updates the description text for the selected tier.
    /// </summary>
    private void CloudAccuracyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        // Switching tier resets the model to the tier's catalog default and
        // clears any domain (medical) selection — they're tier-specific.
        var tierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        LoadCloudTierModels(tierId, preferredModelId: null);
        ApplyMedicalDomainVisibility(tierId, isCheckedFromStorage: false);

        UpdateCloudAccuracyDescription();
        // Switching tiers can flip catalog vocab support (Deepgram → Chirp 3
        // hides the field; reverse re-shows it). Refresh the warning so the
        // user doesn't enter terms that the send-path gate will drop.
        UpdateAllWarnings();
        // The routed upstream provider (and thus its supported-language set)
        // changes with the tier, so re-filter the language picker.
        UpdateLanguagesForSelectedModel();
    }

    /// <summary>
    /// Populates <see cref="CloudTierModelCombo"/> from the selected HyperWhisper
    /// Cloud tier's catalog <c>models[]</c>. The default model is tagged "(Recommended)";
    /// preview and no-custom-vocabulary models get an inline hint. Single-model
    /// tiers (Grok, Azure MAI, Google Chirp) show one disabled entry. Tags carry
    /// the X-STT-Model id (may be the empty string for Grok's implicit model).
    /// </summary>
    private void LoadCloudTierModels(string? tierId, string? preferredModelId)
    {
        _isUpdatingCloudTierModels = true;
        try
        {
            CloudTierModelCombo.Items.Clear();

            var models = Services.AppClassification.CloudSttCatalog.Shared.ModelsForId(tierId);
            if (models.Count == 0)
            {
                CloudTierModelPanel.Visibility = Visibility.Collapsed;
                CloudTierModelDescText.Text = "";
                return;
            }

            CloudTierModelPanel.Visibility = Visibility.Visible;

            foreach (var model in models)
            {
                var label = model.DisplayName;
                if (model.IsDefault)
                    label = Loc.S("mode.editor.cloudModel.defaultLabel", label);
                if (model.PreviewStatus)
                    label = Loc.S("mode.editor.cloudModel.previewLabel", label);
                if (!model.SupportsCustomVocabulary)
                    label = Loc.S("mode.editor.cloudModel.noVocabularyLabel", label);

                CloudTierModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    // Empty-string ids (Grok) are preserved via Tag = "".
                    Tag = model.Id
                });
            }

            // Single implicit model (e.g. Grok): show it but disable interaction.
            CloudTierModelCombo.IsEnabled = models.Count > 1;

            // Select preferred (saved) model, else the catalog default, else first.
            var targetId = !string.IsNullOrEmpty(preferredModelId)
                ? preferredModelId
                : Services.AppClassification.CloudSttCatalog.Shared.DefaultModelIdForId(tierId);

            ComboBoxItem? match = null;
            foreach (ComboBoxItem item in CloudTierModelCombo.Items)
            {
                if (string.Equals(item.Tag?.ToString() ?? "", targetId ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }
            CloudTierModelCombo.SelectedItem = match ?? (CloudTierModelCombo.Items.Count > 0
                ? (ComboBoxItem)CloudTierModelCombo.Items[0]
                : null);
        }
        finally
        {
            _isUpdatingCloudTierModels = false;
        }

        UpdateCloudTierModelDescription();
    }

    private void CloudTierModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _isUpdatingCloudTierModels) return;

        UpdateCloudTierModelDescription();
        // Model can flip vocab support (scribe_v2 → scribe_v1) and the credits
        // caption is per-model, so refresh both.
        UpdateCloudAccuracyDescription();
        UpdateAllWarnings();
        // The model within a tier can narrow the language set (e.g. AssemblyAI
        // universal-3-pro → 7 langs), so re-filter the language picker.
        UpdateLanguagesForSelectedModel();
    }

    /// <summary>Updates the per-model description line under the tier model combo.</summary>
    private void UpdateCloudTierModelDescription()
    {
        var tierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var modelId = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var model = Services.AppClassification.CloudSttCatalog.Shared.GetModel(tierId, modelId);

        if (model == null)
        {
            CloudTierModelDescText.Text = "";
            return;
        }

        var parts = new List<string>();
        if (model.PreviewStatus) parts.Add(Loc.S("mode.editor.cloudModel.previewHint"));
        if (!model.SupportsCustomVocabulary) parts.Add(Loc.S("mode.editor.cloudModel.noVocabularyHint"));
        CloudTierModelDescText.Text = string.Join(" · ", parts);
    }

    /// <summary>
    /// Shows the medical-domain checkbox only for the AssemblyAI tier (Deepgram
    /// medical is a model choice, not a domain). When switching INTO AssemblyAI
    /// the checkbox is reset unless <paramref name="isCheckedFromStorage"/> says
    /// to honour a saved value.
    /// </summary>
    private void ApplyMedicalDomainVisibility(string? tierId, bool isCheckedFromStorage)
    {
        var isAssemblyAI = string.Equals(tierId, "assemblyAI", StringComparison.OrdinalIgnoreCase);
        MedicalDomainCheck.Visibility = isAssemblyAI ? Visibility.Visible : Visibility.Collapsed;
        if (!isAssemblyAI && !isCheckedFromStorage)
        {
            // Leaving the only domain-capable tier clears the domain.
            MedicalDomainCheck.IsChecked = false;
        }
    }

    private void MedicalDomainCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Persisted in SaveModeButton_Click. Toggling medical narrows the AssemblyAI
        // tier's supported languages to EN/ES/DE/FR (and restores them when cleared),
        // so re-filter the language picker.
        if (_isLoading) return;
        UpdateLanguagesForSelectedModel();
    }

    /// <summary>
    /// Updates the description text + credits/min caption for the selected cloud
    /// accuracy tier. The description falls back to the catalog display name when
    /// no localized string is present; credits reflect the SELECTED model.
    /// Credits sourced from <c>shared-app-classification/cloud-stt-catalog.json</c>.
    /// </summary>
    private void UpdateCloudAccuracyDescription()
    {
        if (CloudAccuracyCombo.SelectedItem is not ComboBoxItem selected)
        {
            CloudAccuracyCreditsPanel.Visibility = Visibility.Collapsed;
            CloudAccuracyDescText.Text = "";
            return;
        }

        var catalog = Services.AppClassification.CloudSttCatalog.Shared;
        var tier = selected.Tag?.ToString();

        // Prefer a localized description; fall back to the catalog display name
        // so the 6 new tiers render even before translation strings land.
        // (Loc.S returns the key verbatim when missing, so detect that.)
        var descKey = $"modes.cloudAccuracy.{tier}.description";
        var desc = Loc.S(descKey);
        if (desc == descKey)
        {
            desc = catalog.GetById(tier)?.DisplayName ?? "";
        }
        CloudAccuracyDescText.Text = desc;

        // Credits caption reflects the currently selected model within the tier.
        var modelId = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var credits = catalog.CreditsPerMinuteForModel(tier, modelId);
        if (credits > 0)
        {
            CloudAccuracyCreditsText.Text = Services.AppClassification.CloudSttCatalog
                .FormatCreditsPerMinute(credits, Loc.S("modes.cloudAccuracy.creditsPerMinute"));
            CloudAccuracyCreditsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            CloudAccuracyCreditsPanel.Visibility = Visibility.Collapsed;
        }
    }

    // =========================================================================
    // HYPERWHISPER CLOUD POST-PROCESSING ENGINE + MODEL
    // =========================================================================

    /// <summary>
    /// Populates the HyperWhisper Cloud post-processing Engine combo from the shared
    /// catalog (<c>CloudPpCatalog</c>), in catalog order, enabled engines only. The
    /// catalog's recommended engine (Anthropic today) is tagged "(Recommended)". The
    /// engine tag is the catalog engine id (the storage-key prefix). Mirrors macOS
    /// <c>CloudPostProcessingEngine.allCases</c> + <c>postProcessingEngineLabel</c>.
    /// </summary>
    private void LoadCloudPostProcessingEngines()
    {
        CloudPostProcessingEngineCombo.Items.Clear();
        foreach (var engine in CloudPostProcessingEngine.AllCases())
        {
            var label = engine.IsRecommended
                ? Loc.S("mode.editor.engine.recommendedLabel", engine.DisplayName)
                : engine.DisplayName;

            CloudPostProcessingEngineCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = engine.Id
            });
        }
    }

    /// <summary>The engine selected in the Engine combo (falls back to the catalog default).</summary>
    private CloudPostProcessingEngine SelectedCloudPostProcessingEngine()
    {
        var engineId = (CloudPostProcessingEngineCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(engineId))
            return new CloudPostProcessingEngine(engineId);

        var engines = CloudPostProcessingEngine.AllCases();
        return engines.FirstOrDefault(e => e.IsRecommended)
            ?? engines.FirstOrDefault()
            ?? CloudPostProcessingEngine.EngineFor(CloudPostProcessingModel.Fallback);
    }

    /// <summary>
    /// Repopulates the per-engine Model combo from the selected engine's enabled models
    /// (catalog order). The recommended engine's default model is tagged "(Recommended)".
    /// Each model tag is the provider-qualified storage value (the unit saved). Tries to
    /// preserve <paramref name="preferredModelId"/> (the engine-relative model id), else
    /// selects the engine's default. Mirrors macOS <c>postProcessingModelLabel</c>.
    /// </summary>
    private void LoadCloudPostProcessingModelsForEngine(string? preferredModelId = null)
    {
        CloudPostProcessingModelCombo.Items.Clear();

        var engine = SelectedCloudPostProcessingEngine();
        var models = engine.Models;
        if (models.Count == 0)
            return;

        foreach (var model in models)
        {
            var label = engine.IsRecommendedModel(model)
                ? Loc.S("mode.editor.engine.recommendedLabel", model.DisplayName)
                : model.DisplayName;

            CloudPostProcessingModelCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = model.StorageValue
            });
        }

        // Select the preferred model id within this engine, else the engine default.
        var target = !string.IsNullOrEmpty(preferredModelId)
            ? new CloudPostProcessingModel(engine.Id, preferredModelId!).StorageValue
            : engine.DefaultModel.StorageValue;

        ComboBoxItem? match = null;
        foreach (ComboBoxItem item in CloudPostProcessingModelCombo.Items)
        {
            if (item.Tag?.ToString() == target)
            {
                match = item;
                break;
            }
        }
        CloudPostProcessingModelCombo.SelectedItem = match
            ?? (CloudPostProcessingModelCombo.Items.Count > 0
                ? (ComboBoxItem)CloudPostProcessingModelCombo.Items[0]
                : null);
    }

    private void CloudPostProcessingEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        // Switching engine seeds the model combo to that engine's default model.
        LoadCloudPostProcessingModelsForEngine();
    }

    /// <summary>
    /// Resolves the effective CloudPostProcessingModel from the current engine/model
    /// selection (model combo wins; falls back to the engine's default).
    /// </summary>
    private CloudPostProcessingModel SelectedCloudPostProcessingModel()
    {
        var modelTag = (CloudPostProcessingModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(modelTag))
            return CloudPostProcessingModelExtensions.FromString(modelTag);

        return SelectedCloudPostProcessingEngine().DefaultModel;
    }

    /// <summary>
    /// Selects the Engine + Model combos to match a stored CloudPostProcessingModel.
    /// Coerces a stored engine that's hidden (<c>enabled:false</c>) or unknown to the
    /// recommended (or first) engine's default, mirroring the macOS
    /// <c>ensureValidCloudPostProcessingModel()</c> guard so the combos can't blank.
    /// </summary>
    private void SelectCloudPostProcessingModel(CloudPostProcessingModel model)
    {
        var engines = CloudPostProcessingEngine.AllCases();

        // Engine no longer offered (hidden/unknown) → reset to the recommended
        // (or first) engine's default model.
        if (!engines.Any(e => e.Id == model.EngineId))
        {
            var fallbackEngine = engines.FirstOrDefault(e => e.IsRecommended) ?? engines.FirstOrDefault();
            model = fallbackEngine?.DefaultModel ?? CloudPostProcessingModel.Fallback;
        }

        foreach (ComboBoxItem item in CloudPostProcessingEngineCombo.Items)
        {
            if (item.Tag?.ToString() == model.EngineId)
            {
                CloudPostProcessingEngineCombo.SelectedItem = item;
                break;
            }
        }
        if (CloudPostProcessingEngineCombo.SelectedIndex == -1 && CloudPostProcessingEngineCombo.Items.Count > 0)
            CloudPostProcessingEngineCombo.SelectedIndex = 0;

        // Preserve the stored model id within the engine (falls back to the engine
        // default if that model is hidden/unknown).
        LoadCloudPostProcessingModelsForEngine(model.ModelId);
    }

    private void LoadLanguages()
    {
        LanguageCombo.Items.Clear();

        foreach (var lang in LanguageInfo.AllLanguages)
        {
            var item = new ComboBoxItem
            {
                Content = lang.DisplayName,
                Tag = lang.Code
            };
            LanguageCombo.Items.Add(item);
        }
    }

    private void LoadModeIntoEditor(Mode mode)
    {
        _isLoading = true;

        try
        {
            ModeNameBox.Text = mode.Name;

            // Load preset (migrate legacy voiceToText)
            var presetValue = mode.Preset ?? "hyper";
            if (presetValue == "voiceToText")
                presetValue = "hyper";
            foreach (ComboBoxItem item in PresetCombo.Items)
            {
                if (item.Tag?.ToString() == presetValue)
                {
                    PresetCombo.SelectedItem = item;
                    break;
                }
            }
            if (PresetCombo.SelectedIndex == -1 && PresetCombo.Items.Count > 0)
            {
                PresetCombo.SelectedIndex = 0; // Default to Hyper
            }
            PresetDescText.Text = PresetTypeExtensions.FromString(presetValue).ToDescription();

            // Show custom instructions if preset is Custom
            CustomInstructionsPanel.Visibility = presetValue == "custom" ? Visibility.Visible : Visibility.Collapsed;
            CustomInstructionsBox.Text = mode.CustomInstructions ?? "";

            // Select saved model in unified combo using prefixed tag
            var savedEngine = mode.LocalEngine ?? "whisper";
            var savedModelId = savedEngine == "parakeet"
                ? (mode.LocalParakeetModel ?? "parakeet-v2")
                : (mode.ModelType ?? "base");
            var savedTag = $"{savedEngine}:{savedModelId}";

            bool foundLocalModel = false;
            foreach (ComboBoxItem item in LocalModelCombo.Items)
            {
                if (item.Tag?.ToString() == savedTag)
                {
                    LocalModelCombo.SelectedItem = item;
                    foundLocalModel = true;
                    break;
                }
            }
            if (!foundLocalModel && LocalModelCombo.Items.Count > 0)
            {
                LocalModelCombo.SelectedIndex = 0;
            }
            UpdateLocalModelStatus();

            // CLOUD PROVIDER AND MODEL SELECTION
            // 1. First select the cloud provider
            // 2. Then load models for that provider
            // 3. Finally select the saved model
            var cloudProviderTag = mode.CloudProvider ?? "hyperwhisper";
            // Legacy standalone BYOK provider values (Azure, Google Speech) are
            // folded into HyperWhisper Cloud accuracy tiers via the catalog's
            // migrateFrom aliases. If the saved cloudProvider is one of those
            // aliases for a cloud-tier-eligible entry, snap to "hyperwhisper"
            // + the matching tier.
            string? migratedAccuracyTier = null;
            var legacyTierEntry = Services.AppClassification.CloudSttCatalog.Shared
                .GetByMigrateFromAlias(cloudProviderTag);
            if (legacyTierEntry?.Access?.CloudTierEligible == true)
            {
                cloudProviderTag = "hyperwhisper";
                migratedAccuracyTier = legacyTierEntry.Id;
            }
            // Load models for the selected provider
            var cloudProvider = CloudTranscriptionProviderExtensions.FromIdentifier(cloudProviderTag);

            // Determine the 3-way transcription source from the resolved provider.
            // On-device → local; HyperWhisper Cloud → hwcloud; anything else → BYOK.
            var transcriptionSource = mode.ProviderType == "local"
                ? "ondevice"
                : (cloudProvider == CloudTranscriptionProvider.HyperWhisperCloud ? "hwcloud" : "yourprovider");

            // Select the BYOK provider combo (HyperWhisper Cloud was removed — its
            // segment drives the accuracy tier instead).
            if (transcriptionSource == "yourprovider")
            {
                bool foundCloudProvider = false;
                foreach (ComboBoxItem item in CloudProviderCombo.Items)
                {
                    if (item.Tag?.ToString() == cloudProviderTag)
                    {
                        CloudProviderCombo.SelectedItem = item;
                        foundCloudProvider = true;
                        break;
                    }
                }
                if (!foundCloudProvider && CloudProviderCombo.Items.Count > 0)
                {
                    CloudProviderCombo.SelectedIndex = 0;
                }
            }

            // Reflect the source in the segmented control (gated by _isLoading, so
            // this does not fire the Checked handler — panels are set up below).
            SetTranscriptionSourceSegment(transcriptionSource);

            // Top-level panel visibility for the source (the per-provider branches
            // below refine the cloud sub-panels). On-device shows the local picker;
            // the cloud sources show the relevant cloud panels and hide the local one.
            LocalModelPanel.Visibility = transcriptionSource == "ondevice" ? Visibility.Visible : Visibility.Collapsed;
            CloudProviderPanel.Visibility = transcriptionSource == "yourprovider" ? Visibility.Visible : Visibility.Collapsed;

            // Hide model selector for HyperWhisper Cloud (only has default model)
            // Show accuracy tier selector only for HyperWhisper Cloud
            if (cloudProvider == CloudTranscriptionProvider.HyperWhisperCloud)
            {
                // Only show cloud accuracy panel when this is actually the cloud source
                if (transcriptionSource == "hwcloud")
                {
                    CloudModelPanel.Visibility = Visibility.Collapsed;
                    CloudAccuracyPanel.Visibility = Visibility.Visible;
                }

                // Load the saved cloud accuracy tier (apply migration if a
                // legacy standalone provider was rewritten above).
                var accuracyTierValue = CloudAccuracyTierExtensions
                    .FromString(migratedAccuracyTier ?? mode.CloudAccuracyTier).ToStorageValue();
                foreach (ComboBoxItem item in CloudAccuracyCombo.Items)
                {
                    if (item.Tag?.ToString() == accuracyTierValue)
                    {
                        CloudAccuracyCombo.SelectedItem = item;
                        break;
                    }
                }
                if (CloudAccuracyCombo.SelectedIndex == -1 && CloudAccuracyCombo.Items.Count > 0)
                {
                    // Default to the Deepgram Nova-3 tier via named lookup —
                    // positional index 1 breaks silently if tier ordering changes.
                    CloudAccuracyCombo.SelectedItem = CloudAccuracyCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => (string?)item.Tag == "deepgramNova3")
                        ?? CloudAccuracyCombo.Items[0];
                }

                // Load the saved per-tier model (empty/null → catalog default)
                // and the saved domain (medical) for the resolved tier.
                var resolvedTierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                LoadCloudTierModels(resolvedTierId, mode.CloudTranscriptionModel);

                var savedDomainIsMedical = string.Equals(mode.CloudTranscriptionDomain, "medical", StringComparison.OrdinalIgnoreCase);
                ApplyMedicalDomainVisibility(resolvedTierId, isCheckedFromStorage: savedDomainIsMedical);
                MedicalDomainCheck.IsChecked = savedDomainIsMedical
                    && string.Equals(resolvedTierId, "assemblyAI", StringComparison.OrdinalIgnoreCase);

                UpdateCloudAccuracyDescription();
            }
            else if (cloudProvider == CloudTranscriptionProvider.Grok)
            {
                // Grok has a single implicit model — hide both pickers
                if (transcriptionSource == "yourprovider")
                {
                    CloudModelPanel.Visibility = Visibility.Collapsed;
                    CloudAccuracyPanel.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // Only show cloud model panel when this is the BYOK source
                if (transcriptionSource == "yourprovider")
                {
                    CloudModelPanel.Visibility = Visibility.Visible;
                    CloudAccuracyPanel.Visibility = Visibility.Collapsed;
                }
                // Select the saved cloud model (resolve legacy provider-specific IDs)
                var rawCloudModelId = mode.CloudTranscriptionModel ?? CloudTranscriptionModels.GetModelsForProvider(cloudProvider).FirstOrDefault()?.Id ?? "whisper-1";
                var cloudModelId = CloudTranscriptionModels.ResolveModelAlias(rawCloudModelId, cloudProvider);
                LoadCloudModels(cloudProvider, cloudModelId);

                bool foundCloudModel = false;
                foreach (ComboBoxItem item in CloudModelCombo.Items)
                {
                    if (item.Tag?.ToString() == cloudModelId)
                    {
                        CloudModelCombo.SelectedItem = item;
                        foundCloudModel = true;
                        break;
                    }
                }
                if (!foundCloudModel && CloudModelCombo.Items.Count > 0)
                {
                    CloudModelCombo.SelectedIndex = 0;
                }
            }

            // Filter language list for selected model before selecting saved language
            UpdateLanguagesForSelectedModel();

            // Load Gemini custom prompt
            var geminiPrompt = mode.GeminiCustomPrompt ?? "";
            GeminiCustomPromptBox.Text = geminiPrompt;
            GeminiCustomPromptCharCount.Text = $"{geminiPrompt.Length}/2000";
            GeminiCustomPromptPlaceholder.Visibility = string.IsNullOrEmpty(geminiPrompt)
                ? Visibility.Visible : Visibility.Collapsed;
            GeminiCustomPromptPanel.Visibility = cloudProvider == CloudTranscriptionProvider.Gemini && transcriptionSource == "yourprovider"
                ? Visibility.Visible : Visibility.Collapsed;

            bool foundLanguage = false;
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (item.Tag?.ToString() == mode.Language)
                {
                    LanguageCombo.SelectedItem = item;
                    foundLanguage = true;
                    break;
                }
            }
            if (!foundLanguage && LanguageCombo.Items.Count > 0)
            {
                LanguageCombo.SelectedIndex = 0;
            }

            // Load punctuation toggles
            PunctuationCheck.IsChecked = mode.Punctuation;
            RemoveTrailingPeriodCheck.IsChecked = mode.RemoveTrailingPeriod;
            CapitalizationCheck.IsChecked = mode.Capitalization;

            PostProcessingCheck.IsChecked = mode.PostProcessingMode != 0;
            PostProcessingSettingsPanel.Visibility = mode.PostProcessingMode != 0 ? Visibility.Visible : Visibility.Collapsed;

            // Punctuation & capitalization are LLM instructions — only show when post-processing is enabled
            var ppEnabled = mode.PostProcessingMode != 0;
            PunctuationCheck.Visibility = ppEnabled ? Visibility.Visible : Visibility.Collapsed;
            CapitalizationCheck.Visibility = ppEnabled ? Visibility.Visible : Visibility.Collapsed;
            RemoveTrailingPeriodCheck.Visibility = ppEnabled
                ? (mode.Punctuation ? Visibility.Visible : Visibility.Collapsed)
                : Visibility.Visible;

            // The HyperWhisper Cloud engine/model combos are always populated from
            // the saved CloudPostProcessingModel so switching the PP source segment
            // back to HW Cloud preserves the user's selection.
            SelectCloudPostProcessingModel(
                CloudPostProcessingModelExtensions.FromString(mode.CloudPostProcessingModel));

            if (mode.PostProcessingMode != 0)
            {
                var ppProvider = PostProcessingProviderExtensions.NormalizeStorageValue(mode.PostProcessingProvider)
                    ?? PostProcessingProvider.HyperWhisperCloud.ToStringValue();

                // Map the stored PP provider to one of the three source segments.
                var ppSource = ppProvider == "hyperwhispercloud"
                    ? "hwcloud"
                    : (ppProvider == PostProcessingProvider.LocalLlm.ToStringValue() ? "ondevice" : "yourprovider");
                SetPostProcessingSourceSegment(ppSource);

                if (ppSource == "hwcloud")
                {
                    PostProcessingProviderPanel.Visibility = Visibility.Collapsed;
                    PostProcessingModelPanel.Visibility = Visibility.Collapsed;
                    CloudPostProcessingModelPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    CloudPostProcessingModelPanel.Visibility = Visibility.Collapsed;
                    // Local LLM is reached via the On-device segment (the provider
                    // panel stays hidden); BYOK shows the provider picker.
                    PostProcessingProviderPanel.Visibility = ppSource == "yourprovider"
                        ? Visibility.Visible : Visibility.Collapsed;
                    PostProcessingModelPanel.Visibility = Visibility.Visible;

                    if (ppSource == "yourprovider")
                    {
                        foreach (var item in PostProcessingProviderCombo.Items.OfType<ComboBoxItem>())
                        {
                            if (item.Tag?.ToString() == ppProvider)
                            {
                                PostProcessingProviderCombo.SelectedItem = item;
                                break;
                            }
                        }
                        if (PostProcessingProviderCombo.SelectedIndex == -1 && PostProcessingProviderCombo.Items.Count > 0)
                            PostProcessingProviderCombo.SelectedIndex = 0;
                    }

                    // Populate the model picker for the resolved provider (Local LLM
                    // models for On-device; the BYOK provider's models otherwise).
                    var modelProvider = ppSource == "ondevice"
                        ? PostProcessingProvider.LocalLlm
                        : PostProcessingProviderExtensions.FromString(
                            (PostProcessingProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ppProvider);
                    LoadPostProcessingModels(modelProvider);

                    var ppModelSource = ppSource == "ondevice"
                        ? mode.LocalPostProcessingModel ?? mode.LanguageModel
                        : mode.LanguageModel;
                    var ppModel = LanguageModelInfo.MigrateModelId(ppModelSource);
                    bool foundPPModel = false;
                    foreach (ComboBoxItem item in PostProcessingModelCombo.Items)
                    {
                        if (item.Tag?.ToString() == ppModel)
                        {
                            PostProcessingModelCombo.SelectedItem = item;
                            foundPPModel = true;
                            break;
                        }
                    }
                    if (!foundPPModel && PostProcessingModelCombo.Items.Count > 0)
                    {
                        PostProcessingModelCombo.SelectedIndex = 0;
                    }
                }

                // Load English spelling
                var spellingValue = mode.EnglishSpelling ?? "american";
                foreach (ComboBoxItem item in EnglishSpellingCombo.Items)
                {
                    if (item.Tag?.ToString() == spellingValue)
                    {
                        EnglishSpellingCombo.SelectedItem = item;
                        break;
                    }
                }
                if (EnglishSpellingCombo.SelectedIndex == -1 && EnglishSpellingCombo.Items.Count > 0)
                {
                    EnglishSpellingCombo.SelectedIndex = 0; // Default to American
                }

                ProfanityFilterCheck.IsChecked = mode.ProfanityFilter;
                ScreenOCRCheck.IsChecked = mode.EnableScreenOCR;

                // Load user system prompt
                var userPrompt = mode.UserSystemPrompt ?? "";
                var hasUserPrompt = !string.IsNullOrWhiteSpace(userPrompt);
                UserPromptCheck.IsChecked = hasUserPrompt;
                UserPromptPanel.Visibility = hasUserPrompt ? Visibility.Visible : Visibility.Collapsed;
                UserPromptBox.Text = userPrompt;
                UserPromptCharCount.Text = $"{userPrompt.Length}/2000";

            }
            else
            {
                // Post-processing is off - set defaults for when it's enabled.
                // Default the source segment to HyperWhisper Cloud (or On-device if
                // HW Cloud somehow unavailable) and show its panel.
                SetPostProcessingSourceSegment("hwcloud");
                PostProcessingProviderPanel.Visibility = Visibility.Collapsed;
                PostProcessingModelPanel.Visibility = Visibility.Collapsed;
                CloudPostProcessingModelPanel.Visibility = Visibility.Visible;

                EnglishSpellingCombo.SelectedIndex = 0; // American
                ProfanityFilterCheck.IsChecked = mode.ProfanityFilter;
                ScreenOCRCheck.IsChecked = mode.EnableScreenOCR;
                UserPromptCheck.IsChecked = false;
                UserPromptPanel.Visibility = Visibility.Collapsed;
                UserPromptBox.Text = mode.UserSystemPrompt ?? "";
            }

            // Update English spelling visibility based on language
            UpdateEnglishSpellingVisibility();

            UpdateApiKeyWarning(SelectedProviderType());

            // Update vocabulary and model warnings
            UpdateAllWarnings();

            // Update language dropdown state for English-only models
            AutoSelectEnglishForModel();
        }
        finally
        {
            _isLoading = false;
        }
    }

    // =========================================================================
    // EVENT HANDLERS
    // =========================================================================

    private void SourceSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var source = SelectedTranscriptionSource();
        // UpdateSourcePanels sets the API-key warning with the correct provider for
        // each source (HW Cloud → no key; BYOK → the combo provider; local → cleared).
        UpdateSourcePanels(source);
        UpdateLanguagesForSelectedModel();
        UpdateAllWarnings();
        UpdateSaveButtonState();
    }

    /// <summary>
    /// Shows the transcription model controls for the selected source segment:
    /// On-device → local model picker; HyperWhisper Cloud → Engine (tier) + Model;
    /// Your provider → BYOK provider + model. When switching INTO a cloud source,
    /// the CloudProvider context is set so downstream resolvers behave correctly.
    /// </summary>
    private void UpdateSourcePanels(string source)
    {
        switch (source)
        {
            case "ondevice":
                LocalModelPanel.Visibility = Visibility.Visible;
                CloudProviderPanel.Visibility = Visibility.Collapsed;
                CloudModelPanel.Visibility = Visibility.Collapsed;
                CloudAccuracyPanel.Visibility = Visibility.Collapsed;
                GeminiCustomPromptPanel.Visibility = Visibility.Collapsed;
                UpdateApiKeyWarning("local");
                break;

            case "hwcloud":
                LocalModelPanel.Visibility = Visibility.Collapsed;
                ParakeetLanguageWarning.Visibility = Visibility.Collapsed;
                CloudProviderPanel.Visibility = Visibility.Collapsed;
                ApplyHwCloudTranscriptionPanels();
                break;

            default: // "yourprovider"
                LocalModelPanel.Visibility = Visibility.Collapsed;
                ParakeetLanguageWarning.Visibility = Visibility.Collapsed;
                CloudProviderPanel.Visibility = Visibility.Visible;
                // Ensure a BYOK provider is selected (HyperWhisper was removed).
                if (CloudProviderCombo.SelectedIndex == -1 && CloudProviderCombo.Items.Count > 0)
                    CloudProviderCombo.SelectedIndex = 0;
                ApplySelectedCloudProviderPanels();
                break;
        }
    }

    private void UpdateLocalModelStatus()
    {
        if (LocalModelCombo.SelectedItem is not ComboBoxItem selected) return;

        var (engine, modelId) = ParseModelTag(selected.Tag?.ToString());

        if (engine == "parakeet")
        {
            var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
            if (model != null)
            {
                var modelService = new ParakeetModelService();
                var isDownloaded = modelService.IsModelDownloaded(model);
                LocalModelStatusText.Text = isDownloaded
                    ? ReadyTextForParakeetFamily(model)
                    : "Model not downloaded. Go to Model Library to download.";
            }
        }
        else
        {
            var model = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == modelId);
            if (model != null)
            {
                var modelService = new WhisperModelService();
                var isDownloaded = modelService.IsModelDownloaded(model);
                LocalModelStatusText.Text = isDownloaded
                    ? $"Ready - {model.RecommendedVramDisplay} VRAM"
                    : "Model not downloaded. Go to Home to download.";
            }
        }
    }

    private void LocalModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        UpdateLocalModelStatus();
        AutoSelectEnglishForModel();
        UpdateLanguagesForSelectedModel();
        UpdateAllWarnings();
        UpdateSaveButtonState();
    }

    private void UpdateParakeetLanguageWarning()
    {
        var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var (engine, modelId) = ParseModelTag(tag);

        if (engine != "parakeet")
        {
            ParakeetLanguageWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        if (model == null)
        {
            ParakeetLanguageWarning.Visibility = Visibility.Collapsed;
            return;
        }

        if (model.IsEnglishOnly && language != "en")
        {
            ParakeetLanguageWarningText.Text = $"{model.DisplayName} supports English only. Language will be set to English.";
            ParakeetLanguageWarning.Visibility = Visibility.Visible;
        }
        else if (!model.IsEnglishOnly && !string.IsNullOrEmpty(language) && language != "auto" && !model.IsLanguageSupported(language))
        {
            ParakeetLanguageWarningText.Text = $"{model.DisplayName} does not support this language. Choose another local model or a cloud provider.";
            ParakeetLanguageWarning.Visibility = Visibility.Visible;
        }
        else
        {
            ParakeetLanguageWarning.Visibility = Visibility.Collapsed;
        }
    }

    private static string ReadyTextForParakeetFamily(ParakeetModelInfo model)
    {
        return model.Engine switch
        {
            ParakeetEngine.Qwen3 => "Ready - CPU inference",
            ParakeetEngine.NemotronMl => "Ready - CPU streaming",
            _ => PlatformHelper.IsArm64
                ? "Ready - CPU inference"
                : "Ready - DirectML acceleration with CPU fallback"
        };
    }

    // =========================================================================
    // PRESET HANDLING
    // =========================================================================

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        if (PresetCombo.SelectedItem is ComboBoxItem selected)
        {
            var presetTag = selected.Tag?.ToString();
            PresetDescText.Text = PresetTypeExtensions.FromString(presetTag ?? "hyper").ToDescription();

            // Show/hide custom instructions panel
            CustomInstructionsPanel.Visibility = presetTag == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // =========================================================================
    // LANGUAGE HANDLING
    // =========================================================================

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        UpdateEnglishSpellingVisibility();
        UpdateNova3Warning();
    }

    private void UpdateEnglishSpellingVisibility()
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem selected)
        {
            var lang = selected.Tag?.ToString();
            // Show English spelling only for English or Auto-detect
            var isEnglishRelated = lang == "en" || lang == "auto" || string.IsNullOrEmpty(lang);
            var isPostProcessingEnabled = PostProcessingCheck.IsChecked == true;

            EnglishSpellingPanel.Visibility = (isEnglishRelated && isPostProcessingEnabled)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // =========================================================================
    // PUNCTUATION / REMOVE TRAILING PERIOD HANDLING
    // =========================================================================

    private void PunctuationCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        bool punctuationEnabled = PunctuationCheck.IsChecked == true;

        // Only toggle RemoveTrailingPeriod visibility when post-processing is enabled
        // (when post-processing is off, RemoveTrailingPeriod is always visible standalone)
        if (PostProcessingCheck.IsChecked == true)
        {
            RemoveTrailingPeriodCheck.Visibility = punctuationEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!punctuationEnabled)
        {
            RemoveTrailingPeriodCheck.IsChecked = false;
        }
    }

    // =========================================================================
    // USER SYSTEM PROMPT HANDLING
    // =========================================================================

    private void UserPromptCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        UserPromptPanel.Visibility = UserPromptCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (UserPromptCheck.IsChecked != true)
        {
            UserPromptBox.Text = "";
        }
    }

    private void ClearPromptButton_Click(object sender, RoutedEventArgs e)
    {
        UserPromptBox.Text = "";
    }

    private void UserPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UserPromptCharCount.Text = $"{UserPromptBox.Text.Length}/2000";
    }

    private void GeminiCustomPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GeminiCustomPromptCharCount.Text = $"{GeminiCustomPromptBox.Text.Length}/2000";
        GeminiCustomPromptPlaceholder.Visibility = string.IsNullOrEmpty(GeminiCustomPromptBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Updates the API key warning based on provider type and selected cloud provider.
    /// Shows a warning if the required API key is not configured.
    /// </summary>
    private void UpdateApiKeyWarning(string providerType, CloudTranscriptionProvider? cloudProvider = null)
    {
        if (providerType != "cloud")
        {
            ApiKeyWarning.Visibility = Visibility.Collapsed;
            return;
        }

        // Get selected cloud provider if not passed
        if (cloudProvider == null)
        {
            var selectedItem = CloudProviderCombo.SelectedItem as ComboBoxItem;
            var providerTag = selectedItem?.Tag?.ToString();
            cloudProvider = CloudTranscriptionProviderExtensions.FromIdentifier(providerTag);
        }

        // HyperWhisper Cloud and the HW-Cloud-routed providers (Azure MAI,
        // Google Chirp) don't require an API key.
        if (cloudProvider.HasValue && !cloudProvider.Value.RequiresApiKey())
        {
            ApiKeyWarning.Visibility = Visibility.Collapsed;
            return;
        }

        // Check if API key is configured for this provider
        bool hasKey = cloudProvider switch
        {
            // These share API keys with post-processing providers
            CloudTranscriptionProvider.OpenAI => ApiKeyService.Instance.HasApiKey(PostProcessingProvider.OpenAI),
            CloudTranscriptionProvider.Groq => ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Groq),
            CloudTranscriptionProvider.Gemini => ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Gemini),
            CloudTranscriptionProvider.Grok => ApiKeyService.Instance.HasApiKey(PostProcessingProvider.Grok),

            // These have their own transcription API keys
            CloudTranscriptionProvider.Deepgram => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Deepgram),
            CloudTranscriptionProvider.AssemblyAI => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.AssemblyAI),
            CloudTranscriptionProvider.ElevenLabs => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.ElevenLabs),
            CloudTranscriptionProvider.Mistral => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Mistral),
            CloudTranscriptionProvider.Soniox => ApiKeyService.Instance.HasApiKey(TranscriptionApiKeyType.Soniox),

            _ => false
        };

        if (!hasKey)
        {
            var providerName = cloudProvider?.GetDisplayName() ?? "Cloud";
            ApiKeyWarning.Visibility = Visibility.Visible;
            ApiKeyWarningText.Text = $"{providerName} API key not configured. Add it in the Model Library API keys manager before using this provider.";
        }
        else
        {
            ApiKeyWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void PostProcessingCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var ppEnabled = PostProcessingCheck.IsChecked == true;
        PostProcessingSettingsPanel.Visibility = ppEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (ppEnabled)
        {
            if (PresetCombo.SelectedIndex == -1 && PresetCombo.Items.Count > 0)
                PresetCombo.SelectedIndex = 0;
            // Show the controls for the currently selected post-processing source.
            ApplyPostProcessingSourcePanels();
        }

        // Punctuation & capitalization are LLM instructions — hide when post-processing is off
        PunctuationCheck.Visibility = ppEnabled ? Visibility.Visible : Visibility.Collapsed;
        CapitalizationCheck.Visibility = ppEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (!ppEnabled)
        {
            // Show remove trailing period standalone when post-processing is off
            RemoveTrailingPeriodCheck.Visibility = Visibility.Visible;
            PunctuationCheck.IsChecked = false;
            CapitalizationCheck.IsChecked = false;
            ScreenOCRCheck.IsChecked = false;
        }
        else
        {
            // When post-processing is on, remove trailing period follows punctuation toggle
            RemoveTrailingPeriodCheck.Visibility = PunctuationCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // Update English spelling visibility when post-processing toggle changes
        UpdateEnglishSpellingVisibility();
        UpdateSaveButtonState();
    }

    /// <summary>
    /// Drives the post-processing model controls from the selected source segment:
    /// On-device → Local LLM model picker; HyperWhisper Cloud → Engine + Model;
    /// Your provider → BYOK provider + model. The PostProcessingProvider context is
    /// set here so the save path and warnings resolve correctly.
    /// </summary>
    private void PpSourceSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ApplyPostProcessingSourcePanels();
        UpdateSaveButtonState();
    }

    private void ApplyPostProcessingSourcePanels()
    {
        var source = SelectedPostProcessingSource();
        switch (source)
        {
            case "ondevice":
                // Local LLM: hide BYOK provider + HW-Cloud engine; show the model
                // picker populated with on-device GGUF models.
                PostProcessingProviderPanel.Visibility = Visibility.Collapsed;
                CloudPostProcessingModelPanel.Visibility = Visibility.Collapsed;
                PostProcessingModelPanel.Visibility = Visibility.Visible;
                LoadPostProcessingModels(PostProcessingProvider.LocalLlm);
                break;

            case "hwcloud":
                PostProcessingProviderPanel.Visibility = Visibility.Collapsed;
                PostProcessingModelPanel.Visibility = Visibility.Collapsed;
                LocalLlmOpenModelsSettingsButton.Visibility = Visibility.Collapsed;
                CloudPostProcessingModelPanel.Visibility = Visibility.Visible;
                break;

            default: // "yourprovider"
                CloudPostProcessingModelPanel.Visibility = Visibility.Collapsed;
                PostProcessingProviderPanel.Visibility = Visibility.Visible;
                if (PostProcessingProviderCombo.SelectedIndex == -1 && PostProcessingProviderCombo.Items.Count > 0)
                    PostProcessingProviderCombo.SelectedIndex = 0;
                ApplySelectedPostProcessingProviderPanels();
                break;
        }
    }

    private void PostProcessingProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        // Only relevant for the "Your provider" source — the BYOK list excludes
        // HyperWhisper Cloud and Local LLM (both reached via source segments).
        if (SelectedPostProcessingSource() == "yourprovider")
        {
            ApplySelectedPostProcessingProviderPanels();
        }
        UpdateSaveButtonState();
    }

    /// <summary>
    /// Shows/populates the BYOK post-processing model picker for the provider selected
    /// in <see cref="PostProcessingProviderCombo"/>. Custom endpoints carry their model
    /// in the endpoint config, so the picker is hidden for them.
    /// </summary>
    private void ApplySelectedPostProcessingProviderPanels()
    {
        if (PostProcessingProviderCombo.SelectedItem is not ComboBoxItem selected) return;

        var providerStr = selected.Tag?.ToString();

        // Hide model selection for custom endpoints (model is in endpoint config)
        if (CustomPostProcessingEndpoint.IsCustomProviderString(providerStr))
        {
            PostProcessingModelPanel.Visibility = Visibility.Collapsed;
            LocalLlmOpenModelsSettingsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PostProcessingModelPanel.Visibility = Visibility.Visible;
            var provider = PostProcessingProviderExtensions.FromString(providerStr ?? "");
            LoadPostProcessingModels(provider);
        }
    }

    /// <summary>
    /// Populates the post-processing provider ComboBox dynamically,
    /// including custom endpoints after a separator.
    /// </summary>
    private void LoadPostProcessingProviders()
    {
        _isLoading = true;

        // Keep the static XAML items (they are already in the ComboBox)
        // Just append custom endpoints after them

        // Remove any previously added custom endpoint items (separator + items)
        var itemsToRemove = new List<object>();
        bool foundSeparator = false;
        foreach (var item in PostProcessingProviderCombo.Items)
        {
            if (item is Separator)
            {
                foundSeparator = true;
                itemsToRemove.Add(item);
            }
            else if (foundSeparator)
            {
                itemsToRemove.Add(item);
            }
        }
        foreach (var item in itemsToRemove)
        {
            PostProcessingProviderCombo.Items.Remove(item);
        }

        // Add custom endpoints if any exist
        var customEndpoints = CustomEndpointManager.Instance.GetAllEndpoints();
        if (customEndpoints.Count > 0)
        {
            PostProcessingProviderCombo.Items.Add(new Separator());
            foreach (var endpoint in customEndpoints)
            {
                PostProcessingProviderCombo.Items.Add(new ComboBoxItem
                {
                    Content = endpoint.Name,
                    Tag = endpoint.ProviderString
                });
            }
        }

        _isLoading = false;
    }

    private void LoadPostProcessingModels(PostProcessingProvider provider)
    {
        PostProcessingModelCombo.Items.Clear();
        
        var models = LanguageModelInfo.GetModelsForProvider(provider);
        foreach (var model in models)
        {
            var content = model.DisplayName;
            if (provider == PostProcessingProvider.LocalLlm)
            {
                var localModel = LocalLlmModelInfo.GetById(model.Id);
                if (localModel != null)
                {
                    var modelService = new LocalLlmModelService();
                    var isDownloaded = modelService.IsModelDownloaded(localModel);
                    content = $"{localModel.DisplayName} ({localModel.Size}){(isDownloaded ? "" : Loc.S("settings.models.localLlm.notDownloadedSuffix"))}";
                }
            }

            var item = new ComboBoxItem
            {
                Content = content,
                Tag = model.Id
            };
            PostProcessingModelCombo.Items.Add(item);
        }

        if (PostProcessingModelCombo.Items.Count > 0)
        {
            PostProcessingModelCombo.SelectedIndex = 0;
        }
    }

    private void PostProcessingModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PostProcessingModelCombo.SelectedItem is ComboBoxItem selected)
        {
            var modelId = selected.Tag?.ToString();
            var model = LanguageModelInfo.GetById(modelId);
            // Local LLM is selected via the On-device source segment, not a provider-combo
            // item (ConfigureForArchitecture removes "local_llm" from the combo). Detect it
            // the same way as IsLocalLlmPostProcessingSelected() so the download-required
            // description + Open Models Settings button render for an undownloaded GGUF model.
            if (IsLocalLlmPostProcessingSelected())
            {
                var localModel = LocalLlmModelInfo.GetById(modelId);
                if (localModel != null)
                {
                    var modelService = new LocalLlmModelService();
                    var isDownloaded = modelService.IsModelDownloaded(localModel);
                    PostProcessingModelDescText.Text = isDownloaded
                        ? Loc.S("mode.editor.localLlm.readyDescription", localModel.Description)
                        : Loc.S("mode.editor.localLlm.downloadDescription", localModel.Description);
                    LocalLlmOpenModelsSettingsButton.Visibility = isDownloaded ? Visibility.Collapsed : Visibility.Visible;
                    UpdateSaveButtonState();
                    return;
                }
            }

            PostProcessingModelDescText.Text = model?.Description ?? "";
            LocalLlmOpenModelsSettingsButton.Visibility = Visibility.Collapsed;
            UpdateSaveButtonState();
        }
        else
        {
            PostProcessingModelDescText.Text = "";
            LocalLlmOpenModelsSettingsButton.Visibility = Visibility.Collapsed;
            UpdateSaveButtonState();
        }
    }

    private void LocalLlmOpenModelsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedModelId = (PostProcessingModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var settingsWindow = new Window
        {
            Title = Loc.S("settings.section.models"),
            Width = 720,
            Height = 760,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ModelsSettingsPage()
        };

        settingsWindow.ShowDialog();

        LoadPostProcessingModels(PostProcessingProvider.LocalLlm);
        if (!string.IsNullOrEmpty(selectedModelId))
        {
            foreach (ComboBoxItem item in PostProcessingModelCombo.Items)
            {
                if (item.Tag?.ToString() == selectedModelId)
                {
                    PostProcessingModelCombo.SelectedItem = item;
                    break;
                }
            }
        }
        UpdateSaveButtonState();
    }

    private void SaveModeButton_Click(object sender, RoutedEventArgs e)
    {
        _mode.Name = ModeNameBox.Text.Trim();

        // Save preset
        if (PresetCombo.SelectedItem is ComboBoxItem presetItem)
        {
            _mode.Preset = presetItem.Tag?.ToString() ?? "hyper";
        }
        else if (string.IsNullOrEmpty(_mode.Preset) || _mode.Preset == "voiceToText")
        {
            _mode.Preset = "hyper";
        }

        // Save custom instructions (only relevant for Custom preset, but always save)
        _mode.CustomInstructions = CustomInstructionsBox.Text.Trim();

        var transcriptionSource = SelectedTranscriptionSource();
        _mode.ProviderType = transcriptionSource == "ondevice" ? "local" : "cloud";

        if (_mode.ProviderType == "cloud")
        {
            // HyperWhisper Cloud is its own source segment; "Your provider" is BYOK.
            if (transcriptionSource == "hwcloud")
            {
                _mode.CloudProvider = "hyperwhisper";
            }
            else if (CloudProviderCombo.SelectedItem is ComboBoxItem providerComboItem)
            {
                _mode.CloudProvider = providerComboItem.Tag?.ToString() ?? "openai";
            }

            if (_mode.CloudProvider == "hyperwhisper")
            {
                // HyperWhisper Cloud: the model axis is the per-tier model combo
                // (X-STT-Model) and the domain is the medical checkbox
                // (X-STT-Domain). The BYOK CloudModelCombo is hidden here.
                _mode.CloudTranscriptionModel = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

                _mode.CloudTranscriptionDomain =
                    (MedicalDomainCheck.Visibility == Visibility.Visible && MedicalDomainCheck.IsChecked == true)
                        ? "medical"
                        : null;
            }
            // Grok (BYOK) has a single implicit model — store empty sentinel regardless of any
            // stale entries in the (hidden) CloudModelCombo from a prior provider selection.
            else if (_mode.CloudProvider == "grok")
            {
                _mode.CloudTranscriptionModel = "";
                _mode.CloudTranscriptionDomain = null;
            }
            else if (CloudModelCombo.SelectedItem is ComboBoxItem cloudItem)
            {
                _mode.CloudTranscriptionModel = cloudItem.Tag?.ToString() ?? "whisper-1";
                _mode.CloudTranscriptionDomain = null;
            }
            else
            {
                _mode.CloudTranscriptionDomain = null;
            }

            // Save the cloud accuracy tier (for HyperWhisper Cloud)
            if (CloudAccuracyCombo.SelectedItem is ComboBoxItem accuracyItem)
            {
                _mode.CloudAccuracyTier = CloudAccuracyTierExtensions.FromString(accuracyItem.Tag?.ToString()).ToStorageValue();
            }

            // Save Gemini custom prompt
            var geminiPromptText = GeminiCustomPromptBox.Text?.Trim() ?? "";
            _mode.GeminiCustomPrompt = string.IsNullOrEmpty(geminiPromptText) ? null : geminiPromptText;
        }
        else
        {
            var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var (engine, modelId) = ParseModelTag(tag);
            _mode.LocalEngine = engine;

            if (engine == "parakeet")
            {
                _mode.LocalParakeetModel = modelId;
            }
            else
            {
                _mode.ModelType = modelId;
            }
        }

        if (LanguageCombo.SelectedItem is ComboBoxItem langItem)
        {
            _mode.Language = langItem.Tag?.ToString() ?? "auto";
        }

        // Save text processing toggles (always save, regardless of post-processing state)
        _mode.Punctuation = PunctuationCheck.IsChecked == true;
        _mode.Capitalization = CapitalizationCheck.IsChecked == true;
        _mode.ProfanityFilter = ProfanityFilterCheck.IsChecked == true;
        _mode.EnableScreenOCR = ScreenOCRCheck.IsChecked == true;
        _mode.RemoveTrailingPeriod = RemoveTrailingPeriodCheck.IsChecked == true;

        // Save English spelling
        if (EnglishSpellingCombo.SelectedItem is ComboBoxItem spellingItem)
        {
            _mode.EnglishSpelling = spellingItem.Tag?.ToString() ?? "american";
        }

        // Save user system prompt
        _mode.UserSystemPrompt = UserPromptCheck.IsChecked == true ? UserPromptBox.Text.Trim() : "";

        // Always persist the HyperWhisper Cloud post-processing engine/model choice
        // so it survives switching the PP source segment away and back.
        _mode.CloudPostProcessingModel = SelectedCloudPostProcessingModel().ToStorageValue();

        if (PostProcessingCheck.IsChecked == true)
        {
            // The post-processing provider is driven by the source segment, not a
            // visible "HyperWhisper Cloud" combo item: On-device → Local LLM,
            // HyperWhisper Cloud → hyperwhispercloud, Your provider → BYOK combo.
            var ppSource = SelectedPostProcessingSource();
            if (ppSource == "ondevice")
            {
                _mode.PostProcessingProvider = PostProcessingProvider.LocalLlm.ToStringValue();
            }
            else if (ppSource == "hwcloud")
            {
                _mode.PostProcessingProvider = PostProcessingProvider.HyperWhisperCloud.ToStringValue();
            }
            else if (PostProcessingProviderCombo.SelectedItem is ComboBoxItem ppProviderItem)
            {
                _mode.PostProcessingProvider = ppProviderItem.Tag?.ToString();
            }

            _mode.PostProcessingMode = _mode.PostProcessingProvider == PostProcessingProvider.LocalLlm.ToStringValue()
                ? 2
                : 1;

            // Save language model (only meaningful for On-device + BYOK; HyperWhisper
            // Cloud uses CloudPostProcessingModel above). Keep the existing value when
            // the HW-Cloud source is active so switching providers preserves it.
            if (ppSource != "hwcloud" && PostProcessingModelCombo.SelectedItem is ComboBoxItem ppModelItem)
            {
                if (ppSource == "ondevice")
                {
                    _mode.LocalPostProcessingModel = ppModelItem.Tag?.ToString();
                }
                else
                {
                    _mode.LanguageModel = ppModelItem.Tag?.ToString();
                }
            }
        }
        else
        {
            _mode.PostProcessingMode = 0;
            _mode.EnableScreenOCR = false;
        }

        _mode.ModifiedDate = DateTime.UtcNow;

        if (_isCreateMode)
        {
            ModeService.Instance.SaveMode(_mode);
        }
        else
        {
            ModeService.Instance.UpdateMode(_mode);
        }

        DialogResult = true;
        Close();
    }

    private void DeleteModeButton_Click(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(
            Loc.S("mode.editor.delete.confirm.message", _mode.Name),
            Loc.S("mode.editor.delete.confirm.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var deleted = ModeService.Instance.DeleteMode(_mode.Id);

        if (!deleted)
        {
            WpfMessageBox.Show(
                Loc.S("mode.editor.delete.cannotDelete.message"),
                Loc.S("mode.editor.delete.cannotDelete.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true; // Treating delete as a successful "change" that requires parent refresh
        Close();
    }

    // =========================================================================
    // VOCABULARY & MODEL WARNINGS
    // =========================================================================

    /// <summary>
    /// Updates vocabulary warning visibility based on selected provider/model.
    /// Shows warning for providers that don't support custom vocabulary.
    /// </summary>
    private void UpdateVocabularyWarning()
    {
        bool showWarning = false;
        string warningText = Loc.S("mode.editor.warning.vocabularyUnsupported");

        var providerType = SelectedProviderType();

        if (providerType == "local")
        {
            var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var (engine, _) = ParseModelTag(tag);
            if (engine == "parakeet")
            {
                showWarning = true;
                warningText = Loc.S("mode.editor.warning.vocabularyUnsupported.parakeet");
            }
        }
        else if (providerType == "cloud")
        {
            // HyperWhisper Cloud is detected from the SOURCE segment, not the BYOK
            // provider combo — that combo no longer carries a "hyperwhisper" entry
            // (HW Cloud has its own source segment). When the HW Cloud source is
            // active the BYOK combo is irrelevant, so don't read it.
            var isHyperWhisperCloud = SelectedTranscriptionSource() == "hwcloud";
            var cloudProvider = isHyperWhisperCloud
                ? "hyperwhisper"
                : (CloudProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var cloudModel = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var cloudAccuracyTier = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            // Standalone Grok BYOK: keyterm not plumbed through backend.
            // HW Cloud accuracy tiers: catalog flags whether the chosen tier +
            // model supports custom vocabulary (Grok SST and Chirp 3 do not;
            // ElevenLabs scribe_v1 doesn't while scribe_v2 does). Prefer the
            // per-model flag so the warning matches the send-path gate. If the
            // tier string is null/empty (initial render, catalog load failure)
            // suppress the warning rather than guess.
            var hwCloudModelId = (CloudTierModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var hwCatalog = Services.AppClassification.CloudSttCatalog.Shared;
            var hwModelKnown = isHyperWhisperCloud
                && !string.IsNullOrEmpty(cloudAccuracyTier)
                && !string.IsNullOrEmpty(hwCloudModelId)
                && hwCatalog.GetModel(cloudAccuracyTier, hwCloudModelId) != null;
            var cloudTierUnsupported = isHyperWhisperCloud
                && !string.IsNullOrEmpty(cloudAccuracyTier)
                && (hwModelKnown
                    ? !hwCatalog.ModelSupportsCustomVocabulary(cloudAccuracyTier, hwCloudModelId)
                    : !hwCatalog.SupportsCustomVocabulary(cloudAccuracyTier));
            if (cloudProvider == "grok" || cloudTierUnsupported)
            {
                showWarning = true;
            }
            else if (cloudProvider == "elevenlabs" && cloudModel == "scribe_v1")
            {
                showWarning = true;
                warningText = Loc.S("mode.editor.warning.vocabularyUnsupported.elevenlabs");
            }
            else if (cloudProvider == "mistral")
            {
                showWarning = true;
                warningText = Loc.S("mode.editor.warning.vocabularyUnsupported.mistral");
            }
            // Deepgram base and whisper models don't support vocabulary
            else if (cloudProvider == "deepgram")
            {
                if (cloudModel?.StartsWith("base") == true)
                {
                    showWarning = true;
                    warningText = Loc.S("mode.editor.warning.vocabularyUnsupported.deepgramBase");
                }
                else if (cloudModel?.StartsWith("whisper") == true)
                {
                    showWarning = true;
                    warningText = Loc.S("mode.editor.warning.vocabularyUnsupported.deepgramWhisper");
                }
            }
        }

        VocabularyWarning.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
        VocabularyWarningText.Text = warningText;
    }

    /// <summary>
    /// Updates Nova-3 auto-detect warning visibility.
    /// Shows warning when using Deepgram Nova models with auto-detect language.
    /// </summary>
    private void UpdateNova3Warning()
    {
        var providerType = SelectedProviderType();
        // HyperWhisper Cloud is detected from the SOURCE segment, not the BYOK
        // provider combo — that combo no longer carries a "hyperwhisper" entry,
        // so the Nova-3 tier check below would never fire if read from it.
        var isHyperWhisperCloud = SelectedTranscriptionSource() == "hwcloud";
        var cloudProvider = isHyperWhisperCloud
            ? "hyperwhisper"
            : (CloudProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var cloudModel = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        bool show = providerType == "cloud"
            && ((cloudProvider == "deepgram" && cloudModel?.Contains("nova") == true)
                || (isHyperWhisperCloud && (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "deepgramNova3"))
            && (language == "auto" || string.IsNullOrEmpty(language));

        Nova3Warning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Updates all provider/model warnings.
    /// </summary>
    private void UpdateAllWarnings()
    {
        UpdateVocabularyWarning();
        UpdateParakeetLanguageWarning();
        UpdateNova3Warning();
    }

    /// <summary>
    /// Auto-selects English language for English-only models.
    /// Also disables language dropdown for English-only cloud models.
    /// </summary>
    private void AutoSelectEnglishForModel()
    {
        var providerType = SelectedProviderType();
        bool isEnglishOnly = false;

        if (providerType == "local")
        {
            var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var (engine, modelId) = ParseModelTag(tag);

            if (engine == "parakeet")
            {
                var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
                if (model?.IsEnglishOnly == true)
                    isEnglishOnly = true;
            }
            else if (modelId?.EndsWith(".en") == true)
            {
                isEnglishOnly = true;
            }
        }

        if (isEnglishOnly)
        {
            SelectLanguage("en");
            LanguageCombo.IsEnabled = false;
            LanguagePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            LanguageCombo.IsEnabled = true;
            LanguagePanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Filters the language dropdown based on the selected model.
    /// Parakeet v3 shows only its supported languages + Automatic.
    /// Whisper / Cloud restores the full 101-language list.
    /// English-only models are handled separately by AutoSelectEnglishForModel.
    /// </summary>
    private void UpdateLanguagesForSelectedModel()
    {
        var providerType = SelectedProviderType();

        if (providerType == "local")
        {
            var tag = (LocalModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var (engine, modelId) = ParseModelTag(tag);

            if (engine == "parakeet")
            {
                var model = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
                if (model != null && !model.IsEnglishOnly)
                {
                    // Multilingual Parakeet: filter to supported languages
                    var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                    var supportedCodes = model.SupportedLanguages;

                    LanguageCombo.Items.Clear();
                    foreach (var lang in LanguageInfo.AllLanguages)
                    {
                        if (lang.Code == "auto" || supportedCodes.Contains(lang.Code, StringComparer.OrdinalIgnoreCase))
                        {
                            LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                        }
                    }

                    // Preserve current selection if still valid, otherwise fall back to "auto"
                    bool found = false;
                    if (!string.IsNullOrEmpty(currentLang))
                    {
                        foreach (ComboBoxItem item in LanguageCombo.Items)
                        {
                            if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                        }
                    }
                    if (!found) SelectLanguage("auto");
                    return;
                }
                // English-only Parakeet: language picker is disabled by AutoSelectEnglishForModel,
                // but keep the full list so switching back to a multilingual model works
            }
        }

        if (providerType == "cloud")
        {
            // HyperWhisper Cloud is a tier wrapper: resolve the routed upstream
            // provider + model so the same per-provider language filtering the
            // BYOK branches use also runs for the cloud tier; otherwise the picker
            // shows the full list and lets the user save a language the routed
            // model can't handle. `isHyperWhisperCloud` selects the catalog-driven
            // fallback filter below for tiers without a dedicated branch here.
            // HyperWhisper Cloud is detected from the SOURCE segment, not the BYOK
            // provider combo — that combo no longer carries a "hyperwhisper" entry
            // (HW Cloud has its own source segment), so reading its tag would always
            // miss and skip every tier-specific filter below.
            var isHyperWhisperCloud = SelectedTranscriptionSource() == "hwcloud";
            ResolveEffectiveCloudProviderAndModel(out var cloudProvider, out var effectiveModelId);

            if (cloudProvider == CloudTranscriptionProvider.Soniox)
            {
                var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                var supportedCodes = new HashSet<string>(LanguageInfo.SonioxAsyncV4LanguageCodes, StringComparer.OrdinalIgnoreCase);

                LanguageCombo.Items.Clear();
                foreach (var lang in LanguageInfo.AllLanguages)
                {
                    if (supportedCodes.Contains(lang.Code))
                    {
                        LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                    }
                }

                bool found = false;
                if (!string.IsNullOrEmpty(currentLang))
                {
                    foreach (ComboBoxItem item in LanguageCombo.Items)
                    {
                        if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) SelectLanguage("auto");
                return;
            }

            if (cloudProvider == CloudTranscriptionProvider.AssemblyAI)
            {
                var modelId = effectiveModelId;

                // For the HyperWhisper Cloud AssemblyAI tier, Medical Mode is the
                // separate MedicalDomainCheck (X-STT-Domain), not a "-medical"
                // model id like the BYOK variants. Fold the checked domain into the
                // effective model id so the medical-language restriction below fires
                // for both encodings. Gate on isHyperWhisperCloud (mirrors macOS's
                // currentCloudProvider == .hyperwhisper guard): the checkbox state
                // leaks when the outer provider is switched to BYOK "assemblyai"
                // (the BYOK panel collapses but doesn't hide/uncheck it), so without
                // this gate BYOK AssemblyAI would be wrongly clamped to the 5
                // medical languages.
                if (isHyperWhisperCloud
                    && MedicalDomainCheck.Visibility == Visibility.Visible
                    && MedicalDomainCheck.IsChecked == true
                    && !modelId.EndsWith("-medical", StringComparison.Ordinal))
                {
                    modelId += "-medical";
                }

                string[]? filteredCodes = null;

                // Medical Mode add-on only supports EN/ES/DE/FR — for other
                // languages AssemblyAI silently skips the domain correction.
                if (modelId.EndsWith("-medical", StringComparison.Ordinal))
                {
                    filteredCodes = new[] { "auto", "en", "es", "de", "fr" };
                }
                else if (modelId.Equals("universal-3-pro", StringComparison.Ordinal))
                {
                    filteredCodes = new[] { "auto", "en", "es", "de", "fr", "pt", "it" };
                }

                if (filteredCodes != null)
                {
                    var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                    var supportedCodes = new HashSet<string>(filteredCodes, StringComparer.OrdinalIgnoreCase);

                    LanguageCombo.Items.Clear();
                    foreach (var lang in LanguageInfo.AllLanguages)
                    {
                        if (supportedCodes.Contains(lang.Code))
                        {
                            LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                        }
                    }

                    bool found = false;
                    if (!string.IsNullOrEmpty(currentLang))
                    {
                        foreach (ComboBoxItem item in LanguageCombo.Items)
                        {
                            if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                        }
                    }
                    if (!found) SelectLanguage("auto");
                    return;
                }
            }

            if (cloudProvider == CloudTranscriptionProvider.Grok)
            {
                var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

                LanguageCombo.Items.Clear();
                foreach (var lang in LanguageInfo.AllLanguages)
                {
                    if (lang.Code == "auto" || GrokSttService.TryGetSupportedFormattingLanguageCode(lang.Code, out _))
                    {
                        LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                    }
                }

                bool found = false;
                if (!string.IsNullOrEmpty(currentLang))
                {
                    foreach (ComboBoxItem item in LanguageCombo.Items)
                    {
                        if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) SelectLanguage("auto");
                return;
            }

            // HyperWhisper Cloud Deepgram tier: the medical models
            // (nova-3-medical / nova-2-medical) are ENGLISH-ONLY (vendor-confirmed),
            // unlike the broad language set of the general models. The catalog-codes
            // fallback below keys on the TIER, not the model, so restrict to English
            // here before it runs. Gated on isHyperWhisperCloud (mirrors the
            // AssemblyAI-medical branch) so a leaked picker state can't clamp a BYOK
            // Deepgram selection.
            if (isHyperWhisperCloud
                && cloudProvider == CloudTranscriptionProvider.Deepgram
                && effectiveModelId.EndsWith("-medical", StringComparison.Ordinal))
            {
                var allowedMedical = new HashSet<string>(new[] { "auto", "en" }, StringComparer.OrdinalIgnoreCase);
                var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

                LanguageCombo.Items.Clear();
                foreach (var lang in LanguageInfo.AllLanguages)
                {
                    if (allowedMedical.Contains(lang.Code))
                    {
                        LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                    }
                }

                bool found = false;
                if (!string.IsNullOrEmpty(currentLang))
                {
                    foreach (ComboBoxItem item in LanguageCombo.Items)
                    {
                        if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) SelectLanguage("auto");
                return;
            }

            // HyperWhisper Cloud tiers without a dedicated branch above
            // (Deepgram, OpenAI, ElevenLabs, …): filter to the routed tier's
            // catalog-declared languages. The catalog stores upstream-native codes
            // in mixed formats (BCP-47 en-AU, ISO-639-2/3 eng, region variants
            // ar-AE), so we normalize to the picker's two-letter base via
            // PickerLanguageCodesForId before intersecting — intersecting the raw
            // codes would collapse e.g. ElevenLabs/Chirp to a 2-item list. Unknown
            // ("unverified") sets return null and fall through to the full list.
            if (isHyperWhisperCloud)
            {
                var tierId = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                var allowed = Services.AppClassification.CloudSttCatalog.Shared.PickerLanguageCodesForId(tierId);
                if (allowed is { Count: > 0 })
                {
                    var filtered = new List<LanguageInfo>();
                    foreach (var lang in LanguageInfo.AllLanguages)
                    {
                        if (allowed.Contains(lang.Code)) filtered.Add(lang);
                    }

                    // Safety net: never show a near-empty picker. If normalization
                    // collapsed the set to ~just "auto" (a malformed/unmappable
                    // catalog entry), fall through to the full list — matching the
                    // macOS "miss → full list" semantics — rather than regress.
                    if (filtered.Count > 2)
                    {
                        var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

                        LanguageCombo.Items.Clear();
                        foreach (var lang in filtered)
                        {
                            LanguageCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Code });
                        }

                        bool found = false;
                        if (!string.IsNullOrEmpty(currentLang))
                        {
                            foreach (ComboBoxItem item in LanguageCombo.Items)
                            {
                                if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                            }
                        }
                        if (!found) SelectLanguage("auto");
                        return;
                    }
                }
            }
        }

        // Whisper or Cloud: restore full language list if it was filtered
        if (LanguageCombo.Items.Count < LanguageInfo.AllLanguages.Length)
        {
            var currentLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            LoadLanguages();

            bool found = false;
            if (!string.IsNullOrEmpty(currentLang))
            {
                foreach (ComboBoxItem item in LanguageCombo.Items)
                {
                    if (item.Tag?.ToString() == currentLang) { LanguageCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found && LanguageCombo.Items.Count > 0) LanguageCombo.SelectedIndex = 0;
        }
    }

    private void SelectLanguage(string languageCode)
    {
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (item.Tag?.ToString() == languageCode)
            {
                LanguageCombo.SelectedItem = item;
                break;
            }
        }
    }
}
