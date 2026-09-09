using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

public partial class ModeEditorWindow
{
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
                    // Case-INSENSITIVE on purpose. The shared core folds cloud
                    // provider ids to lowercase, and NormalizeLegacyCloudModeValues
                    // writes the folded value back on every init, so the stored id
                    // is `geminitranscribe` while this combo's XAML tag is
                    // `geminiTranscribe`. An ordinal compare found no match, fell
                    // through to SelectedIndex = 0 (OpenAI) and SAVED that —
                    // silently discarding the user's provider choice.
                    if (string.Equals(item.Tag?.ToString(), cloudProviderTag, StringComparison.OrdinalIgnoreCase))
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

                // The combo holds companies, so select the company that owns the
                // stored tier. Storage keeps the tier id — only the row is coarser.
                // With an empty catalog there are no vendor groups and the rows are
                // tagged with tier ids instead, so fall back to the tier id itself.
                var storedVendorKey = Services.AppClassification.CloudSttCatalog.Shared
                    .VendorGroupForId(accuracyTierValue)?.VendorKey ?? accuracyTierValue;
                foreach (ComboBoxItem item in CloudAccuracyCombo.Items)
                {
                    if (string.Equals(item.Tag?.ToString(), storedVendorKey, StringComparison.OrdinalIgnoreCase))
                    {
                        CloudAccuracyCombo.SelectedItem = item;
                        break;
                    }
                }
                if (CloudAccuracyCombo.SelectedIndex == -1 && CloudAccuracyCombo.Items.Count > 0)
                {
                    // Default to Deepgram via named lookup — a positional index
                    // breaks silently if the catalog order changes.
                    var deepgramVendorKey = Services.AppClassification.CloudSttCatalog.Shared
                        .VendorGroupForId("deepgramNova3")?.VendorKey ?? "deepgramNova3";
                    CloudAccuracyCombo.SelectedItem = CloudAccuracyCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(
                            (string?)item.Tag, deepgramVendorKey, StringComparison.OrdinalIgnoreCase))
                        ?? CloudAccuracyCombo.Items[0];
                }

                // Load the saved model (empty/null → the PERSISTED tier's catalog
                // default, not the company's) and the saved domain (medical) for
                // the tier that model resolves to.
                var selectedVendorKey = (CloudAccuracyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                LoadCloudTierModels(
                    selectedVendorKey,
                    preferredModelId: mode.CloudTranscriptionModel,
                    persistedTierId: accuracyTierValue);
                var resolvedTierId = SelectedCloudTierId();

                var savedDomainIsMedical = string.Equals(mode.CloudTranscriptionDomain, "medical", StringComparison.OrdinalIgnoreCase);
                ApplyMedicalDomainVisibility(resolvedTierId, isCheckedFromStorage: savedDomainIsMedical);
                MedicalDomainCheck.IsChecked = savedDomainIsMedical
                    && string.Equals(resolvedTierId, "assemblyAI", StringComparison.OrdinalIgnoreCase);

                UpdateCloudAccuracyDescription();
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
                SelectEnglishSpelling(mode.EnglishSpelling);

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

                SelectEnglishSpelling(mode.EnglishSpelling);
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

}
