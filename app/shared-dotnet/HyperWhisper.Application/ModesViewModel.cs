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
            Selected = null;
            Name = string.Empty;
            Language = "en";
            LocalPostProcessingEnabled = false;
            LocalPostProcessingModel = string.Empty;
            UserSystemPrompt = string.Empty;
            ProviderType = "local"; LocalEngine = "whisper"; TranscriptionModel = "base";
            CloudProvider = "elevenlabs"; CloudAccuracyTier = "elevenLabsScribeV2"; CloudDomain = string.Empty;
            GeminiPrompt = string.Empty; CustomVocabulary = string.Empty; EnableScreenOcr = false;
            PostProcessingMode = "off"; PostProcessingProvider = "openai";
            PostProcessingModel = "gpt-4.1-mini"; HyperWhisperCloudModel = "anthropic:claude-haiku-4-5";
            CustomInstructions = string.Empty; Punctuation = true; Capitalization = true;
            ProfanityFilter = false; RemoveTrailingPeriod = false; EnglishSpelling = string.Empty;
            ClearCustomEndpointEditor();
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
            Name = value.Name;
            Language = value.Language;
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
            LoadCustomEndpoint(value.PostProcessingProvider);
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
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
    public string LocalPostProcessingModel { get => _localPostProcessingModel; set => Set(ref _localPostProcessingModel, value); }
    public string UserSystemPrompt { get => _userSystemPrompt; set => Set(ref _userSystemPrompt, value); }
    public string ProviderType
    {
        get => _providerType;
        set
        {
            if (!Set(ref _providerType, value)) return;
            if (value == "cloud" && PortableModelCatalog.All.Any(model => model.Kind is ManagedModelKind.Whisper or ManagedModelKind.Parakeet
                && string.Equals(model.Id, TranscriptionModel, StringComparison.Ordinal))) TranscriptionModel = string.Empty;
        }
    }
    public string LocalEngine { get => _localEngine; set => Set(ref _localEngine, value); }
    public string TranscriptionModel { get => _transcriptionModel; set => Set(ref _transcriptionModel, value); }
    public IReadOnlyList<string> ProviderTypes { get; } = ["local", "cloud"];
    public IReadOnlyList<string> LocalEngines { get; } = ["whisper", "parakeet"];
    public string CloudProvider { get => _cloudProvider; set => Set(ref _cloudProvider, value); }
    public string CloudAccuracyTier { get => _cloudAccuracyTier; set => Set(ref _cloudAccuracyTier, value); }
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
            if (Set(ref _postProcessingMode, normalized) && _localPostProcessingEnabled != (normalized == "local"))
            {
                _localPostProcessingEnabled = normalized == "local";
                Notify(nameof(LocalPostProcessingEnabled));
            }
        }
    }
    public string PostProcessingProvider { get => _postProcessingProvider; set => Set(ref _postProcessingProvider, value ?? "openai"); }
    public string PostProcessingModel { get => _postProcessingModel; set => Set(ref _postProcessingModel, value ?? string.Empty); }
    public string HyperWhisperCloudModel { get => _hyperWhisperCloudModel; set => Set(ref _hyperWhisperCloudModel, value ?? string.Empty); }
    public string CustomInstructions { get => _customInstructions; set => Set(ref _customInstructions, value ?? string.Empty); }
    public bool Punctuation { get => _punctuation; set => Set(ref _punctuation, value); }
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
    public IReadOnlyList<string> CloudProviders { get; } = ["openai", "groq", "elevenlabs", "mistral", "grok", "deepgram", "assemblyai", "soniox", "gemini", "geminitranscribe", "microsoftazurespeech", "googlespeech", "hyperwhisper"];
    public IReadOnlyList<string> CloudAccuracyTiers { get; } = ["groqWhisper", "deepgramNova3", "grokStt", "azureMaiTranscribe", "geminiTranscribe", "elevenLabsScribeV2", "openaiWhisper", "gemini", "mistralVoxtral", "assemblyAI", "soniox", "metaMuse"];
    public IReadOnlyList<string> CloudDomains { get; } = ["", "medical"];
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
        var mode = Selected ?? new Mode { SortOrder = Items.Count };
        mode.Name = Name.Trim();
        mode.Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();
        mode.ProviderType = ProviderType == "cloud" ? "cloud" : "local";
        mode.LocalEngine = LocalEngine == "parakeet" ? "parakeet" : "whisper";
        if (mode.ProviderType == "cloud")
        {
            mode.CloudProvider = CloudProvider;
            mode.CloudTranscriptionModel = string.IsNullOrWhiteSpace(TranscriptionModel) ? null : TranscriptionModel.Trim();
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
