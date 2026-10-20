using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Input;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.ModelManagement;
using HyperWhisper.SharedCore;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed partial class ModesViewModel : ViewModelBase
{
    private readonly ModeRepository _repository;
    private readonly PortableSettingsService? _settings;
    private readonly ICredentialStore? _credentials;
    private Mode? _selected;
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
            PostProcessingModel = "gpt-5.6-luna"; HyperWhisperCloudModel = "anthropic:claude-haiku-5-5";
            CustomInstructions = string.Empty; Punctuation = true; Capitalization = true;
            ProfanityFilter = false; RemoveTrailingPeriod = false; EnglishSpelling = string.Empty;
            Preset = "hyper"; UserPromptEnabled = false;
            ClearCustomEndpointEditor();
            NotifyEditorReveals();
            return Task.CompletedTask;
        });
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
        PropertyChanged += (_, e) =>
        {
            if (!_loadingEditor && e.PropertyName is { } name && EditorFieldNames.Contains(name))
                ClearStaleSuccessStatus();
        };
    }

    // The persisted editor fields — the same set ModeEditorSnapshot carries. A user edit to any of
    // them makes the form dirty.
    private static readonly HashSet<string> EditorFieldNames =
    [
        nameof(Name), nameof(Language), nameof(Preset), nameof(LocalPostProcessingEnabled),
        nameof(LocalPostProcessingModel), nameof(UserSystemPrompt), nameof(UserPromptEnabled),
        nameof(ProviderType), nameof(LocalEngine), nameof(TranscriptionModel), nameof(CloudProvider),
        nameof(CloudAccuracyTier), nameof(CloudDomain), nameof(GeminiPrompt), nameof(CustomVocabulary),
        nameof(EnableScreenOcr), nameof(PostProcessingMode), nameof(PostProcessingProvider),
        nameof(PostProcessingModel), nameof(HyperWhisperCloudModel), nameof(CustomInstructions),
        nameof(Punctuation), nameof(Capitalization), nameof(ProfanityFilter), nameof(RemoveTrailingPeriod),
        nameof(EnglishSpelling), nameof(CustomEndpointName), nameof(CustomEndpointUrl),
        nameof(CustomEndpointModel), nameof(CustomEndpointApiKey),
    ];

    /// <summary>
    /// Issue #632: the editor footer binds <see cref="Status"/>, which is shared with the page and
    /// outlives the dialog. A `Mode saved` left by the previous save read as confirmation of an
    /// edit that is still pending. A success line describes state the form no longer shows once a
    /// field changes, so it goes; an error stays, because it names what the user is fixing.
    /// </summary>
    private void ClearStaleSuccessStatus()
    {
        if (!Status.HasError && !Status.IsBusy && Status.Message.Length > 0) Status.Clear();
    }

    /// <summary>
    /// Called when the mode editor opens, on both the edit and the create path. A success line
    /// belongs to an earlier dialog — a fresh Create New Mode form used to open on the last edit's
    /// `Mode saved` (issue #632) — so it goes. An error stays: the footer is the only place this
    /// status shows, and the gear click that opens the editor sets <see cref="Selected"/>, whose
    /// failure to persist is reported here. An earlier dialog's own errors are cleared by
    /// <see cref="EndEditorSession"/> when that dialog is discarded.
    /// </summary>
    public void BeginEditorSession()
    {
        if (!Status.HasError) Status.Clear();
    }

    /// <summary>
    /// Called when the mode editor is discarded (Cancel, Esc or the title bar), BEFORE the
    /// pre-dialog state is restored. A validation error names a field of the form being thrown
    /// away, so it must not greet the next dialog; a failure the restore itself raises still shows.
    /// </summary>
    public void EndEditorSession() => Status.Clear();
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
            Status.Success(Plural.Count(Items.Count, "mode", "modes"));
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
        if (!ValidateEditor()) return;
        var customEndpointId = PostProcessingMode == "cloud" && PostProcessingProvider == "custom"
            ? _customEndpointId ?? Guid.NewGuid()
            : (Guid?)null;
        // A create dialog must insert even though Selected still points at the mode the user had
        // active — Windows keeps that selection while the create window is open.
        var mode = (_isCreating ? null : Selected) ?? new Mode { SortOrder = Items.Count };
        ApplyEditorTo(mode, customEndpointId);
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
            CommitSavedMode(mode, customEndpointId);
        }
        catch (Exception)
        {
            RollBackFailedSave(settingsSnapshot, customEndpointId, credentialTouched, previousCredential);
            Status.Failure("modes.save_failed", "Could not save the mode or its secure endpoint configuration.");
        }
        finally
        {
            if (previousCredential is not null) CryptographicOperations.ZeroMemory(previousCredential);
        }
    }

    /// <summary>
    /// The checks <see cref="SaveAsync"/> runs before it writes anything. Each failure is reported on
    /// <see cref="Status"/> and answers false. A legacy cloud provider is folded here (issue #601), as
    /// before, so the allow-list check that follows sees the folded value.
    /// </summary>
    private bool ValidateEditor()
    {
        if (IsDictation && (!DictationLanguages.Contains(Language) || !string.IsNullOrWhiteSpace(CloudDomain)))
        {
            Status.Failure("modes.dictation_selection", "Dictation needs an explicit supported language and no Medical domain.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Name)) { Status.Failure("modes.name_required", "Enter a mode name."); return false; }
        if (PostProcessingMode == "local"
            && (string.IsNullOrWhiteSpace(LocalPostProcessingModel)
                || LocalPostProcessingModel != Path.GetFileName(LocalPostProcessingModel)
                || LocalPostProcessingModel.Contains('\\')
                || !LocalPostProcessingModel.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
        {
            Status.Failure("modes.local_llm_model_required", "Enter a GGUF model filename from the local LLM models directory.");
            return false;
        }
        if (UserSystemPrompt.Length > 8000)
        {
            Status.Failure("modes.prompt_too_long", "The system prompt cannot exceed 8000 characters.");
            return false;
        }
        if (CustomInstructions.Length > 4000)
        { Status.Failure("modes.instructions_too_long", "Custom instructions cannot exceed 4000 characters."); return false; }
        if (PostProcessingMode == "cloud" && !PostProcessingProviders.Contains(PostProcessingProvider, StringComparer.Ordinal))
        { Status.Failure("modes.postprocessing_provider_invalid", "Select a supported post-processing provider."); return false; }
        if (PostProcessingMode == "cloud" && PostProcessingProvider == "custom" && !ValidateCustomEndpoint()) return false;
        if (GeminiPrompt.Length > 2000) { Status.Failure("modes.gemini_prompt_too_long", "The Gemini prompt cannot exceed 2000 characters."); return false; }
        // Issue #601: run the provider through the shared storage fold BEFORE the allow-list check,
        // as #598 does for the Local API's ApplyModeDocument. A legacy alias set on this view
        // model by any path other than the (folded) load becomes HyperWhisper Cloud, with its tier
        // only if the current one is blank or the old default; it is never written verbatim.
        if (ProviderType == "cloud"
            && LegacyCloudProviderFold.TryFold(CloudProvider, CloudAccuracyTier, out var savedProvider, out var savedTier))
        {
            CloudProvider = savedProvider;
            CloudAccuracyTier = savedTier ?? CloudAccuracyTier;
        }
        if (ProviderType == "cloud" && !CloudProviders.Contains(CloudProvider, StringComparer.Ordinal))
        { Status.Failure("modes.cloud_provider_required", "Select a supported cloud provider."); return false; }
        if (ProviderType != "cloud")
        {
            var kind = LocalEngine == "parakeet" ? ManagedModelKind.Parakeet : ManagedModelKind.Whisper;
            if (!PortableModelCatalog.All.Any(model => model.Kind == kind && string.Equals(model.Id, TranscriptionModel, StringComparison.Ordinal)))
            { Status.Failure("modes.local_model_invalid", "Select a model ID from the local model catalog."); return false; }
        }
        return true;
    }

    /// <summary>Copies the editor fields onto <paramref name="mode"/>, the row Save upserts.</summary>
    private void ApplyEditorTo(Mode mode, Guid? customEndpointId)
    {
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
            ? "anthropic:claude-haiku-5-5" : HyperWhisperCloudModel.Trim();
        mode.UserSystemPrompt = string.IsNullOrWhiteSpace(UserSystemPrompt) ? null : UserSystemPrompt.Trim();
        mode.CustomInstructions = string.IsNullOrWhiteSpace(CustomInstructions) ? null : CustomInstructions.Trim();
        mode.Punctuation = Punctuation; mode.Capitalization = Capitalization;
        mode.ProfanityFilter = ProfanityFilter; mode.RemoveTrailingPeriod = RemoveTrailingPeriod;
        mode.EnglishSpelling = string.IsNullOrWhiteSpace(EnglishSpelling) ? null : EnglishSpelling.Trim();
        mode.CustomVocabulary = CustomVocabulary.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        mode.EnableScreenOCR = EnableScreenOcr;
        mode.ModifiedDate = DateTime.UtcNow;
    }

    /// <summary>The in-memory half of a save that reached the database: the list, the selection, the endpoint id.</summary>
    private void CommitSavedMode(Mode mode, Guid? customEndpointId)
    {
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

    /// <summary>
    /// Undoes a failed save: puts the settings snapshot back and restores, or removes, the endpoint
    /// credential. <see cref="SaveAsync"/> still zeroes <paramref name="previousCredential"/> in its
    /// own finally, so the bytes are cleared on every path.
    /// </summary>
    private void RollBackFailedSave(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? settingsSnapshot,
        Guid? customEndpointId, bool credentialTouched, byte[]? previousCredential)
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
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (Selected == null) { Status.Failure("modes.no_selection", "Select a mode to delete."); return; }
        var selected = Selected;
        try { if (await _repository.DeleteSafelyAsync(selected.Id, cancellationToken)) { Items.Remove(selected); Selected = FallbackSelection(); Status.Success("Mode deleted"); } else Status.Failure("modes.not_found", "The mode no longer exists."); }
        catch (InvalidOperationException exception) { Status.Failure("modes.last_mode", exception.Message); }
        catch (Exception) { Status.Failure("modes.delete_failed", "Could not delete the mode."); }
    }
}
