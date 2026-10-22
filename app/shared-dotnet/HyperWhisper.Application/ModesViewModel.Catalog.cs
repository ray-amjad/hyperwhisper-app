using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

// The on-device and BYOK model pickers: what each lists, and how the selection follows the source.
public sealed partial class ModesViewModel
{
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
    ///
    /// ORDER MATTERS, for the reason <see cref="NormalizeCloudModel"/> spells out (issue #1629).
    /// An engine change swaps the model combo's ItemsSource for the other engine's list. Avalonia
    /// clears a SelectedItem the new list lacks, the <see cref="LocalTranscriptionModel"/> proxy
    /// refuses the null write-back, and the binding re-reads the property and records what it
    /// finds as already delivered. If the incoming id is assigned first, that re-read records the
    /// INCOMING id, the notification that follows is deduplicated, and the combo stays blank over
    /// a correct field (Whisper -> Parakeet showed nothing while parakeet-v2 was saved). Handing
    /// the combo the new list while the field still holds the OUTGOING id makes the incoming one a
    /// change the binding delivers.
    /// </summary>
    private void NormalizeLocalModel()
    {
        if (!IsOnDeviceSource) return;
        var kind = LocalModelKind;
        if (PortableModelCatalog.All.Any(model => model.Kind == kind
            && string.Equals(model.Id, _transcriptionModel, StringComparison.Ordinal))) return;
        // The list BEFORE the selection — see the order note above. The getter rebuilds the cache
        // for the new engine here; the LocalModels notification NotifyEditorReveals raises a moment
        // later finds the same instance and is a no-op at the control.
        Notify(nameof(LocalModels));
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
    /// An id with no BYOK catalog vendor returns nothing — the retired <c>microsoftazurespeech</c>
    /// (<c>azure-mai</c> is not <c>byokEligible</c>) and <c>googlespeech</c> (no catalog vendor)
    /// were the two such ids in <see cref="CloudProviders"/> until issue #601 removed them.
    /// <see cref="NormalizeCloudModel"/> empties the selection to match, so Save writes null —
    /// which is what an implicit model means. <c>grok</c> was a third until 2026-09-19: its single
    /// catalog model carried an empty id, because an xAI request named no model.
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
        //
        // An id the vendor no longer lists but the shared alias table (hw-catalog model_alias)
        // names a successor for, such as Grok `grok-voice-transcribe-1.0` -> 2.0 (#1908), is that
        // successor rather than an unknown id to pin. The alias is consulted only for an unlisted
        // id: a deprecated id that is still a row (OpenAI `gpt-4o-transcribe`) stays as saved.
        var vendorIds = CloudVendorModelIds(_cloudProvider);
        var listed = vendorIds
            .FirstOrDefault(id => string.Equals(id, _transcriptionModel, StringComparison.OrdinalIgnoreCase));
        if (listed is null)
        {
            var aliased = SharedCoreBridge.ResolveCloudSttModelAlias(_transcriptionModel, _cloudProvider);
            listed = vendorIds
                .FirstOrDefault(id => string.Equals(id, aliased, StringComparison.OrdinalIgnoreCase));
        }
        if (listed is null) _cloudModelsPinnedId = _transcriptionModel;
        else _transcriptionModel = listed;
    }

    private string? _cloudModelsProvider;
    private string? _cloudModelsPinnedId;
    private IReadOnlyList<string>? _cloudModels;

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

    /// <summary>
    /// Every HyperWhisper Cloud post-processing model, as the "provider:model" storage value the
    /// mode actually saves. Windows offers these in a pair of pickers rather than a text field
    /// (ModeEditorWindow.xaml:437-452, filled by LoadCloudPostProcessingModelsForEngine), so a
    /// Linux mode editor that typed the id by hand showed a raw "anthropic:claude-haiku-5-5" where
    /// Windows shows "Claude Haiku 5.5 (Recommended)".
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
}
