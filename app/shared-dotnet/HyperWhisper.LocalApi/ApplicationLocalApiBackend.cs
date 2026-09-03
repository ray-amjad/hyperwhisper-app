using System.Diagnostics;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using uniffi.hyperwhisper_core;

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
        "soniox", "hyperwhisper", "gemini", "geminitranscribe", "grok",
        "microsoftazurespeech", "googlespeech", "meta",
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
        var facts = ApplyModeDocument(mode, document, allowIdentity: false);
        if (existing.Count == 0) mode.IsDefault = true;
        NormalizeMode(mode);
        ValidateMode(mode, facts, HwLocalApiModeOperation.Create);
        EnsureUniqueName(mode, existing, HwLocalApiModeOperation.Create);
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
        var facts = ApplyModeDocument(mode, patch, allowIdentity: false);
        NormalizeMode(mode);
        mode.ModifiedDate = DateTime.UtcNow;
        ValidateMode(mode, facts, HwLocalApiModeOperation.Patch);
        EnsureUniqueName(mode, existing, HwLocalApiModeOperation.Patch);
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
                upload.Engine, upload.Model, upload.ApplicationContext?.ToSnapshot(),
                RequestsTimestamps(upload.TimestampGranularities)).ConfigureAwait(false);
            var mode = request.SelectedMode;
            // Match the Windows Local API contract: /transcribe returns the
            // transcription result and never runs a mode's post-processing.
            // Callers that want enhancement use the separate /post-process route.
            if (mode is not null) mode.PostProcessingMode = 0;
            var started = Stopwatch.GetTimestamp();
            var result = await _workflow.TranscribeFileAsync(path, request, cancellationToken).ConfigureAwait(false);
            // The failure's code AND its message both used to die here: the
            // middleware's `catch (InvalidOperationException)` binds no
            // variable, so every transcription failure on this head reached the
            // wire as one fixed ENGINE_UNAVAILABLE string (issue #356 item 4).
            if (!result.IsSuccess) throw LocalApiSharedFailure.TranscriptionFailure(result.Failure);
            succeeded = true;
            return new(
                result.Text!,
                EngineLabel(mode),
                ModelLabel(mode),
                string.Equals(request.Language, "auto", StringComparison.OrdinalIgnoreCase) ? null : request.Language,
                0, 0,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.Timestamps?.RawText,
                result.Timestamps?.Segments,
                result.Timestamps?.Words);
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

    public async ValueTask<RecordingPage> GetRecordingsAsync(RecordingQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<Transcript> rows = await _history.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query.Search)) rows = rows.Where(item => item.Text.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || (item.TranscribedText?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));
        if (query.Since is { } since) rows = rows.Where(item => item.Date >= since);
        if (query.Until is { } until) rows = rows.Where(item => item.Date <= until);
        // Materialize the filtered set so `Total` is the true match count, not the
        // page size. Windows does exactly this (`matches.Count` before `Take(limit)`)
        // and macOS runs a separate count fetch; a client paginating on
        // `total > returned` has to see the same number on all three heads.
        var matches = rows.ToList();
        return new RecordingPage(matches.Take(query.Limit).Select(ToRecording).ToList(), matches.Count);
    }

    public async ValueTask<RecordingEntry?> GetRecordingAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var recordingId)) return null;
        var item = await _history.GetAsync(recordingId, cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToRecording(item);
    }

    /// <summary>
    /// "The same name" is now one rule, and a collision is now the code the
    /// other two heads already send (issue #356 item 5).
    /// </summary>
    /// <remarks>
    /// This head compared with <c>OrdinalIgnoreCase</c> and threw a plain
    /// <see cref="ArgumentException"/>, which the middleware turned into HTTP
    /// 400 <c>INVALID_REQUEST</c> — where macOS and Windows both answer HTTP 200
    /// <c>MODE_NAME_TAKEN</c>. That code was declared here and never emitted.
    /// The comparison key (trim, then <c>to_lowercase</c>) and the message now
    /// come from <c>hw-localapi</c>; the "which record am I writing" filter
    /// stays here, because only this head knows that.
    /// </remarks>
    private static void EnsureUniqueName(Mode mode, IReadOnlyList<Mode> existing, HwLocalApiModeOperation operation)
    {
        // ONLY WHEN THE NAME IS ACTUALLY CHANGING (issue #356, review round 1).
        // `existing` is a separate `ListAsync` materialisation, so its copy of
        // this record still carries the STORED name; `mode` has already been
        // patched. macOS has always had this guard (`newName != mode.name`) and
        // Windows has it again — this head never did, and #356 widened the
        // comparison key, which enlarges the set of already-stored pairs that
        // collide. Duplicate names are producible: nothing outside these two
        // endpoints checks, and backup import does not. Without the guard a mode
        // that shares a name with another is patchable only by a body that never
        // mentions `name` — and `ApplyModeDocument` leaves the stored name in
        // place for exactly those bodies, so it would fail on all of them.
        //
        // A name whose comparison key is unchanged cannot introduce a NEW
        // collision: the multiset of keys in storage is the same after the write
        // as before it.
        var storedName = existing.FirstOrDefault(item => item.Id == mode.Id)?.Name;
        if (storedName is not null && HyperwhisperCoreMethods.LocalApiModeNameConflict(mode.Name, [storedName]))
            return;
        var others = existing.Where(item => item.Id != mode.Id).Select(item => item.Name).ToList();
        if (HyperwhisperCoreMethods.LocalApiModeNameConflict(mode.Name, others))
            throw LocalApiFailureException.From(
                HyperwhisperCoreMethods.LocalApiModeNameTakenFailure(mode.Name, operation));
    }

    private async Task<TranscriptionWorkflowRequest> BuildRequestAsync(
        string? requestedModeId,
        string? languageOverride,
        CancellationToken cancellationToken,
        string? engineOverride = null,
        string? modelOverride = null,
        ApplicationContextSnapshot? applicationContext = null,
        bool storeWordTimestamps = false)
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

        // Shared core rule: sanitize, drop empties, dedupe case-insensitively.
        // Uncapped — the local API hands the whole vocabulary to the workflow,
        // and each provider applies its own cap downstream.
        IReadOnlyList<string> vocabulary = _vocabulary is null
            ? []
            : SharedCoreBridge.NormalizeVocabularyTerms(
                [.. (await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false)).Select(item => item.Word)],
                null);
        return new(
            languageOverride ?? mode?.Language,
            mode?.Name,
            mode?.Id,
            mode,
            vocabulary,
            applicationContext,
            StoreWordTimestamps: storeWordTimestamps);
    }

    private static bool RequestsTimestamps(IReadOnlyList<string>? granularities) =>
        granularities?.Any(value => value.Equals("word", StringComparison.OrdinalIgnoreCase)
            || value.Equals("words", StringComparison.OrdinalIgnoreCase)
            || value.Equals("segment", StringComparison.OrdinalIgnoreCase)
            || value.Equals("segments", StringComparison.OrdinalIgnoreCase)) == true;

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
            else if (cloud == "meta") mode.CloudTranscriptionModel = "muse-voice-transcribe-1.0";
            return;
        }

        // ONE ALIAS TABLE, SHARED WITH macOS AND WINDOWS (issue #356 item 3).
        // The local half of the documented `engine` field used to be a fourth
        // hand-kept `switch`; it is now `resolve_engine_alias`, which normalises
        // (trim, then lowercase) and answers a canonical id. `None` still means
        // "not one of the five", which after the cloud fold above is the same
        // unsupported-engine answer this head already gave — the wording is
        // item 4's to reconcile, not this phase's.
        var resolved = HyperwhisperCoreMethods.LocalApiResolveEngineAlias(normalizedEngine);
        switch (resolved)
        {
            case HwLocalApiEngineId.WhisperLocal:
                if (normalizedModel is null) throw new ArgumentException("A Whisper model is required.");
                mode.ProviderType = "local";
                mode.LocalEngine = "whisper";
                mode.ModelType = mode.Model = normalizedModel;
                return;
            case HwLocalApiEngineId.Parakeet:
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = mode.Model = normalizedModel ?? "parakeet-v3";
                return;
            case HwLocalApiEngineId.Qwen3Asr:
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = mode.Model = normalizedModel ?? "qwen3-asr-0.6b";
                return;
            // Real engine ids this build cannot serve — macOS has them and the
            // .NET heads do not. The resolver deliberately answers identity and
            // not availability, so the capability verdict is made here, and it
            // is `ENGINE_UNAVAILABLE`: the caller named an engine that exists,
            // which is a different fault from naming one that does not.
            case HwLocalApiEngineId.Nemotron:
            case HwLocalApiEngineId.AppleSpeech:
                throw LocalApiFailureException.From(HyperwhisperCoreMethods.LocalApiBusinessFailure(
                    HwLocalApiErrorCode.EngineUnavailable,
                    $"Engine '{HyperwhisperCoreMethods.LocalApiEngineWireLabel(resolved.Value)}' is not available on this platform.",
                    null));
            default:
                throw new ArgumentException("The requested transcription engine is unsupported.");
        }
    }

    /// <summary>
    /// The <c>engine</c> spelling a response carries, read from the shared
    /// table (issue #356 item 3, review round 1).
    /// </summary>
    /// <remarks>
    /// CLIENT-VISIBLE RESPONSE CHANGE: Qwen3 was labelled <c>qwen3_asr</c> here
    /// and is now <c>qwen3Asr</c>. <c>openapi.yaml</c> publishes
    /// <c>qwen3Asr</c> as the ONLY spelling of that value and macOS has always
    /// emitted it, so this head was answering with a string its own published
    /// contract does not list. <c>qwen3_asr</c> remains an accepted REQUEST
    /// alias on all three heads, so a client echoing an old response still
    /// works — the round trip is closed from both sides now, not only the
    /// accept side. It is also what <c>EngineId::wire_label</c> was added for:
    /// an export no head calls is an export that gets deleted.
    ///
    /// The cloud arm keeps this head's provider id; the shared table covers the
    /// five local ids only.
    /// </remarks>
    private static string EngineLabel(Mode? mode)
    {
        if (mode is null) return string.Empty;
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
            return mode.CloudProvider ?? "cloud";
        if (string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase))
            return HyperwhisperCoreMethods.LocalApiEngineWireLabel(
                mode.LocalParakeetModel?.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) == true
                    ? HwLocalApiEngineId.Qwen3Asr : HwLocalApiEngineId.Parakeet);
        return HyperwhisperCoreMethods.LocalApiEngineWireLabel(HwLocalApiEngineId.WhisperLocal);
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

    /// <summary>
    /// The wire-shape half comes from <c>hw-localapi</c>; the capability half
    /// stays here (issue #356 items 2 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>validate_mode</c> owns the required-key set, every length bound and
    /// both numeric ranges, so this head, macOS and Windows now refuse the same
    /// bodies with the same messages. Two of those are new here and are a
    /// client-visible tightening: a create body must carry the seven keys
    /// <c>openapi.yaml</c> marks <c>required</c> (this head required none), and
    /// <c>sortOrder</c> is bounded to the <c>Int16</c> range (this head had no
    /// bound and crashed outside <c>Int32</c>).
    /// </para>
    /// <para>
    /// Lengths are now counted in Unicode scalar values rather than UTF-16 code
    /// units, which is the only count all three heads can compute identically.
    /// A 60-emoji mode name was 120 units here and is 60 scalars now.
    /// </para>
    /// <para>
    /// NOT shared, deliberately: the cross-field "an enabled
    /// <c>postProcessingMode</c> requires a provider" rule and the catalog
    /// membership checks below. Windows's version of the first reaches into
    /// <c>CustomEndpointManager</c>, <c>LanguageModelInfo</c> and
    /// <c>PlatformHelper</c>, and macOS has none — that is platform capability,
    /// which is exactly what the crate keeps out.
    /// </para>
    /// <para>
    /// <c>sortOrder</c> is the one bound that is validated from the REQUEST and
    /// not from the merged entity, and the asymmetry is deliberate. Every other
    /// bound here — <c>name</c>, <c>language</c>, <c>preset</c>,
    /// <c>postProcessingMode</c>, the prompts, the vocabulary — was already
    /// applied to the merged entity before issue #356, so a stored value that
    /// fails one has always failed. The <c>Int16</c> range is NEW, and this head
    /// (plus backup import) could store an out-of-range <c>sortOrder</c> before
    /// it existed: applying it to the merged entity would make an unrelated
    /// <c>PATCH {"isDefault":true}</c> fail forever, naming a field the client
    /// never sent. macOS (<c>ModesEndpoint.swift</c>) and Windows
    /// (<c>ModesEndpoints.cs</c>) both bound only the patch's own value, so
    /// reading the stored one here would re-open the divergence this issue
    /// closes.
    /// </para>
    /// </remarks>
    private void ValidateMode(Mode mode, ModeDocumentFacts facts, HwLocalApiModeOperation operation)
    {
        mode.Name = mode.Name.Trim();
        var failure = HyperwhisperCoreMethods.LocalApiValidateMode(new HwLocalApiModeValidationInput(
            operation,
            facts.PresentKeys,
            mode.Name,
            mode.Language,
            mode.Preset,
            facts.PostProcessingMode ?? mode.PostProcessingMode,
            facts.SortOrder,
            mode.UserSystemPrompt,
            mode.GeminiCustomPrompt,
            // STORED terms, so `StringArray`'s guard is not the whole answer:
            // backup import and the GUI write this column too, and a null term
            // already in the database would throw inside
            // `FfiConverterSequenceString.AllocationSize` on an unrelated PATCH.
            // Dropping it is right rather than refusing: a null is not a term,
            // it is not something the caller sent, and refusing would be the
            // same fault as bounding a stored `sortOrder` (see above).
            mode.CustomVocabulary?.Where(term => term is not null).ToList()));
        if (failure is not null) throw LocalApiFailureException.From(failure);
        if (mode.PostProcessingMode != 0 && string.IsNullOrWhiteSpace(mode.PostProcessingProvider)) throw new ArgumentException("An enabled post-processing mode requires a provider.");
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

    /// <summary>
    /// What the walk over a mode body observed and could not put on the entity:
    /// the top-level key names, and the two numeric fields as written (issue
    /// #356).
    /// </summary>
    /// <remarks>
    /// The numbers are <c>long</c>, not <c>int</c>, so an out-of-range value
    /// survives the crossing into <c>hw-localapi</c> instead of being
    /// pre-truncated or throwing during the parse. That is what turns
    /// <c>{"sortOrder": 99999999999}</c> — an unhandled
    /// <see cref="FormatException"/> and a bare HTTP 500 before this change —
    /// into an ordinary <c>INVALID_REQUEST</c> naming the bound.
    /// </remarks>
    private readonly record struct ModeDocumentFacts(
        List<string> PresentKeys,
        long? SortOrder,
        long? PostProcessingMode);

    private static ModeDocumentFacts ApplyModeDocument(Mode mode, JsonElement document, bool allowIdentity)
    {
        if (document.ValueKind != JsonValueKind.Object) throw new ArgumentException("Mode body must be a JSON object.");
        var presentKeys = new List<string>();
        long? sortOrder = null;
        long? postProcessingMode = null;
        foreach (var property in document.EnumerateObject())
        {
            presentKeys.Add(property.Name);
            switch (property.Name)
            {
                case "id" when allowIdentity: mode.Id = GuidValue(property); break;
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
                case "punctuation": mode.Punctuation = BooleanValue(property); break;
                case "capitalization": mode.Capitalization = BooleanValue(property); break;
                case "profanityFilter": mode.ProfanityFilter = BooleanValue(property); break;
                case "removeTrailingPeriod": mode.RemoveTrailingPeriod = BooleanValue(property); break;
                case "englishSpelling": mode.EnglishSpelling = OptionalString(property); break;
                // Held as written and handed to the shared bound below. Storing
                // it here would either truncate or throw before `validate_mode`
                // ever sees the number the caller sent.
                case "postProcessingMode":
                    postProcessingMode = IntegerValue(property);
                    if (postProcessingMode is >= int.MinValue and <= int.MaxValue)
                        mode.PostProcessingMode = (int)postProcessingMode.Value;
                    break;
                case "postProcessingProvider": mode.PostProcessingProvider = OptionalString(property); break;
                case "languageModel": mode.LanguageModel = OptionalString(property); break;
                case "localPostProcessingModel": mode.LocalPostProcessingModel = OptionalString(property); break;
                case "userSystemPrompt": mode.UserSystemPrompt = OptionalString(property); break;
                case "customInstructions": mode.CustomInstructions = OptionalString(property); break;
                case "enableScreenOCR": mode.EnableScreenOCR = BooleanValue(property); break;
                case "cloudPostProcessingModel": mode.CloudPostProcessingModel = RequiredString(property); break;
                case "customVocabulary": mode.CustomVocabulary = StringArray(property); break;
                case "isDefault": mode.IsDefault = BooleanValue(property); break;
                case "sortOrder":
                    sortOrder = IntegerValue(property);
                    if (sortOrder is >= int.MinValue and <= int.MaxValue)
                        mode.SortOrder = (int)sortOrder.Value;
                    break;
                case "useStreamingTranscription": break; // Legacy wire-only field; no EF storage exists.
                // AN UNRECOGNISED KEY IS IGNORED, NOT REJECTED (issue #356
                // item 2). `openapi.yaml` documents five keys as "Windows only.
                // macOS ignores this key", so the published contract actively
                // invites a cross-platform client to send keys a given head does
                // not implement — and macOS and Windows both drop an unmapped
                // key inside their JSON decoders. This head was the only one
                // that threw. `mode_key_classification` is the authoritative
                // union, and it is consulted rather than assumed so that a key
                // this switch has not caught up with is distinguishable, in the
                // log, from a client's typo.
                default:
                    LogIgnoredModeKey(property.Name);
                    break;
            }
        }
        return new ModeDocumentFacts(presentKeys, sortOrder, postProcessingMode);
    }

    private static void LogIgnoredModeKey(string key)
    {
        var classification = HyperwhisperCoreMethods.LocalApiModeKeyClassification(key);
        if (classification == HwLocalApiModeKeyClass.Unknown)
            Debug.WriteLine($"Local API: ignoring unrecognised mode field '{key}'.");
        else
            Debug.WriteLine($"Local API: ignoring documented mode field '{key}' ({classification}) this head does not store.");
    }

    // A WRONG-TYPED VALUE IS `INVALID_REQUEST`, NOT A MISSING CAPABILITY
    // (issue #356). `JsonElement.GetBoolean`/`GetString`/`GetGuid` throw
    // `InvalidOperationException` when the value is of another JSON kind, and
    // this head's middleware answers that with HTTP 200 `ENGINE_UNAVAILABLE` —
    // so `{"punctuation":"yes"}` was reported as an absent app capability.
    // `GetInt32` was worse: a number outside `Int32` raises `FormatException`,
    // which NO catch in that middleware handles, so `{"sortOrder":99999999999}`
    // was an unhandled HTTP 500 with no envelope at all. Every accessor here
    // tests the kind first and raises `ArgumentException`, which is the body
    // error the middleware already knows how to answer.
    private static string RequiredString(JsonProperty property) => property.Value.ValueKind == JsonValueKind.String
        ? property.Value.GetString() ?? throw new ArgumentException($"'{property.Name}' must be a string.")
        : throw new ArgumentException($"'{property.Name}' must be a string.");

    private static string? OptionalString(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => property.Value.GetString(),
        _ => throw new ArgumentException($"'{property.Name}' must be a string or null."),
    };

    private static bool BooleanValue(JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException($"'{property.Name}' must be true or false."),
    };

    private static Guid GuidValue(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var value)
            ? value
            : throw new ArgumentException($"'{property.Name}' must be a UUID string.");

    /// <summary>
    /// A JSON integer, as written. Out-of-range is the shared bound's answer,
    /// not a parse error — see <see cref="ModeDocumentFacts"/>.
    /// </summary>
    private static long IntegerValue(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value)
            ? value
            : throw new ArgumentException($"'{property.Name}' must be a whole number.");

    /// <summary>
    /// A JSON array of strings, in which <c>null</c> is not a string.
    /// </summary>
    /// <remarks>
    /// <c>Deserialize&lt;List&lt;string&gt;&gt;</c> accepted <c>["ok", null]</c>
    /// and produced a list with a null element — System.Text.Json erases
    /// nullable reference types unless <c>RespectNullableAnnotations</c> is set,
    /// which it is nowhere in this repo. That element cannot cross the FFI: the
    /// generated <c>FfiConverterSequenceString.AllocationSize</c> sums
    /// <c>Encoding.UTF8.GetByteCount(item)</c> with no per-element guard, so the
    /// first null throws before Rust sees the call (issue #356, review round 1).
    /// The guard is HERE, at the one place this head parses a string array,
    /// rather than at each call site that hands a list to <c>hw-localapi</c>: a
    /// value that cannot cross the boundary should never be built.
    /// </remarks>
    private static List<string>? StringArray(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Null) return null;
        if (property.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"'{property.Name}' must be an array of strings.");
        var items = new List<string>();
        foreach (var element in property.Value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } term)
                throw new ArgumentException($"'{property.Name}' must be an array of strings.");
            items.Add(term);
        }
        return items;
    }

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

    /// <summary>
    /// The `/recording/*` routes' half of the same failure, through the same
    /// mapping (issue #356 item 4).
    /// </summary>
    /// <remarks>
    /// This did a partial two-way split of the same four-case enum —
    /// <c>BackendUnavailable</c> became an <see cref="InvalidOperationException"/>
    /// (HTTP 200 <c>ENGINE_UNAVAILABLE</c>) and everything else an
    /// <see cref="ArgumentException"/> (HTTP 400 <c>INVALID_REQUEST</c>) — and
    /// it discarded the message on both arms. Routing it through
    /// <see cref="LocalApiSharedFailure.TranscriptionFailure"/> is what stops
    /// the two paths drifting: a cancelled recording and a cancelled
    /// `/transcribe` now answer with the same code and the same wording.
    /// <c>BackendUnavailable</c> still reaches <c>ENGINE_UNAVAILABLE</c>, which
    /// is what `/recording/toggle`'s existing assertion pins.
    /// </remarks>
    private static void ThrowWorkflowFailure(PortableTranscriptionResult result)
    {
        if (result.IsSuccess) return;
        throw LocalApiSharedFailure.TranscriptionFailure(result.Failure);
    }
}
