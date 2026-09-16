using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class ModesViewModel : ViewModelBase
{
    private readonly ModeRepository _repository;
    private readonly PortableSettingsService? _settings;
    private readonly ICredentialStore? _credentials;
    private Mode? _selected;
    private string _name = string.Empty;
    private string _language = "en";
    private bool _localPostProcessingEnabled;
    private string _localPostProcessingModel = string.Empty;
    private string _userSystemPrompt = string.Empty;
    private string _providerType = "local";
    private string _localEngine = "whisper";
    private string _transcriptionModel = "base";
    private string _cloudProvider = "elevenlabs";
    private string _cloudAccuracyTier = "elevenLabsScribeV2";
    private string _cloudDomain = string.Empty;
    private string _geminiPrompt = string.Empty;
    private string _customVocabulary = string.Empty;
    private bool _enableScreenOcr;
    private string _postProcessingMode = "off";
    private string _postProcessingProvider = "openai";
    private string _postProcessingModel = "gpt-4.1-mini";
    private string _hyperWhisperCloudModel = "anthropic:claude-haiku-4-5";
    private string _customInstructions = string.Empty;
    private bool _punctuation = true;
    private bool _capitalization = true;
    private bool _profanityFilter;
    private bool _removeTrailingPeriod;
    private string _englishSpelling = string.Empty;
    private string _customEndpointName = string.Empty;
    private string _customEndpointUrl = string.Empty;
    private string _customEndpointModel = string.Empty;
    private string _customEndpointApiKey = string.Empty;
    private Guid? _customEndpointId;
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
    public ModesViewModel(
        ModeRepository repository,
        PortableSettingsService? settings = null,
        ICredentialStore? credentials = null)
    {
        _repository = repository;
        _settings = settings;
        _credentials = credentials;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        NewCommand = new AsyncCommand(_ =>
        {
            // Deliberately does NOT touch Selected: Windows' create dialog leaves the active
            // mode alone. IsCreating is what makes Save insert instead of overwrite.
            IsCreating = true;
            // The seed values are Windows' own create-mode defaults (ModeEditorWindow.xaml.cs:
            // 41-58): a new mode opens on HyperWhisper Cloud with auto language and cloud
            // post-processing already on, NOT on a local Whisper model with post-processing off.
            // Linux used to seed the latter, so the create dialog opened on a different segment
            // and a different card state than the same click gives on Windows.
            Name = string.Empty;
            Language = "auto";
            LocalPostProcessingEnabled = false;
            LocalPostProcessingModel = string.Empty;
            UserSystemPrompt = string.Empty;
            ProviderType = "cloud"; LocalEngine = "whisper"; TranscriptionModel = "base";
            CloudProvider = "hyperwhisper"; CloudAccuracyTier = "elevenLabsScribeV2"; CloudDomain = string.Empty;
            GeminiPrompt = string.Empty; CustomVocabulary = string.Empty; EnableScreenOcr = false;
            PostProcessingMode = "cloud"; PostProcessingProvider = "hyperwhispercloud";
            PostProcessingModel = "gpt-4.1-mini"; HyperWhisperCloudModel = "anthropic:claude-haiku-4-5";
            CustomInstructions = string.Empty; Punctuation = true; Capitalization = true;
            ProfanityFilter = false; RemoveTrailingPeriod = false; EnglishSpelling = string.Empty;
            Preset = "hyper"; UserPromptEnabled = false;
            ClearCustomEndpointEditor();
            NotifyEditorReveals();
            return Task.CompletedTask;
        });
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
    }
    public ObservableCollection<Mode> Items { get; } = new();
    public Mode? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            PersistSelectedMode(value.Id);
            IsCreating = false;
            LoadEditorFrom(value);
            Notify(nameof(IsDefaultModeSelected));
        }
    }

    /// <summary>
    /// Copies a mode's persisted state into the editor fields. Split out of the
    /// <see cref="Selected"/> setter so the mode editor can reload the same mode after a Cancel:
    /// the setter short-circuits when the value has not changed, which is exactly why abandoned
    /// edits used to survive re-opening the dialog on the same mode.
    /// </summary>
    public void ReloadSelected()
    {
        IsCreating = false;
        if (_selected is { } mode) LoadEditorFrom(mode);
    }

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
            CloudProvider = value.CloudProvider ?? "elevenlabs";
            // Canonicalise on load, not just on save: a mode persisted by an older
            // build (or restored from a backup) can carry a retired tier id such as
            // `googleChirp3`. The shared core resolves it through the catalog's
            // `migrateFrom` list, so the editor opens on the tier the user actually
            // has (geminiTranscribe) instead of an id that is no longer offered.
            CloudAccuracyTier = SharedCoreBridge.CanonicalCloudSttTier(value.CloudAccuracyTier);
            CloudDomain = value.CloudTranscriptionDomain ?? string.Empty;
            GeminiPrompt = value.GeminiCustomPrompt ?? string.Empty;
            CustomVocabulary = string.Join(", ", value.CustomVocabulary ?? []);
            EnableScreenOcr = value.EnableScreenOCR;
            PostProcessingMode = value.PostProcessingMode switch { 1 => "cloud", 2 => "local", _ => "off" };
            PostProcessingProvider = value.PostProcessingProvider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) == true
                ? "custom" : value.PostProcessingProvider ?? "openai";
            PostProcessingModel = value.LanguageModel ?? string.Empty;
            HyperWhisperCloudModel = value.CloudPostProcessingModel;
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
            NotifyEditorReveals();
        }
    }
    public string LocalEngine
    {
        get => _localEngine;
        set { if (Set(ref _localEngine, value)) { NormalizeLocalModel(); NotifyEditorReveals(); } }
    }

    private ManagedModelKind LocalModelKind => string.Equals(_localEngine, "parakeet", StringComparison.Ordinal)
        ? ManagedModelKind.Parakeet : ManagedModelKind.Whisper;

    /// <summary>
    /// The model ids the chosen on-device engine can actually run. Windows fills its single
    /// LocalModelCombo (ModeEditorWindow.xaml:104) from the same catalog, so the card offers a
    /// pick list rather than a free-text box.
    /// </summary>
    /// One cached list per engine.
    ///
    /// This used to build a NEW list on every read, and a single engine change notifies it twice
    /// (NormalizeLocalModel assigns TranscriptionModel, whose setter notifies again). Avalonia
    /// clears SelectedItem whenever ItemsSource is a different instance, so the model combo went
    /// blank while the value underneath stayed correct. The list depends only on the engine, so
    /// the same instance is handed back until the engine changes.
    public IReadOnlyList<string> LocalModels
    {
        get
        {
            var kind = LocalModelKind;
            if (_localModels is null || _localModelsKind != kind)
            {
                _localModelsKind = kind;
                _localModels = [.. PortableModelCatalog.All.Where(model => model.Kind == kind).Select(model => model.Id)];
            }
            return _localModels;
        }
    }

    private IReadOnlyList<string>? _localModels;
    private ManagedModelKind? _localModelsKind;

    /// <summary>
    /// Mirrors the guard above. Switching TO cloud already drops a local model id; without the
    /// reverse a cloud tier such as `scribe_v2` survived a switch to On-device and sat in the
    /// local model field, where it is not a catalog entry and silently blocks CanSave. Windows
    /// cannot show that state at all because its combo only lists entries for the engine.
    /// </summary>
    private void NormalizeLocalModel()
    {
        if (!IsOnDeviceSource) return;
        var kind = LocalModelKind;
        if (PortableModelCatalog.All.Any(model => model.Kind == kind
            && string.Equals(model.Id, _transcriptionModel, StringComparison.Ordinal))) return;
        TranscriptionModel = PortableModelCatalog.All.First(model => model.Kind == kind).Id;
    }

    /// <summary>
    /// True when <paramref name="id"/> names an on-device (Whisper or Parakeet) model.
    ///
    /// ONE predicate with three callers: the <see cref="ProviderType"/> setter drops such an id on
    /// the way to cloud, <see cref="ApplyLoadedCloudModel"/> refuses to pin one into the BYOK
    /// dropdown, and <c>SaveAsync</c> refuses to write one into the cloud column. The three used to
    /// be inline copies of the same catalog query, and they disagreed: only SaveAsync trimmed. A
    /// mode POSTed to the Local API with <c>"cloudTranscriptionModel": " base "</c> — no write path
    /// trims that column — therefore escaped the other two, was offered as a BYOK model, and then
    /// saved back as <c>null</c> with no error, which is the exact fault the guards exist to stop.
    /// The trim lives here so every caller gets it.
    /// </summary>
    private static bool IsLocalTranscriptionModelId(string? id)
    {
        var trimmed = (id ?? string.Empty).Trim();
        return trimmed.Length > 0 && PortableModelCatalog.All.Any(model =>
            model.Kind is ManagedModelKind.Whisper or ManagedModelKind.Parakeet
            && string.Equals(model.Id, trimmed, StringComparison.Ordinal));
    }

    /// <summary>
    /// The model ids a BYOK vendor offers, in catalog order — what the Cloud Model combo lists.
    ///
    /// <see cref="CloudProviders"/> holds the ids a mode is PERSISTED with; the shared cloud STT
    /// catalog keys its vendors on <c>sttProvider</c>, and the two spellings differ for Gemini
    /// Transcribe (<c>geminitranscribe</c> here, <c>gemini-transcribe</c> there). Without the alias
    /// that shipping BYOK vendor (LinuxLiveStreamingAdapters.cs:76) drew an EMPTY dropdown. The
    /// same pair of spellings is already reconciled at ProviderAssets.cs:58/:62 and
    /// SettingsViewModel.cs:590; this adds no model id and changes no catalog.
    ///
    /// Three vendor ids still return nothing, and correctly so: <c>grok</c>'s single catalog model
    /// carries an empty id, because the model is implicit in an xAI request; <c>microsoftazurespeech</c>
    /// resolves to <c>azure-mai</c>, which the catalog does not mark <c>byokEligible</c>; and
    /// <c>googlespeech</c> has no catalog vendor at all. <see cref="NormalizeCloudModel"/> empties
    /// the selection to match, so Save writes null — which is what an implicit model means.
    /// </summary>
    private static IReadOnlyList<string> CloudVendorModelIds(string vendor)
        => [.. HyperWhisper.ModelReadiness.CloudSttModelCatalog
            .ForProvider(string.Equals(vendor, "geminitranscribe", StringComparison.OrdinalIgnoreCase)
                ? "gemini-transcribe" : vendor)
            .Select(entry => entry.Value)];

    /// <summary>
    /// Mirrors <see cref="NormalizeLocalModel"/> for the BYOK picker: once a control OTHER than the
    /// model combo has committed — the vendor combo, or the transcription source segment — the
    /// selected model must be one the vendor now in force actually offers.
    ///
    /// This is the second half of issue #645. The first half made <see cref="CloudModels"/> a pure
    /// read that never shrinks under the MODEL combo's own commit. That left the VENDOR combo
    /// handing the list a wholly new set of ids while <c>_transcriptionModel</c> still held the old
    /// vendor's: Avalonia cleared a SelectedItem that was no longer in ItemsSource, the
    /// <see cref="CloudTranscriptionModel"/> proxy refused the null write-back, so the field read
    /// blank while the value underneath survived — and SaveAsync, which strips only LOCAL ids, then
    /// wrote the old vendor's model id under the new vendor with a `Mode saved` status. Every
    /// dictation on that mode afterwards posts one vendor's model id to another vendor.
    ///
    /// Moving the selection is a WRITE, so it lives here and at the two setters that call it, never
    /// in the <see cref="CloudModels"/> getter. Windows does the same thing from the same two
    /// places: a provider change (ModeEditorWindow.xaml.cs:615-617 → :724) and a switch to the
    /// "your provider" segment (:1184-1186) both call LoadCloudModels with NO preferred id, which
    /// ends at <c>CloudModelCombo.SelectedIndex = 0</c> (:532-534).
    ///
    /// Never while the editor is loading a mode or restoring a cancelled one: there the persisted
    /// id is the whole point, and <see cref="ApplyLoadedCloudModel"/> keeps it visible instead.
    /// Windows guards the same call with its own <c>_isLoading</c> (xaml.cs:615).
    ///
    /// ORDER MATTERS, and it is the whole of the verify-round fix. The list must reach the bound
    /// ComboBox while this field still holds the OUTGOING id, never after the incoming one is in
    /// place. Measured against a real Avalonia 12.1.1 ComboBox: when ItemsSource is swapped for a
    /// different instance that does not contain the current SelectedItem, Avalonia clears the
    /// selection and the two-way binding writes that null back; the
    /// <see cref="CloudTranscriptionModel"/> proxy refuses a null (it has to — the hidden picker's
    /// combo would otherwise wipe the shared field), and the binding then RE-READS the property and
    /// records what it finds as the value it has already delivered. Assign first and that value is
    /// the incoming id, so the <c>CloudTranscriptionModel</c> notification that follows carries a
    /// value the binding believes it already published, is deduplicated, and never reaches the
    /// control: the combo stays blank over a correct field for the rest of the dialog. Notify
    /// first and the binding records the OUTGOING id, so the incoming one is a change it delivers.
    ///
    /// The field run on a real X screen read this as "Deepgram and Google Gemini go blank, the
    /// three other vendors do not", and reasonably guessed at the two panel reveals those two
    /// vendors share. It is not the reveals. Every vendor change made from a combo that HAS a
    /// selection went blank and every one made from an already-blank combo did not, so the walk
    /// alternated and the two vendors that happened to land on the failing beat were those two.
    /// Reversing the walk order moves the blanks onto ElevenLabs and Groq.
    /// </summary>
    private void NormalizeCloudModel()
    {
        if (_loadingEditor || !IsYourProviderSource) return;
        var catalog = CloudVendorModelIds(_cloudProvider);
        // OrdinalIgnoreCase, as the vendor lookup is: a mode persisted with `Whisper-1` names the
        // catalog's `whisper-1` and must not be treated as a model openai does not offer.
        if (catalog.Any(id => string.Equals(id, _transcriptionModel, StringComparison.OrdinalIgnoreCase))) return;
        // Hand the combo the new vendor's list BEFORE the new selection — see the order note above.
        // The getter is a pure read, so this only releases the cache the vendor change invalidated;
        // the CloudModels notification NotifyEditorReveals raises a moment later then finds the same
        // instance and is a no-op at the control.
        Notify(nameof(CloudModels));
        TranscriptionModel = catalog.Count > 0 ? catalog[0] : string.Empty;
    }
    public string TranscriptionModel
    {
        get => _transcriptionModel;
        set { if (Set(ref _transcriptionModel, value)) NotifyEditorReveals(); }
    }
    /// <summary>
    /// The model ids the chosen BYOK vendor offers. Windows fills the same field from a pick list
    /// (ModeEditorWindow.xaml:244, LoadCloudModels), so a raw id is never typed or shown; Linux had
    /// a free-text box here, which put the saved id on screen verbatim.
    ///
    /// A mode written by another platform, or by a newer catalog, may hold an id this build does
    /// not list at all. Linux keeps such a value visible: the id
    /// <see cref="ApplyLoadedCloudModel"/> pinned is appended after the vendor's own models.
    ///
    /// This DIVERGES from Windows on purpose, and the divergence is worth being precise about.
    /// Windows re-adds a preferred id only when it IS in the vendor's full catalog and was merely
    /// filtered out of the shortlist — a retired model such as ElevenLabs `scribe_v1`
    /// (ModeEditorWindow.xaml.cs:491-505). For an id the catalog does not contain AT ALL its
    /// `currentModel` lookup returns null and control falls through to
    /// `CloudModelCombo.SelectedIndex = 0` (:532-534, repeated at ModeEditorWindow.Load.cs:213-216),
    /// so Windows silently migrates that mode to a different model on the next save. Linux would
    /// rather show the id the user's mode is actually set to than rewrite it behind their back, so
    /// it pins instead. The two heads agree everywhere the selection is NOT the user's saved value:
    /// see <see cref="NormalizeCloudModel"/>, which is index 0, exactly as Windows does it.
    ///
    /// This getter is a pure read of the vendor and that pin (issue #645). It used to key the cache
    /// on the CURRENT model id, so clicking an entry in the combo dropped the appended id and
    /// handed back a SHORTER list in the middle of the selection commit: the model setter notifies
    /// this property, and Avalonia's selection model then read the clicked index out of the new,
    /// shorter list and took the whole process down with an ArgumentOutOfRangeException. Windows
    /// cannot hit this because a model click only redraws a description
    /// (ModeEditorWindow.xaml.cs:564-607); its combo is refilled from three places only — a
    /// provider change (:724), the Show-all checkbox (:545) and a mode load
    /// (ModeEditorWindow.Load.cs:201) — and never from the model combo's own handler.
    /// </summary>
    public IReadOnlyList<string> CloudModels
    {
        get
        {
            // Hand back the SAME list instance while the vendor and the pin are unchanged. Every
            // reveal notification re-reads this property, and a fresh list each time makes the
            // bound ComboBox drop and re-pick its selection on every keystroke elsewhere in the
            // form. The vendor is matched OrdinalIgnoreCase, as CloudSttModelCatalog keys it.
            // Compare-in-getter, like the sibling LocalModels: this is cache invalidation, and it
            // reads and writes nothing but the three cache fields.
            if (!string.Equals(_cloudModelsProvider, _cloudProvider, StringComparison.OrdinalIgnoreCase))
            {
                // A vendor change drops the pin along with the list: a pinned id belongs to the
                // vendor it was pinned for, and leaving it would offer one vendor's model under
                // another — which SaveAsync would then persist, as it strips only LOCAL ids.
                // The SELECTION is moved onto the new vendor's first id by NormalizeCloudModel,
                // from the setter that changed the vendor; a getter must never write it.
                _cloudModelsProvider = _cloudProvider;
                _cloudModelsPinnedId = null;
                _cloudModels = null;
            }
            if (_cloudModels is not null) return _cloudModels;
            var ids = CloudVendorModelIds(_cloudProvider).ToList();
            if (_cloudModelsPinnedId is { Length: > 0 } pinned) ids.Add(pinned);
            return _cloudModels = ids;
        }
    }

    /// <summary>
    /// Settles the BYOK model list on the id the editor has just taken from persisted or restored
    /// data: drops the cached list, folds the id onto the catalog's own spelling when the catalog
    /// has it, and otherwise pins it so <see cref="CloudModels"/> appends it and the user can see
    /// what their mode is set to.
    ///
    /// Called ONLY from <see cref="LoadEditorFrom"/> and <see cref="RestoreEditorState"/>, after
    /// their fields are assigned. Windows takes the mode's saved id at exactly that point too, as
    /// LoadCloudModels' preferredModelId (ModeEditorWindow.Load.cs:200-201); its other two call
    /// sites are a provider change, which passes none (ModeEditorWindow.xaml.cs:724), and the
    /// Show-all checkbox, which passes the combo's CURRENT selection (:545).
    ///
    /// Deriving the pin inside the getter instead (issue #645, review round 1) captured whatever
    /// _transcriptionModel happened to hold at the instant of a read and never released it: the
    /// OUTGOING mode's id while the next mode was still loading, the on-device default `base` while
    /// the create dialog sat between its ProviderType and TranscriptionModel assignments, and a
    /// local id such as `tiny` that NormalizeLocalModel assigned during an On-device detour the
    /// user then cancelled. Each stayed in the dropdown and could be re-selected; a foreign vendor's
    /// id then saves under the wrong vendor and a local id saves as null.
    ///
    /// NewCommand needs no call: it assigns CloudProvider="hyperwhisper", and a pin exists only for
    /// the BYOK vendor it was derived for, so the vendor change in the getter drops it.
    /// </summary>
    private void ApplyLoadedCloudModel()
    {
        _cloudModelsProvider = _cloudProvider;
        _cloudModelsPinnedId = null;
        _cloudModels = null;
        // Only the BYOK picker draws this list, so only the BYOK state may pin into it. The
        // HyperWhisper Cloud segment edits the same TranscriptionModel field through its own tier
        // model control (ShowCloudAccuracyPanel), and On-device edits it through LocalModels.
        if (!IsYourProviderSource || _transcriptionModel.Length == 0) return;
        // A local model id is never a cloud model id, whatever a mode's cloud column holds: an
        // older build persisted CloudTranscriptionModel="base", and the ProviderType guard drops
        // such an id rather than showing it. The shared predicate trims, so a value POSTed to the
        // Local API as " base " cannot slip past this and then save itself back as null.
        if (IsLocalTranscriptionModelId(_transcriptionModel)) return;
        // OrdinalIgnoreCase, as CloudSttModelCatalog keys its vendors and as Windows matches its
        // combo tags (ModeEditorWindow.xaml.cs:497, :519-523). Ordinal here listed one model twice:
        // a mode POSTed with `Whisper-1` (no write path folds that column's case) missed the
        // catalog row `whisper-1`, so it was pinned and appended, and because Label() is an Ordinal
        // dictionary the catalog row drew its display name and the pinned row drew the raw id.
        // Folding the field onto the catalog's spelling is what Windows does through
        // ResolveModelAlias plus its OrdinalIgnoreCase tag match; both callers notify right after.
        var listed = CloudVendorModelIds(_cloudProvider)
            .FirstOrDefault(id => string.Equals(id, _transcriptionModel, StringComparison.OrdinalIgnoreCase));
        if (listed is null) _cloudModelsPinnedId = _transcriptionModel;
        else _transcriptionModel = listed;
    }

    private string? _cloudModelsProvider;
    private string? _cloudModelsPinnedId;
    private IReadOnlyList<string>? _cloudModels;
    private bool _loadingEditor;

    /// <summary>
    /// The on-device and BYOK model pickers both edit TranscriptionModel, but each lists only its
    /// own catalog. Windows rebuilds one combo per panel as the panel is revealed; a XAML head that
    /// keeps both alive has a hidden combo whose ItemsSource no longer holds the selected id, and a
    /// two-way SelectedItem then writes null straight back and wipes the field. These two proxies
    /// read as null while their panel is hidden and refuse a null write, so only the visible picker
    /// can change the model.
    /// </summary>
    public string? LocalTranscriptionModel
    {
        get => IsOnDeviceSource ? _transcriptionModel : null;
        set { if (!string.IsNullOrEmpty(value)) TranscriptionModel = value; }
    }

    /// <inheritdoc cref="LocalTranscriptionModel"/>
    public string? CloudTranscriptionModel
    {
        get => IsYourProviderSource ? _transcriptionModel : null;
        set { if (!string.IsNullOrEmpty(value)) TranscriptionModel = value; }
    }

    public IReadOnlyList<string> ProviderTypes { get; } = ["local", "cloud"];
    public IReadOnlyList<string> LocalEngines { get; } = ["whisper", "parakeet"];
    public string CloudProvider
    {
        get => _cloudProvider;
        // Same shape as LocalEngine above: the list the picker draws is rebuilt lazily by the
        // getter, and the SELECTION is moved onto that list here, where the change is written.
        set { if (Set(ref _cloudProvider, value)) { NormalizeCloudModel(); NotifyEditorReveals(); } }
    }
    public string CloudAccuracyTier
    {
        get => _cloudAccuracyTier;
        set { if (Set(ref _cloudAccuracyTier, value)) NotifyEditorReveals(); }
    }
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
    public string CustomEndpointName { get => _customEndpointName; set => Set(ref _customEndpointName, value ?? string.Empty); }
    public string CustomEndpointUrl { get => _customEndpointUrl; set => Set(ref _customEndpointUrl, value ?? string.Empty); }
    public string CustomEndpointModel { get => _customEndpointModel; set => Set(ref _customEndpointModel, value ?? string.Empty); }
    public string CustomEndpointApiKey { get => _customEndpointApiKey; set => Set(ref _customEndpointApiKey, value ?? string.Empty); }
    public IReadOnlyList<string> PostProcessingModes { get; } = ["off", "cloud", "local"];
    public IReadOnlyList<string> PostProcessingProviders { get; } = ["hyperwhispercloud", "openai", "anthropic", "groq", "grok", "gemini", "cerebras", "mistral", "custom"];
    // Lowercase ids, matched ordinally on save. `geminitranscribe` is the BYOK
    // Gemini 3.5 Transcribe provider, distinct from `gemini` (the multimodal
    // model) and from the `geminiTranscribe` HyperWhisper Cloud accuracy TIER.
    public IReadOnlyList<string> CloudProviders { get; } = ["openai", "groq", "elevenlabs", "mistral", "grok", "deepgram", "assemblyai", "soniox", "gemini", "geminitranscribe", "microsoftazurespeech", "googlespeech", "meta", "hyperwhisper"];
    public IReadOnlyList<string> CloudAccuracyTiers { get; } = ["groqWhisper", "deepgramNova3", "grokStt", "azureMaiTranscribe", "geminiTranscribe", "elevenLabsScribeV2", "openaiWhisper", "gemini", "mistralVoxtral", "assemblyAI", "soniox", "metaMuse"];
    public IReadOnlyList<string> CloudDomains { get; } = ["", "medical"];

    // =====================================================================================
    // MODE EDITOR — segmented sources, conditional reveals and live Save validation.
    //
    // Windows' ModeEditorWindow.xaml.cs drives ~94 Visibility switches from code-behind. The
    // same rules live here as computed properties so the Avalonia dialog can bind IsVisible
    // straight to them. Each rule cites the Windows line it mirrors.
    // =====================================================================================

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
    /// Every HyperWhisper Cloud post-processing model, as the "provider:model" storage value the
    /// mode actually saves. Windows offers these in a pair of pickers rather than a text field
    /// (ModeEditorWindow.xaml:437-452, filled by LoadCloudPostProcessingModelsForEngine), so a
    /// Linux mode editor that typed the id by hand showed a raw "anthropic:claude-haiku-4-5" where
    /// Windows shows "Claude Haiku 4.5 (Recommended)".
    ///
    /// Read once, from the same cloud post-processing catalog Windows reads through CloudPpCatalog
    /// and the model library reads through UnifiedModelCatalog, so there is no second list to keep
    /// in step. If the core is unavailable the field still has its default value, so the picker
    /// falls back to exactly that one entry rather than going empty.
    /// </summary>
    public IReadOnlyList<string> HyperWhisperCloudModels => _hyperWhisperCloudModels
        ??= HyperWhisper.ModelReadiness.CloudPostProcessingCatalog.Entries
            .Select(entry => entry.Value)
            .ToList();

    private IReadOnlyList<string>? _hyperWhisperCloudModels;

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
        && string.Equals(_cloudAccuracyTier, "assemblyAI", StringComparison.OrdinalIgnoreCase);
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

    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Items.Clear();
            foreach (var item in await _repository.ListAsync(cancellationToken)) Items.Add(item);
            var selectedId = _settings?.Get<string>("selectedModeId");
            Selected = Guid.TryParse(selectedId, out var id)
                ? Items.FirstOrDefault(item => item.Id == id) ?? FallbackSelection()
                : FallbackSelection();
            Status.Success($"{Items.Count} mode(s)");
        }
        catch (Exception) { Status.Failure("modes.load_failed", "Could not load modes."); }
    }
    private Mode? FallbackSelection() => Items.FirstOrDefault(item => item.IsDefault)
        ?? Items.OrderBy(item => item.SortOrder).FirstOrDefault();
    private void PersistSelectedMode(Guid id)
    {
        if (_settings is null) return;
        _settings.Set("selectedModeId", id.ToString("D"));
        var saved = _settings.Save();
        if (saved.IsFailure) Status.Failure(saved.Error!.Code, "The selected mode could not be remembered on this device.");
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Status.Failure("modes.name_required", "Enter a mode name."); return; }
        if (PostProcessingMode == "local"
            && (string.IsNullOrWhiteSpace(LocalPostProcessingModel)
                || LocalPostProcessingModel != Path.GetFileName(LocalPostProcessingModel)
                || LocalPostProcessingModel.Contains('\\')
                || !LocalPostProcessingModel.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
        {
            Status.Failure("modes.local_llm_model_required", "Enter a GGUF model filename from the local LLM models directory.");
            return;
        }
        if (UserSystemPrompt.Length > 2000)
        {
            Status.Failure("modes.prompt_too_long", "The system prompt cannot exceed 2000 characters.");
            return;
        }
        if (CustomInstructions.Length > 4000)
        { Status.Failure("modes.instructions_too_long", "Custom instructions cannot exceed 4000 characters."); return; }
        if (PostProcessingMode == "cloud" && !PostProcessingProviders.Contains(PostProcessingProvider, StringComparer.Ordinal))
        { Status.Failure("modes.postprocessing_provider_invalid", "Select a supported post-processing provider."); return; }
        if (PostProcessingMode == "cloud" && PostProcessingProvider == "custom" && !ValidateCustomEndpoint()) return;
        if (GeminiPrompt.Length > 2000) { Status.Failure("modes.gemini_prompt_too_long", "The Gemini prompt cannot exceed 2000 characters."); return; }
        if (ProviderType == "cloud" && !CloudProviders.Contains(CloudProvider, StringComparer.Ordinal))
        { Status.Failure("modes.cloud_provider_required", "Select a supported cloud provider."); return; }
        if (ProviderType != "cloud")
        {
            var kind = LocalEngine == "parakeet" ? ManagedModelKind.Parakeet : ManagedModelKind.Whisper;
            if (!PortableModelCatalog.All.Any(model => model.Kind == kind && string.Equals(model.Id, TranscriptionModel, StringComparison.Ordinal)))
            { Status.Failure("modes.local_model_invalid", "Select a model ID from the local model catalog."); return; }
        }
        var customEndpointId = PostProcessingMode == "cloud" && PostProcessingProvider == "custom"
            ? _customEndpointId ?? Guid.NewGuid()
            : (Guid?)null;
        // A create dialog must insert even though Selected still points at the mode the user had
        // active — Windows keeps that selection while the create window is open.
        var mode = (_isCreating ? null : Selected) ?? new Mode { SortOrder = Items.Count };
        mode.Name = Name.Trim();
        mode.Preset = string.IsNullOrWhiteSpace(Preset) ? "hyper" : Preset;
        mode.Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();
        mode.ProviderType = ProviderType == "cloud" ? "cloud" : "local";
        mode.LocalEngine = LocalEngine == "parakeet" ? "parakeet" : "whisper";
        if (mode.ProviderType == "cloud")
        {
            mode.CloudProvider = CloudProvider;
            // A LOCAL model id must never reach the cloud column. TranscriptionModel is shared by
            // the on-device picker and the BYOK picker, and it starts at the on-device default
            // "base". The ProviderType setter drops a local id on the way to cloud, but that runs
            // only when the type actually CHANGES, so a mode that is already cloud — every mode
            // created from the HyperWhisper Cloud segment — kept "base" and was saved with
            // CloudTranscriptionModel="base". The mode card then printed a Whisper id on a cloud
            // mode, and ModeUsingModel matched it against the local catalog. A real cloud model id
            // such as "mai-1.5" is unaffected and still persists.
            var cloudModel = TranscriptionModel.Trim();
            mode.CloudTranscriptionModel = cloudModel.Length == 0 || IsLocalTranscriptionModelId(cloudModel)
                ? null : cloudModel;
            // Canonicalise BEFORE the allow-list check. The list holds catalog ids
            // only, and the comparison is ordinal, so a legacy alias (`googleChirp3`,
            // `chirp_3`, `high`, …) would otherwise miss every entry and be silently
            // rewritten to elevenLabsScribeV2 — a different vendor at different
            // credits, with no error shown.
            var canonicalTier = SharedCoreBridge.CanonicalCloudSttTier(CloudAccuracyTier);
            mode.CloudAccuracyTier = CloudAccuracyTiers.Contains(canonicalTier, StringComparer.Ordinal) ? canonicalTier : "elevenLabsScribeV2";
            mode.CloudTranscriptionDomain = CloudDomain == "medical" ? "medical" : null;
            mode.GeminiCustomPrompt = string.IsNullOrWhiteSpace(GeminiPrompt) ? null : GeminiPrompt.Trim();
        }
        else if (mode.LocalEngine == "parakeet") { mode.LocalParakeetModel = TranscriptionModel.Trim(); mode.Model = mode.LocalParakeetModel; }
        else { mode.Model = string.IsNullOrWhiteSpace(TranscriptionModel) ? "base" : TranscriptionModel.Trim(); mode.ModelType = mode.Model; }
        mode.PostProcessingMode = PostProcessingMode switch { "cloud" => 1, "local" => 2, _ => 0 };
        mode.PostProcessingProvider = mode.PostProcessingMode switch
        {
            2 => "local_llm",
            1 when customEndpointId is { } id => $"custom:{id:D}",
            1 => PostProcessingProvider,
            _ => "none",
        };
        mode.LocalPostProcessingModel = string.IsNullOrWhiteSpace(LocalPostProcessingModel)
            ? null
            : LocalPostProcessingModel.Trim();
        mode.LanguageModel = string.IsNullOrWhiteSpace(PostProcessingModel) ? null : PostProcessingModel.Trim();
        mode.CloudPostProcessingModel = string.IsNullOrWhiteSpace(HyperWhisperCloudModel)
            ? "anthropic:claude-haiku-4-5" : HyperWhisperCloudModel.Trim();
        mode.UserSystemPrompt = string.IsNullOrWhiteSpace(UserSystemPrompt) ? null : UserSystemPrompt.Trim();
        mode.CustomInstructions = string.IsNullOrWhiteSpace(CustomInstructions) ? null : CustomInstructions.Trim();
        mode.Punctuation = Punctuation; mode.Capitalization = Capitalization;
        mode.ProfanityFilter = ProfanityFilter; mode.RemoveTrailingPeriod = RemoveTrailingPeriod;
        mode.EnglishSpelling = string.IsNullOrWhiteSpace(EnglishSpelling) ? null : EnglishSpelling.Trim();
        mode.CustomVocabulary = CustomVocabulary.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        mode.EnableScreenOCR = EnableScreenOcr;
        mode.ModifiedDate = DateTime.UtcNow;
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? settingsSnapshot = null;
        byte[]? previousCredential = null;
        var credentialTouched = customEndpointId is not null && !string.IsNullOrWhiteSpace(CustomEndpointApiKey);
        try
        {
            if (customEndpointId is { } id)
            {
                settingsSnapshot = _settings!.Snapshot();
                if (credentialTouched)
                {
                    var previous = _credentials!.Read("HyperWhisper", $"CustomEndpoint_{id:D}");
                    if (previous.IsFailure) throw new InvalidOperationException(previous.Error!.Message);
                    previousCredential = previous.Value;
                }
                PersistCustomEndpoint(id);
            }
            await _repository.UpsertSafelyAsync(mode, cancellationToken);
            var existingIndex = Items.IndexOf(mode);
            if (existingIndex < 0) Items.Add(mode);
            else Items[existingIndex] = mode;
            IsCreating = false;
            Selected = mode;
            if (customEndpointId is { } savedId)
            {
                _customEndpointId = savedId;
                CustomEndpointApiKey = string.Empty;
            }
            Status.Success("Mode saved");
        }
        catch (Exception)
        {
            if (settingsSnapshot is not null)
            {
                _settings!.Replace(settingsSnapshot);
                _ = _settings.Save();
            }
            if (customEndpointId is { } id && credentialTouched)
            {
                if (previousCredential is { Length: > 0 })
                    _ = _credentials!.Write("HyperWhisper", $"CustomEndpoint_{id:D}", previousCredential);
                else
                    _ = _credentials!.Delete("HyperWhisper", $"CustomEndpoint_{id:D}");
            }
            Status.Failure("modes.save_failed", "Could not save the mode or its secure endpoint configuration.");
        }
        finally
        {
            if (previousCredential is not null) CryptographicOperations.ZeroMemory(previousCredential);
        }
    }
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (Selected == null) { Status.Failure("modes.no_selection", "Select a mode to delete."); return; }
        var selected = Selected;
        try { if (await _repository.DeleteSafelyAsync(selected.Id, cancellationToken)) { Items.Remove(selected); Selected = FallbackSelection(); Status.Success("Mode deleted"); } else Status.Failure("modes.not_found", "The mode no longer exists."); }
        catch (InvalidOperationException exception) { Status.Failure("modes.last_mode", exception.Message); }
        catch (Exception) { Status.Failure("modes.delete_failed", "Could not delete the mode."); }
    }

    private bool ValidateCustomEndpoint()
    {
        // The URL and model rules come from the shared core (#282), the same
        // strict verdict the runtime and the Windows editor use. This method used
        // to spell out its own third variant of them.
        if (string.IsNullOrWhiteSpace(CustomEndpointName)
            || HyperWhisper.SharedCore.LlmPostProcessing.NormalizeCustomEndpoint(
                   CustomEndpointUrl ?? string.Empty, CustomEndpointModel ?? string.Empty).Status
               != HyperWhisper.SharedCore.PortableEndpointStatus.Valid)
        {
            Status.Failure("modes.custom_endpoint_invalid", "Enter a name, HTTP(S) endpoint URL without embedded credentials, and model.");
            return false;
        }
        if (_settings is null || _credentials is null)
        {
            Status.Failure("modes.custom_endpoint_unavailable", "Secure custom endpoint storage is unavailable.");
            return false;
        }
        return true;
    }

    private void PersistCustomEndpoint(Guid id)
    {
        var endpoints = (_settings!.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? []).ToList();
        var configured = new PortableCustomPostProcessingEndpoint(
            id, CustomEndpointName.Trim(), CustomEndpointUrl.Trim(), CustomEndpointModel.Trim());
        var index = endpoints.FindIndex(item => item.Id == id);
        if (index < 0) endpoints.Add(configured); else endpoints[index] = configured;
        _settings.Set("customEndpoints", endpoints);
        if (!string.IsNullOrWhiteSpace(CustomEndpointApiKey))
        {
            var bytes = Encoding.UTF8.GetBytes(CustomEndpointApiKey.Trim());
            try
            {
                var saved = _credentials!.Write("HyperWhisper", $"CustomEndpoint_{id:D}", bytes);
                if (saved.IsFailure) throw new InvalidOperationException(saved.Error!.Message);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var persisted = _settings.Save();
        if (persisted.IsFailure) throw new InvalidOperationException(persisted.Error!.Message);
    }

    private void LoadCustomEndpoint(string? provider)
    {
        ClearCustomEndpointEditor();
        if (_settings is null || provider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) != true
            || !Guid.TryParse(provider[7..], out var id)) return;
        var configured = (_settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
            .FirstOrDefault(item => item.Id == id);
        if (configured is null) return;
        _customEndpointId = id;
        CustomEndpointName = configured.Name;
        CustomEndpointUrl = configured.EndpointUrl;
        CustomEndpointModel = configured.ModelName;
    }

    private void ClearCustomEndpointEditor()
    {
        _customEndpointId = null;
        CustomEndpointName = CustomEndpointUrl = CustomEndpointModel = CustomEndpointApiKey = string.Empty;
    }
}
