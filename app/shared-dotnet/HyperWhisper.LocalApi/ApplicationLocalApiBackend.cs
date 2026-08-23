using System.Diagnostics;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.LocalApi;

public interface ILocalApiCapabilityCatalog
{
    IReadOnlyList<ModelEntry> Models { get; }
    IReadOnlyList<ProviderStatus> TranscriptionProviders { get; }
    IReadOnlyList<ProviderStatus> PostProcessingProviders { get; }
    object LocalModels { get; }
}

public interface ILocalApiPostProcessor
{
    ValueTask<PostProcessResult> ProcessAsync(PostProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Production adapter over portable persistence and workflow services. It only
/// reports capabilities supplied by the composed application and never probes
/// credentials, networks, or models on behalf of an API request.
/// </summary>
public sealed class ApplicationLocalApiBackend : ILocalApiBackend
{
    private static readonly HashSet<string> CloudProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "groq", "deepgram", "assemblyai", "elevenlabs", "mistral",
        "soniox", "hyperwhisper", "gemini", "grok", "microsoftazurespeech", "googlespeech",
    };
    private static readonly HashSet<string> WhisperModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en",
        "large-v3-turbo", "large-v2", "large-v3",
    };
    private static readonly HashSet<string> ParakeetModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "parakeet-v2", "parakeet-v3", "qwen3-asr-0.6b", "nemotron-3.5-ml-560ms",
    };
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly ModeRepository _modes;
    private readonly HistoryRepository _history;
    private readonly TranscriptionWorkflow _workflow;
    private readonly ILocalApiCapabilityCatalog _catalog;
    private readonly ILocalApiPostProcessor? _postProcessor;
    private readonly VocabularyRepository? _vocabulary;
    private readonly IPrivateFileService _privateFiles;
    private readonly string _recordingsDirectory;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _recordingToggle = new(1, 1);
    private TranscriptionWorkflowRequest? _activeRecordingRequest;

    public ApplicationLocalApiBackend(
        ModeRepository modes,
        HistoryRepository history,
        TranscriptionWorkflow workflow,
        ILocalApiCapabilityCatalog catalog,
        IPrivateFileService privateFiles,
        IAppPaths paths,
        string appVersion,
        ILocalApiPostProcessor? postProcessor = null,
        VocabularyRepository? vocabulary = null)
    {
        _modes = modes ?? throw new ArgumentNullException(nameof(modes));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _privateFiles = privateFiles ?? throw new ArgumentNullException(nameof(privateFiles));
        ArgumentNullException.ThrowIfNull(paths);
        _recordingsDirectory = paths.RecordingsDirectory;
        _appVersion = appVersion;
        _postProcessor = postProcessor;
        _vocabulary = vocabulary;
    }

    public ValueTask<HealthSnapshot> GetHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new HealthSnapshot(_appVersion, _catalog.TranscriptionProviders, _catalog.PostProcessingProviders, _catalog.LocalModels));
    }

    public ValueTask<IReadOnlyList<ModelEntry>> GetModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_catalog.Models);
    }

    public async ValueTask<IReadOnlyList<JsonElement>> GetModesAsync(CancellationToken cancellationToken)
        => (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).Select(ToModeJson).ToList();

    public async ValueTask<JsonElement?> GetModeAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return null;
        var mode = (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == modeId);
        return mode is null ? null : ToModeJson(mode);
    }

    public async ValueTask<JsonElement> CreateModeAsync(JsonElement document, CancellationToken cancellationToken)
    {
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        var mode = new Mode { Id = Guid.NewGuid(), IsDefault = existing.Count == 0, CreatedDate = DateTime.UtcNow, ModifiedDate = DateTime.UtcNow };
        ApplyModeDocument(mode, document, allowIdentity: false);
        if (existing.Count == 0) mode.IsDefault = true;
        NormalizeMode(mode);
        ValidateMode(mode);
        EnsureUniqueName(mode, existing);
        if (mode.IsDefault)
            foreach (var previous in existing.Where(item => item.IsDefault)) { previous.IsDefault = false; await _modes.UpsertAsync(previous, cancellationToken).ConfigureAwait(false); }
        await _modes.UpsertAsync(mode, cancellationToken).ConfigureAwait(false);
        return ToModeJson(mode);
    }

    public async ValueTask<JsonElement?> PatchModeAsync(string id, JsonElement patch, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return null;
        var mode = (await _modes.ListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == modeId);
        if (mode is null) return null;
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        ApplyModeDocument(mode, patch, allowIdentity: false);
        NormalizeMode(mode);
        mode.ModifiedDate = DateTime.UtcNow;
        ValidateMode(mode);
        EnsureUniqueName(mode, existing);
        if (mode.IsDefault)
            foreach (var previous in existing.Where(item => item.Id != mode.Id && item.IsDefault)) { previous.IsDefault = false; await _modes.UpsertAsync(previous, cancellationToken).ConfigureAwait(false); }
        else if (existing.Count > 0 && existing.All(item => item.Id == mode.Id || !item.IsDefault))
            throw new ArgumentException("At least one mode must remain the default.");
        await _modes.UpsertAsync(mode, cancellationToken).ConfigureAwait(false);
        return ToModeJson(mode);
    }

    public async ValueTask<bool> DeleteModeAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var modeId)) return false;
        var existing = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        var mode = existing.SingleOrDefault(item => item.Id == modeId);
        if (mode is null) return false;
        if (existing.Count == 1) throw new ArgumentException("Cannot delete the last remaining mode.");
        if (!await _modes.DeleteAsync(modeId, cancellationToken).ConfigureAwait(false)) return false;
        if (mode.IsDefault)
        {
            var replacement = existing.Where(item => item.Id != modeId).OrderBy(item => item.SortOrder).First();
            replacement.IsDefault = true;
            replacement.ModifiedDate = DateTime.UtcNow;
            await _modes.UpsertAsync(replacement, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async ValueTask<RecordingState> ToggleRecordingAsync(CancellationToken cancellationToken)
    {
        Task<PortableTranscriptionResult>? stopOperation = null;
        PortableTranscriptionResult result;
        await _recordingToggle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _workflow.Snapshot;
            if (snapshot.State is TranscriptionWorkflowState.Stopping or TranscriptionWorkflowState.Transcribing)
                throw new ArgumentException("A transcription is already in progress.");
            if (snapshot.State == TranscriptionWorkflowState.Recording)
            {
                var request = _activeRecordingRequest ?? await BuildRequestAsync(null, null, cancellationToken).ConfigureAwait(false);
                _activeRecordingRequest = null;
                stopOperation = _workflow.StopAndTranscribeAsync(request, cancellationToken);
            }
            else
            {
                _activeRecordingRequest = null;
                var request = await BuildRequestAsync(null, null, cancellationToken).ConfigureAwait(false);
                result = await _workflow.StartRecordingAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess) _activeRecordingRequest = request;
                ThrowWorkflowFailure(result);
                return ToRecordingState(_workflow.Snapshot);
            }
        }
        finally { _recordingToggle.Release(); }

        result = await stopOperation!.ConfigureAwait(false);
        ThrowWorkflowFailure(result);
        return ToRecordingState(_workflow.Snapshot);
    }

    public async ValueTask<RecordingState> CancelRecordingAsync(CancellationToken cancellationToken)
    {
        await _recordingToggle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _workflow.CancelAsync().ConfigureAwait(false);
            _activeRecordingRequest = null;
            return ToRecordingState(_workflow.Snapshot);
        }
        finally { _recordingToggle.Release(); }
    }

    public async ValueTask<TranscriptionResult> TranscribeAsync(AudioUpload upload, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(upload.FileName);
        if (extension.Length > 12 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.')) extension = ".audio";
        var path = Path.Combine(_recordingsDirectory, $"local-api-{Guid.NewGuid():N}{extension}");
        var written = _privateFiles.WriteAllBytesAtomically(path, upload.Content.Span);
        if (written.IsFailure) throw new InvalidOperationException("The uploaded audio could not be staged privately.");
        var succeeded = false;
        var retainedByHistory = false;
        try
        {
            var request = await BuildRequestAsync(
                upload.ModeId, upload.Language, cancellationToken,
                upload.Engine, upload.Model, upload.ApplicationContext?.ToSnapshot()).ConfigureAwait(false);
            var mode = request.SelectedMode;
            // Match the Windows Local API contract: /transcribe returns the
            // transcription result and never runs a mode's post-processing.
            // Callers that want enhancement use the separate /post-process route.
            if (mode is not null) mode.PostProcessingMode = 0;
            var started = Stopwatch.GetTimestamp();
            var result = await _workflow.TranscribeFileAsync(path, request, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Failure?.Message ?? "Transcription failed.");
            succeeded = true;
            return new(
                result.Text!,
                EngineLabel(mode),
                ModelLabel(mode),
                string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase) ? null : request.Language,
                0, 0,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch
        {
            // The workflow may return a failure or throw after it has created a
            // retryable Failed row. Always consult history without the caller's
            // cancelled token before deciding whether staged audio is orphaned.
            try
            {
                retainedByHistory = (await _history.ListAsync(CancellationToken.None).ConfigureAwait(false))
                    .Any(item => string.Equals(item.AudioFilePath, path, StringComparison.Ordinal));
            }
            catch (Exception)
            {
                // Conservatively retain audio if persistence cannot be checked;
                // deleting could corrupt an existing retryable history row.
                retainedByHistory = true;
            }
            throw;
        }
        finally { if (!succeeded && !retainedByHistory) _ = _privateFiles.Delete(path); }
    }

    public ValueTask<PostProcessResult> PostProcessAsync(PostProcessRequest request, CancellationToken cancellationToken)
        => _postProcessor?.ProcessAsync(request, cancellationToken)
            ?? ValueTask.FromException<PostProcessResult>(new InvalidOperationException("Post-processing is not configured."));

    public async ValueTask<IReadOnlyList<RecordingEntry>> GetRecordingsAsync(RecordingQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<Transcript> rows = await _history.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query.Search)) rows = rows.Where(item => item.Text.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || (item.TranscribedText?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));
        if (query.Since is { } since) rows = rows.Where(item => item.Date >= since);
        if (query.Until is { } until) rows = rows.Where(item => item.Date <= until);
        return rows.Take(query.Limit).Select(ToRecording).ToList();
    }

    public async ValueTask<RecordingEntry?> GetRecordingAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var recordingId)) return null;
        var item = await _history.GetAsync(recordingId, cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToRecording(item);
    }

    private static void EnsureUniqueName(Mode mode, IReadOnlyList<Mode> existing)
    {
        if (existing.Any(item => item.Id != mode.Id && string.Equals(item.Name, mode.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A mode with this name already exists.");
    }

    private async Task<TranscriptionWorkflowRequest> BuildRequestAsync(
        string? requestedModeId,
        string? languageOverride,
        CancellationToken cancellationToken,
        string? engineOverride = null,
        string? modelOverride = null,
        ApplicationContextSnapshot? applicationContext = null)
    {
        var modes = await _modes.ListAsync(cancellationToken).ConfigureAwait(false);
        Mode? mode;
        if (!string.IsNullOrWhiteSpace(requestedModeId))
        {
            if (!Guid.TryParse(requestedModeId, out var parsed))
                throw new ArgumentException("The requested mode ID is invalid.", nameof(requestedModeId));
            mode = modes.SingleOrDefault(item => item.Id == parsed)
                ?? throw new ArgumentException("The requested mode does not exist.", nameof(requestedModeId));
        }
        else if (string.IsNullOrWhiteSpace(engineOverride))
        {
            mode = modes.SingleOrDefault(item => item.IsDefault);
            if (mode is null && modes.Count != 0)
                throw new InvalidOperationException("No default transcription mode is configured.");
        }
        else mode = new Mode
        {
            Name = "__local_api_transient__",
            Language = "auto",
            ProviderType = "local",
            LocalEngine = "whisper",
            Model = "base",
            ModelType = "base",
            SortOrder = int.MaxValue,
        };

        if (mode is not null)
            ApplyTranscriptionOverrides(mode, engineOverride, modelOverride);

        var vocabulary = _vocabulary is null
            ? Array.Empty<string>()
            : (await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(item => item.Word.Trim())
                .Where(item => item.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return new(languageOverride ?? mode?.Language, mode?.Name, mode?.Id, mode, vocabulary, applicationContext);
    }

    private static void ApplyTranscriptionOverrides(Mode mode, string? engine, string? model)
    {
        var normalizedEngine = engine?.Trim().ToLowerInvariant();
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEngine))
        {
            if (normalizedModel is null) return;
            if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
                mode.CloudTranscriptionModel = normalizedModel;
            else if (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase))
                mode.LocalParakeetModel = mode.Model = normalizedModel;
            else mode.ModelType = mode.Model = normalizedModel;
            return;
        }

        var cloud = normalizedEngine == "cloud" ? "hyperwhisper" : normalizedEngine switch
        {
            "microsoftazurespeech" => "microsoftAzureSpeech",
            "googlespeech" => "googleSpeech",
            _ => normalizedEngine,
        };
        if (CloudProviders.Contains(cloud))
        {
            mode.ProviderType = "cloud";
            mode.CloudProvider = cloud;
            mode.Model = "cloud";
            if (normalizedModel is not null) mode.CloudTranscriptionModel = normalizedModel;
            return;
        }

        switch (normalizedEngine)
        {
            case "whisper":
            case "whisperlocal":
            case "libwhisper":
                if (normalizedModel is null) throw new ArgumentException("A Whisper model is required.");
                mode.ProviderType = "local";
                mode.LocalEngine = "whisper";
                mode.ModelType = mode.Model = normalizedModel;
                return;
            case "parakeet":
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = mode.Model = normalizedModel ?? "parakeet-v3";
                return;
            case "qwen3":
            case "qwen3asr":
            case "qwen3_asr":
            case "qwen3-asr":
            case "qwen":
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = mode.Model = normalizedModel ?? "qwen3-asr-0.6b";
                return;
            default:
                throw new ArgumentException("The requested transcription engine is unsupported.");
        }
    }

    private static string EngineLabel(Mode? mode)
    {
        if (mode is null) return string.Empty;
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
            return mode.CloudProvider ?? "cloud";
        if (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase))
            return mode.LocalParakeetModel?.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) == true
                ? "qwen3_asr" : "parakeet";
        return "whisperLocal";
    }

    private static string ModelLabel(Mode? mode)
    {
        if (mode is null) return string.Empty;
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
            return mode.CloudTranscriptionModel ?? string.Empty;
        return string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            ? mode.LocalParakeetModel ?? mode.Model ?? string.Empty
            : mode.ModelType ?? mode.Model ?? string.Empty;
    }

    private static void NormalizeMode(Mode mode)
    {
        mode.LocalEngine = string.IsNullOrWhiteSpace(mode.LocalEngine) ? "whisper" : mode.LocalEngine.Trim().ToLowerInvariant();
        if (string.Equals(mode.Model, "cloud", StringComparison.OrdinalIgnoreCase)) mode.ProviderType = "cloud";
        mode.ProviderType = string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
        if (mode.ProviderType == "cloud") mode.Model = "cloud";
        else if (mode.LocalEngine == "parakeet") mode.Model = mode.LocalParakeetModel ?? mode.Model ?? "parakeet-v3";
        else { mode.Model = string.IsNullOrWhiteSpace(mode.Model) ? "base" : mode.Model; mode.ModelType = mode.Model; }
        mode.CloudAccuracyTier = string.IsNullOrWhiteSpace(mode.CloudAccuracyTier) ? "elevenLabsScribeV2" : mode.CloudAccuracyTier;
        mode.CloudPostProcessingModel = string.IsNullOrWhiteSpace(mode.CloudPostProcessingModel) ? "anthropic:claude-haiku-4-5" : mode.CloudPostProcessingModel;
    }

    private void ValidateMode(Mode mode)
    {
        mode.Name = mode.Name.Trim();
        if (mode.Name.Length is < 1 or > 100) throw new ArgumentException("Mode name must contain 1 to 100 characters.");
        if (mode.Language.Length is < 1 or > 32) throw new ArgumentException("Mode language is invalid.");
        if (mode.Preset.Length is < 1 or > 64) throw new ArgumentException("Mode preset is invalid.");
        if (mode.PostProcessingMode is < 0 or > 2) throw new ArgumentException("Post-processing mode is invalid.");
        if (mode.PostProcessingMode != 0 && string.IsNullOrWhiteSpace(mode.PostProcessingProvider)) throw new ArgumentException("An enabled post-processing mode requires a provider.");
        if (mode.UserSystemPrompt?.Length > 2000 || mode.GeminiCustomPrompt?.Length > 2000) throw new ArgumentException("Mode prompt exceeds 2000 characters.");
        if (mode.CustomVocabulary?.Count > 1000 || mode.CustomVocabulary?.Any(term => term.Length > 200) == true) throw new ArgumentException("Custom vocabulary is invalid.");
        if (mode.ProviderType == "cloud")
        {
            if (string.IsNullOrWhiteSpace(mode.CloudProvider) || !CloudProviders.Contains(mode.CloudProvider))
                throw new ArgumentException("Cloud provider is invalid.");
        }
        else
        {
            if (mode.LocalEngine is not ("whisper" or "parakeet"))
                throw new ArgumentException("Local transcription engine is invalid.");
            var model = mode.LocalEngine == "parakeet" ? mode.LocalParakeetModel ?? mode.Model : mode.ModelType ?? mode.Model;
            var known = mode.LocalEngine == "parakeet" ? ParakeetModels : WhisperModels;
            if (string.IsNullOrWhiteSpace(model) || !known.Contains(model))
                throw new ArgumentException("Local transcription model is invalid.");
            var advertisedVoiceModels = _catalog.Models.Where(item => string.Equals(item.Kind, "voice", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (advertisedVoiceModels.Length != 0 && !advertisedVoiceModels.Any(item => string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Local transcription model is not present in the capability catalog.");
        }
    }

    private static void ApplyModeDocument(Mode mode, JsonElement document, bool allowIdentity)
    {
        if (document.ValueKind != JsonValueKind.Object) throw new ArgumentException("Mode body must be a JSON object.");
        foreach (var property in document.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id" when allowIdentity: mode.Id = property.Value.GetGuid(); break;
                case "id" or "createdDate" or "modifiedDate" or "isSystemProvided": break;
                case "name": mode.Name = RequiredString(property); break;
                case "preset": mode.Preset = RequiredString(property); break;
                case "language": mode.Language = RequiredString(property); break;
                case "model": mode.Model = OptionalString(property); mode.ModelType = mode.Model; break;
                case "localEngine": mode.LocalEngine = RequiredString(property); break;
                case "localParakeetModel": mode.LocalParakeetModel = OptionalString(property); break;
                case "cloudProvider": mode.CloudProvider = OptionalString(property); break;
                case "cloudTranscriptionModel": mode.CloudTranscriptionModel = OptionalString(property); break;
                case "cloudTranscriptionDomain": mode.CloudTranscriptionDomain = OptionalString(property); break;
                case "providerType": mode.ProviderType = OptionalString(property); break;
                case "cloudAccuracyTier": mode.CloudAccuracyTier = RequiredString(property); break;
                case "geminiCustomPrompt": mode.GeminiCustomPrompt = OptionalString(property); break;
                case "punctuation": mode.Punctuation = property.Value.GetBoolean(); break;
                case "capitalization": mode.Capitalization = property.Value.GetBoolean(); break;
                case "profanityFilter": mode.ProfanityFilter = property.Value.GetBoolean(); break;
                case "removeTrailingPeriod": mode.RemoveTrailingPeriod = property.Value.GetBoolean(); break;
                case "englishSpelling": mode.EnglishSpelling = OptionalString(property); break;
                case "postProcessingMode": mode.PostProcessingMode = property.Value.GetInt32(); break;
                case "postProcessingProvider": mode.PostProcessingProvider = OptionalString(property); break;
                case "languageModel": mode.LanguageModel = OptionalString(property); break;
                case "localPostProcessingModel": mode.LocalPostProcessingModel = OptionalString(property); break;
                case "userSystemPrompt": mode.UserSystemPrompt = OptionalString(property); break;
                case "customInstructions": mode.CustomInstructions = OptionalString(property); break;
                case "enableScreenOCR": mode.EnableScreenOCR = property.Value.GetBoolean(); break;
                case "cloudPostProcessingModel": mode.CloudPostProcessingModel = RequiredString(property); break;
                case "customVocabulary": mode.CustomVocabulary = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.Deserialize<List<string>>(WebJson); break;
                case "isDefault": mode.IsDefault = property.Value.GetBoolean(); break;
                case "sortOrder": mode.SortOrder = property.Value.GetInt32(); break;
                case "useStreamingTranscription": break; // Legacy wire-only field; no EF storage exists.
                default: throw new ArgumentException($"Unsupported mode field '{property.Name}'.");
            }
        }
    }

    private static string RequiredString(JsonProperty property) => property.Value.GetString() ?? throw new ArgumentException($"'{property.Name}' must be a string.");
    private static string? OptionalString(JsonProperty property) => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();

    private static JsonElement ToModeJson(Mode mode) => JsonSerializer.SerializeToElement(new
    {
        id = mode.Id.ToString("D"), mode.Name, mode.Preset, mode.Language, model = mode.ProviderType == "cloud" ? "cloud" : mode.ModelType ?? mode.Model ?? "base",
        mode.Punctuation, mode.Capitalization, mode.ProfanityFilter, mode.CustomInstructions,
        mode.UserSystemPrompt, mode.IsDefault, mode.IsSystemProvided, mode.SortOrder,
        mode.CreatedDate, mode.ModifiedDate, mode.LanguageModel, mode.CloudTranscriptionModel,
        mode.CloudTranscriptionDomain, mode.CloudProvider, mode.PostProcessingMode,
        mode.PostProcessingProvider, mode.EnglishSpelling, useStreamingTranscription = false,
        mode.CloudAccuracyTier, mode.RemoveTrailingPeriod, mode.EnableScreenOCR,
        mode.GeminiCustomPrompt, mode.CloudPostProcessingModel, mode.LocalEngine,
        mode.LocalParakeetModel, mode.LocalPostProcessingModel, mode.CustomVocabulary, mode.ProviderType,
    }, WebJson);

    private static RecordingEntry ToRecording(Transcript item) => new(item.Id.ToString("D"), item.Text, item.Date, item.Duration, item.Mode, item.Status.ToString().ToLowerInvariant(), item.PostProcessedText, item.TranscribedText, item.TranscriptionProvider, item.PostProcessingProvider, item.AudioFilePath);
    private static RecordingState ToRecordingState(TranscriptionWorkflowSnapshot snapshot) => new(snapshot.State == TranscriptionWorkflowState.Recording, snapshot.State.ToString().ToLowerInvariant());

    private static void ThrowWorkflowFailure(PortableTranscriptionResult result)
    {
        if (result.IsSuccess) return;
        if (result.Failure?.Code == PortableTranscriptionErrorCode.BackendUnavailable)
            throw new InvalidOperationException("The recording workflow is unavailable.");
        throw new ArgumentException("The recording workflow could not complete the requested transition.");
    }
}
