using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

// The mode EDITOR field bag: the persisted fields, their load from a Mode, and the Cancel snapshot.
public sealed partial class ModesViewModel
{
    private string _name = string.Empty;
    private string _language = "en";
    private bool _localPostProcessingEnabled;
    private string _localPostProcessingModel = string.Empty;
    private string _userSystemPrompt = string.Empty;
    private string _providerType = "local";
    private string _localEngine = "whisper";
    private string _transcriptionModel = "base";
    /// <summary>The provider the editor offers when a local mode is switched to cloud.</summary>
    private const string DefaultByokCloudProvider = "elevenlabs";
    private string _cloudProvider = DefaultByokCloudProvider;
    private string _cloudAccuracyTier = "elevenLabsScribeV2";
    private string _cloudDomain = string.Empty;
    private string _geminiPrompt = string.Empty;
    private string _customVocabulary = string.Empty;
    private bool _enableScreenOcr;
    private string _postProcessingMode = "off";
    private string _postProcessingProvider = "openai";
    private string _postProcessingModel = "gpt-5.6-luna";
    private string _hyperWhisperCloudModel = "anthropic:claude-haiku-5-5";
    private string _customInstructions = string.Empty;
    private bool _punctuation = true;
    private bool _capitalization = true;
    private bool _profanityFilter;
    private bool _removeTrailingPeriod;
    private string _englishSpelling = string.Empty;

    // The Windows editor keeps a preset on the entity and reveals Custom Instructions only for
    // "custom" (ModeEditorWindow.xaml.cs:1138). Linux used to show the box unconditionally and
    // never round-tripped Mode.Preset at all, so an edit silently reset a mode to "hyper".
    private string _preset = "hyper";
    // "Persistent Instructions" is a checkbox on Windows that reveals the prompt box
    // (UserPromptCheck → UserPromptPanel). It is not persisted; it starts checked when the mode
    // already carries a prompt.
    private bool _userPromptEnabled;
    // Windows builds a brand-new Mode for the create dialog and leaves ModeService's selection
    // alone, so the grid keeps its accent border and the status bar keeps its mode name. Linux
    // used to null Selected instead, which dropped the active mode the instant the dialog opened
    // and never put it back on Cancel. The flag separates "editing a new mode" from "nothing is
    // selected" so Save can still tell an insert from an update.
    private bool _isCreating;

    private void LoadEditorFrom(Mode value)
    {
        // Windows holds the same flag up for the whole of its own editor load
        // (ModeEditorWindow.xaml.cs:19, ModeEditorWindow.Load.cs:15/:382) and every combo handler
        // returns early on it (xaml.cs:615): a vendor assignment made BY the load is not a vendor
        // change made by the user, and must not move the model selection off the value this mode
        // was saved with. NormalizeCloudModel is the only reader here.
        _loadingEditor = true;
        try
        {
            Name = value.Name;
            Language = value.Language;
            Preset = string.IsNullOrWhiteSpace(value.Preset) ? "hyper" : value.Preset;
            LocalPostProcessingEnabled = value.PostProcessingMode == 2
                && string.Equals(value.PostProcessingProvider, "local_llm", StringComparison.OrdinalIgnoreCase);
            LocalPostProcessingModel = value.LocalPostProcessingModel ?? string.Empty;
            UserSystemPrompt = value.UserSystemPrompt ?? string.Empty;
            ProviderType = value.ProviderType ?? "local";
            LocalEngine = value.LocalEngine;
            TranscriptionModel = value.ProviderType == "cloud" ? value.CloudTranscriptionModel ?? string.Empty : value.LocalEngine == "parakeet" ? value.LocalParakeetModel ?? "parakeet-v3" : value.ModelType ?? value.Model ?? "base";
            // A CLOUD mode with no stored provider IS a HyperWhisper Cloud mode (issue #1709), so
            // the editor opens on the provider the card shows and the router runs. A local mode
            // switched to cloud keeps the editor's own BYOK default.
            var isCloudMode = string.Equals(value.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase);
            var loadedProvider = isCloudMode
                ? SharedCoreBridge.EffectiveModeCloudProvider(value.CloudProvider)
                : value.CloudProvider ?? DefaultByokCloudProvider;
            var loadedTier = value.CloudAccuracyTier;
            // Issue #601: a retired standalone provider (`googlespeech`, `microsoftazurespeech`)
            // is no longer in CloudProviders, so loading it verbatim would leave the vendor combo
            // with no selection over a value SaveAsync then refuses. Fold it here exactly as the
            // startup pass does (provider onto HyperWhisper Cloud, tier only if blank/default),
            // so the editor opens on the HyperWhisper Cloud segment the mode will be saved as.
            // The stored row is not written until the user saves.
            if (LegacyCloudProviderFold.TryFold(loadedProvider, loadedTier, out var foldedProvider, out var foldedTier))
            {
                loadedProvider = foldedProvider;
                loadedTier = foldedTier;
            }
            CloudProvider = loadedProvider;
            // Canonicalise on load, not just on save: a mode persisted by an older
            // build (or restored from a backup) can carry a retired tier id such as
            // `googleChirp3`. The shared core resolves it through the catalog's
            // `migrateFrom` list, so the editor opens on the tier the user actually
            // has (geminiTranscribe) instead of an id that is no longer offered.
            CloudAccuracyTier = SharedCoreBridge.CanonicalCloudSttTier(loadedTier);
            CloudDomain = value.CloudTranscriptionDomain ?? string.Empty;
            GeminiPrompt = value.GeminiCustomPrompt ?? string.Empty;
            CustomVocabulary = string.Join(", ", value.CustomVocabulary ?? []);
            EnableScreenOcr = value.EnableScreenOCR;
            PostProcessingMode = value.PostProcessingMode switch { 1 => "cloud", 2 => "local", _ => "off" };
            PostProcessingProvider = value.PostProcessingProvider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true
                ? "custom" : value.PostProcessingProvider ?? "openai";
            PostProcessingModel = value.LanguageModel ?? string.Empty;
            // Canonicalise on load: a mode saved before 2026-10 stores
            // "anthropic:claude-haiku-4-5", which the picker no longer lists. Map it to the
            // provider's default (Haiku 5.5), as Windows FromString does, so the picker opens
            // on a row instead of blank.
            HyperWhisperCloudModel = HyperWhisper.ModelReadiness.CloudPostProcessingCatalog.Canonicalize(value.CloudPostProcessingModel);
            CustomInstructions = value.CustomInstructions ?? string.Empty;
            Punctuation = value.Punctuation; Capitalization = value.Capitalization;
            ProfanityFilter = value.ProfanityFilter; RemoveTrailingPeriod = value.RemoveTrailingPeriod;
            EnglishSpelling = value.EnglishSpelling ?? string.Empty;
            UserPromptEnabled = !string.IsNullOrWhiteSpace(value.UserSystemPrompt);
            LoadCustomEndpoint(value.PostProcessingProvider);
            // AFTER the fields above, never before: the assignments each notify, every notification
            // re-reads CloudModels, and the vendor and model this mode is pinned on are only final
            // here. Windows pins at the same point, from the same persisted value
            // (ModeEditorWindow.Load.cs:200-201).
            ApplyLoadedCloudModel();
            NotifyEditorReveals();
        }
        finally { _loadingEditor = false; }
    }
    public string Name { get => _name; set { if (Set(ref _name, value)) Notify(nameof(CanSave)); } }
    public string Language
    {
        get => _language;
        set { if (Set(ref _language, value)) NotifyEditorReveals(); }
    }
    public bool LocalPostProcessingEnabled
    {
        get => _localPostProcessingEnabled;
        set
        {
            if (!Set(ref _localPostProcessingEnabled, value)) return;
            var desired = value ? "local" : _postProcessingMode == "local" ? "off" : _postProcessingMode;
            if (_postProcessingMode != desired)
            {
                _postProcessingMode = desired;
                Notify(nameof(PostProcessingMode));
            }
        }
    }
    public string LocalPostProcessingModel
    {
        get => _localPostProcessingModel;
        set { if (Set(ref _localPostProcessingModel, value)) Notify(nameof(CanSave)); }
    }
    public string UserSystemPrompt { get => _userSystemPrompt; set => Set(ref _userSystemPrompt, value); }
    public string ProviderType
    {
        get => _providerType;
        set
        {
            if (!Set(ref _providerType, value)) return;
            if (value == "cloud" && IsLocalTranscriptionModelId(_transcriptionModel)) TranscriptionModel = string.Empty;
            NormalizeLocalModel();
            NormalizeCloudModel();
            NormalizeDictationDomain();
            NotifyEditorReveals();
        }
    }
    public string LocalEngine
    {
        get => _localEngine;
        set { if (Set(ref _localEngine, value)) { NormalizeLocalModel(); NotifyEditorReveals(); } }
    }

    public string TranscriptionModel
    {
        get => _transcriptionModel;
        set { if (Set(ref _transcriptionModel, value)) { NormalizeDictationDomain(); NotifyEditorReveals(); } }
    }

    private bool _loadingEditor;

    public IReadOnlyList<string> ProviderTypes { get; } = ["local", "cloud"];
    public IReadOnlyList<string> LocalEngines { get; } = ["whisper", "parakeet"];
    public string CloudProvider
    {
        get => _cloudProvider;
        // Same shape as LocalEngine above: the list the picker draws is rebuilt lazily by the
        // getter, and the SELECTION is moved onto that list here, where the change is written.
        set { if (Set(ref _cloudProvider, value)) { NormalizeCloudModel(); NormalizeDictationDomain(); NotifyEditorReveals(); } }
    }
    public string CloudAccuracyTier
    {
        get => _cloudAccuracyTier;
        set { if (Set(ref _cloudAccuracyTier, value)) { NormalizeDictationDomain(); Notify(nameof(CloudVendor)); Notify(nameof(CloudTierModels)); NotifyEditorReveals(); } }
    }

    public bool IsDictation => _transcriptionModel == "dictation"
        && (IsHwCloudSource && _cloudAccuracyTier == "assemblyAI" || IsYourProviderSource && _cloudProvider == "assemblyai");
    public bool IsNotDictation => !IsDictation;
    // A hidden ComboBox still writes null when its list cannot represent Language.
    // Keep that selection separate from the unrestricted language input, and preserve
    // invalid loaded choices so Save can report them instead of inventing a default.
    public string? DictationLanguage
    {
        get => IsDictation && DictationLanguages.Contains(_language) ? _language : null;
        set { if (IsDictation && value is not null && DictationLanguages.Contains(value)) Language = value; }
    }

    private void NormalizeDictationDomain()
    {
        if (!_loadingEditor && IsDictation) CloudDomain = string.Empty;
    }

    public IReadOnlyList<string> DictationLanguages { get; } = ["en", "es", "de", "fr", "it", "pt", "tr", "nl", "sv", "no", "da", "fi", "hi", "vi", "he", "ur", "ko", "ca", "gl", "ru", "ro", "et", "fa", "yue", "af", "mr", "zu", "xh", "nn", "ar", "ja", "zh"];
    public string CloudDomain { get => _cloudDomain; set => Set(ref _cloudDomain, value); }
    public string GeminiPrompt { get => _geminiPrompt; set => Set(ref _geminiPrompt, value); }
    public string CustomVocabulary { get => _customVocabulary; set => Set(ref _customVocabulary, value); }
    public bool EnableScreenOcr { get => _enableScreenOcr; set => Set(ref _enableScreenOcr, value); }
    public string PostProcessingMode
    {
        get => _postProcessingMode;
        set
        {
            var normalized = value is "cloud" or "local" ? value : "off";
            if (!Set(ref _postProcessingMode, normalized)) return;
            if (_localPostProcessingEnabled != (normalized == "local"))
            {
                _localPostProcessingEnabled = normalized == "local";
                Notify(nameof(LocalPostProcessingEnabled));
            }
            NotifyEditorReveals();
        }
    }
    public string PostProcessingProvider
    {
        get => _postProcessingProvider;
        set { if (Set(ref _postProcessingProvider, value ?? "openai")) NotifyEditorReveals(); }
    }
    public string PostProcessingModel { get => _postProcessingModel; set => Set(ref _postProcessingModel, value ?? string.Empty); }
    public string HyperWhisperCloudModel { get => _hyperWhisperCloudModel; set => Set(ref _hyperWhisperCloudModel, value ?? string.Empty); }
    public string CustomInstructions { get => _customInstructions; set => Set(ref _customInstructions, value ?? string.Empty); }
    public bool Punctuation
    {
        get => _punctuation;
        set { if (Set(ref _punctuation, value)) Notify(nameof(ShowRemoveTrailingPeriod)); }
    }
    public bool Capitalization { get => _capitalization; set => Set(ref _capitalization, value); }
    public bool ProfanityFilter { get => _profanityFilter; set => Set(ref _profanityFilter, value); }
    public bool RemoveTrailingPeriod { get => _removeTrailingPeriod; set => Set(ref _removeTrailingPeriod, value); }
    public string EnglishSpelling { get => _englishSpelling; set => Set(ref _englishSpelling, value ?? string.Empty); }

    public IReadOnlyList<string> PostProcessingModes { get; } = ["off", "cloud", "local"];
    public IReadOnlyList<string> PostProcessingProviders { get; } = ["hyperwhispercloud", "openai", "anthropic", "groq", "grok", "gemini", "cerebras", "mistral", "custom"];
    // Lowercase ids, matched ordinally on save. `geminitranscribe` is the BYOK
    // Gemini 3.5 Transcribe provider, distinct from `gemini` (the multimodal
    // model) and from the `geminiTranscribe` HyperWhisper Cloud accuracy TIER.
    // `microsoftazurespeech` and `googlespeech` are NOT offered (issue #601): on this head a mode
    // saved with either could never transcribe, and both native heads fold them onto
    // HyperWhisper Cloud. A mode still carrying one is folded on load and on save.
    public IReadOnlyList<string> CloudProviders { get; } = ["openai", "groq", "elevenlabs", "mistral", "grok", "deepgram", "assemblyai", "soniox", "gemini", "geminitranscribe", "meta", "hyperwhisper"];

    /// <summary>True while the editor holds an unsaved new mode (see <see cref="_isCreating"/>).</summary>
    public bool IsCreating
    {
        get => _isCreating;
        private set { if (Set(ref _isCreating, value)) { Notify(nameof(IsEditing)); Notify(nameof(IsDefaultModeSelected)); } }
    }
    /// <summary>The inverse of <see cref="IsCreating"/>; Delete is offered only when editing.</summary>
    public bool IsEditing => !_isCreating;

    /// <summary>
    /// True when the editor is open on the one mode whose name is fixed (issue #494).
    ///
    /// The FLAG, not the name. macOS used to key the same rule on <c>name == "Default"</c>,
    /// which locked any mode a user happened to call that and stopped locking the real default
    /// as soon as a restored backup carried another name. Windows has always read IsDefault
    /// (ModeEditorWindow.xaml.cs), and Linux reads it here.
    ///
    /// A mode being created is never the default, hence the IsCreating guard: without it the
    /// create dialog would inherit the flag from whichever mode was selected behind it.
    /// </summary>
    public bool IsDefaultModeSelected => !_isCreating && _selected?.IsDefault == true;

    /// <summary>Mode.Preset, round-tripped. Drives <see cref="ShowCustomInstructions"/>.</summary>
    public string Preset
    {
        get => _preset;
        set { if (Set(ref _preset, string.IsNullOrWhiteSpace(value) ? "hyper" : value)) NotifyEditorReveals(); }
    }
    public IReadOnlyList<string> Presets { get; } = ["hyper", "message", "mail", "note", "meeting", "code", "custom"];
    /// <summary>
    /// The four variants Windows offers (ModeEditorWindow.xaml:485-488). There is deliberately no
    /// empty entry: a mode that has never chosen one keeps EnglishSpelling empty, which the
    /// shared core reads as "send no spelling instruction", and the combo simply shows nothing.
    /// </summary>
    public IReadOnlyList<string> EnglishSpellings { get; } = ["american", "british", "australian", "canadian"];

    /// <summary>
    /// A copy of every editor field, taken before the dialog opens. Windows edits a Mode entity
    /// copy and writes it only on Save, so Cancel discards; Linux binds the dialog to this live
    /// shared view model, and restoring this snapshot is what makes its Cancel mean the same.
    /// </summary>
    public sealed record ModeEditorSnapshot(
        Mode? Selected, bool IsCreating, string Name, string Language, string Preset,
        bool LocalPostProcessingEnabled, string LocalPostProcessingModel, string UserSystemPrompt,
        bool UserPromptEnabled, string ProviderType, string LocalEngine, string TranscriptionModel,
        string CloudProvider, string CloudAccuracyTier, string CloudDomain, string GeminiPrompt,
        string CustomVocabulary, bool EnableScreenOcr, string PostProcessingMode,
        string PostProcessingProvider, string PostProcessingModel, string HyperWhisperCloudModel,
        string CustomInstructions, bool Punctuation, bool Capitalization, bool ProfanityFilter,
        bool RemoveTrailingPeriod, string EnglishSpelling, string CustomEndpointName,
        string CustomEndpointUrl, string CustomEndpointModel, string CustomEndpointApiKey,
        Guid? CustomEndpointId);

    public ModeEditorSnapshot CaptureEditorState() => new(
        _selected, _isCreating, _name, _language, _preset,
        _localPostProcessingEnabled, _localPostProcessingModel, _userSystemPrompt,
        _userPromptEnabled, _providerType, _localEngine, _transcriptionModel,
        _cloudProvider, _cloudAccuracyTier, _cloudDomain, _geminiPrompt,
        _customVocabulary, _enableScreenOcr, _postProcessingMode,
        _postProcessingProvider, _postProcessingModel, _hyperWhisperCloudModel,
        _customInstructions, _punctuation, _capitalization, _profanityFilter,
        _removeTrailingPeriod, _englishSpelling, _customEndpointName,
        _customEndpointUrl, _customEndpointModel, _customEndpointApiKey, _customEndpointId);

    public void RestoreEditorState(ModeEditorSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // A Cancel puts back values the user already had; it is not a user picking a new vendor,
        // so the same flag LoadEditorFrom sets holds NormalizeCloudModel off for the whole restore.
        // Without it the CloudProvider assignment below would move the model onto the restored
        // vendor's first catalog id and the restore would discard the id it exists to put back.
        _loadingEditor = true;
        try
        {
            // Assign the backing field, not the property: the Selected setter would re-persist the
            // selected-mode id and reload every field from disk, undoing the restore it is part of.
            if (!ReferenceEquals(_selected, state.Selected))
            {
                _selected = state.Selected;
                Notify(nameof(Selected));
            }
            IsCreating = state.IsCreating;
            Name = state.Name; Language = state.Language; Preset = state.Preset;
            ProviderType = state.ProviderType; LocalEngine = state.LocalEngine;
            TranscriptionModel = state.TranscriptionModel;
            CloudProvider = state.CloudProvider; CloudAccuracyTier = state.CloudAccuracyTier;
            CloudDomain = state.CloudDomain; GeminiPrompt = state.GeminiPrompt;
            CustomVocabulary = state.CustomVocabulary; EnableScreenOcr = state.EnableScreenOcr;
            PostProcessingMode = state.PostProcessingMode;
            LocalPostProcessingEnabled = state.LocalPostProcessingEnabled;
            PostProcessingProvider = state.PostProcessingProvider;
            PostProcessingModel = state.PostProcessingModel;
            HyperWhisperCloudModel = state.HyperWhisperCloudModel;
            LocalPostProcessingModel = state.LocalPostProcessingModel;
            UserSystemPrompt = state.UserSystemPrompt; UserPromptEnabled = state.UserPromptEnabled;
            CustomInstructions = state.CustomInstructions;
            Punctuation = state.Punctuation; Capitalization = state.Capitalization;
            ProfanityFilter = state.ProfanityFilter; RemoveTrailingPeriod = state.RemoveTrailingPeriod;
            EnglishSpelling = state.EnglishSpelling;
            CustomEndpointName = state.CustomEndpointName; CustomEndpointUrl = state.CustomEndpointUrl;
            CustomEndpointModel = state.CustomEndpointModel; CustomEndpointApiKey = state.CustomEndpointApiKey;
            _customEndpointId = state.CustomEndpointId;
            // Cancel must put the BYOK list back too, not just the fields: an abandoned edit can
            // have changed the vendor (which drops the pin) or assigned a local id through the
            // On-device segment. Re-derived here, after the restore, for the same reason
            // LoadEditorFrom does it last — vendor and model are only final once every field is back.
            ApplyLoadedCloudModel();
            NotifyEditorReveals();
        }
        finally { _loadingEditor = false; }
    }
}
