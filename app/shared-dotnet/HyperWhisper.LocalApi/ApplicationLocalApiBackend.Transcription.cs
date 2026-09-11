using System.Diagnostics;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.SharedCore;
using HyperWhisper.SpeechOutput;
using HyperWhisper.TranscriptionRouting;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

public sealed partial class ApplicationLocalApiBackend
{
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
            // Match the Windows Local API contract: /transcribe declines the AI
            // REWRITE, even when the resolved Mode enables it. Callers that want
            // enhancement use the separate /post-process route.
            //
            // Forcing the mode to 0 also puts `SpeechOutputProcessor` on its
            // `PostProcessingMode.Off` arm, which is the arm that runs the
            // deterministic passes — filler-word removal (gated on the user's
            // own setting, through `OutputOptions` above) and dictated
            // "new line" / "new paragraph" break commands. Vocabulary
            // replacements run on every arm. Those three are the user's own
            // configuration and contain no LLM, so they belong to this route's
            // `text` exactly as they do on Windows (issues #495, #498, #530).
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

        IReadOnlyList<VocabularyItem> vocabularyItems = _vocabulary is null
            ? []
            : await _vocabulary.ListAsync(cancellationToken).ConfigureAwait(false);
        // Shared core rule: sanitize, drop empties, dedupe case-insensitively.
        // Uncapped — the local API hands the whole vocabulary to the workflow,
        // and each provider applies its own cap downstream.
        IReadOnlyList<string> vocabulary = vocabularyItems.Count == 0
            ? []
            : SharedCoreBridge.NormalizeVocabularyTerms([.. vocabularyItems.Select(item => item.Word)], null);
        // The word/replacement pairs, which are a DIFFERENT thing from the
        // prompt hints above: the hints bias the engine, these rewrite the
        // finished transcript. The backend only ever sent the hints, so
        // /transcribe returned "eta" for a user whose rule says "estimated time
        // of arrival" while dictation of the same audio in the same Mode
        // returned the expansion (issue #530). Built the same way
        // `ApplicationShellViewModel.BuildVocabularyReplacements` builds them
        // for the GUI path, off the same repository rows. Not normalized through
        // the shared core: `NormalizeVocabularyTerms` is the prompt-hint rule
        // (it strips punctuation and collapses whitespace), and a replacement
        // rule must match the word the user actually typed.
        IReadOnlyList<PortableVocabularyReplacement> replacements =
        [
            .. vocabularyItems
                .Where(item => !string.IsNullOrWhiteSpace(item.Word) && !string.IsNullOrWhiteSpace(item.Replacement))
                .Select(item => new PortableVocabularyReplacement(item.Word, item.Replacement!)),
        ];
        return new(
            languageOverride ?? mode?.Language,
            mode?.Name,
            mode?.Id,
            mode,
            vocabulary,
            applicationContext,
            VocabularyReplacements: replacements,
            // Mode-level word/replacement pairs have no portable storage yet;
            // `ApplicationShellViewModel` passes the same empty list.
            ModeVocabularyReplacements: [],
            // Without this the workflow falls back to
            // `BuildDefaultOutputOptions`, which hard-codes
            // `RemoveFillerWords: true` — so /transcribe stripped filler words
            // even for a user who had turned that setting OFF, the opposite of
            // the Windows defect in issue #498. The composed application hands
            // in the SAME projection its own dictation path uses, so the two
            // cannot disagree about one user's settings.
            OutputOptions: _outputOptions?.Invoke(mode),
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

        // THE CLOUD HALF OF THE ONE ALIAS TABLE (issue #575). The two strings
        // below used to be a literal `switch` that only re-cased them:
        //
        //     "microsoftazurespeech" => "microsoftAzureSpeech",
        //     "googlespeech"         => "googleSpeech",
        //
        // Both are `legacyCloudProviderAliases` in `cloud-stt-catalog.json`, so
        // `normalize_cloud_provider` folds them onto
        // `(hyperwhisper, azureMaiTranscribe | geminiTranscribe)` — which is
        // what Windows `ApplyEngineModel` and macOS `applyEngineModel` have
        // always done with them. `openapi.yaml` carves out no platform, so one
        // documented request used to route to a different vendor here than
        // there.
        //
        // #575 describes the old behaviour as spending the caller's own API key.
        // MEASURED, IT IS WORSE THAN THAT AND NOT BYOK AT ALL. Re-casing landed
        // on `CloudTranscriptionProvider.GoogleChirp` / `AzureMai`, which
        // `CloudCredentialSource.AccountFor` maps to `LicenseKey` — so the
        // credential was already the HyperWhisper one. But
        // `ModeAwareTranscriptionRouter.BuildCloudRequest` fills `BaseUrl` for
        // `HyperWhisperCloud` ONLY, and both of those providers delegate to
        // `hyperwhisper_cloud::build_routed_request`, which refuses an empty
        // `base_url` before any I/O. Those two engine values therefore could not
        // transcribe on this head at ALL: every request died as
        // `INVALID_REQUEST` without leaving the process. (`googleChirp3` is also
        // a tier catalog v8 retired, which is the same story from the data side.)
        // Folding puts the request back on a route that has a base URL, and the
        // test asserts exactly that.
        var providerNormalization = HyperwhisperCoreMethods.CloudSttNormalizeCloudProvider(normalizedEngine);
        var cloud = normalizedEngine == "cloud"
            ? "hyperwhisper"
            : providerNormalization.@provider ?? normalizedEngine;
        if (CloudProviders.Contains(cloud))
        {
            // Captured BEFORE the column is overwritten: deciding whether the
            // inherited model is foreign needs the provider it came FROM.
            var priorProvider = mode.CloudProvider;
            var priorModel = mode.CloudTranscriptionModel;

            mode.ProviderType = "cloud";
            mode.CloudProvider = cloud;
            mode.Model = "cloud";
            if (!string.IsNullOrEmpty(providerNormalization.@accuracyTier))
            {
                // A FOLDED ENGINE ALSO CHOOSES THE TIER. HyperWhisper Cloud
                // dispatches on `CloudAccuracyTier`, so folding the provider
                // without the tier would route the request to whatever tier the
                // baseline mode happened to carry — Scribe v2 for a transient
                // mode — and `engine: "googlespeech"` would not run Gemini at
                // all. This arm is where the tier is written, exactly as
                // Windows writes it.
                mode.CloudAccuracyTier = providerNormalization.@accuracyTier;
                if (normalizedModel is not null) mode.CloudTranscriptionModel = normalizedModel;
                // Otherwise LEAVE THE MODEL COLUMN ALONE — the #528 correction.
                // Writing the tier default here would silently drop a sub-model
                // the caller had pinned INSIDE the same tier, and
                // `azureMaiTranscribe` carries two models at different rates, so
                // that changed what ran and what it cost. Nothing stale leaks
                // through: `DispatchedCloudModelId` validates the surviving id
                // against the NEW tier on the send path and in `ModelLabel`, and
                // falls back to the tier default when it does not belong.
                //
                // The foreign-model guard is deliberately NOT reached here.
                // Provider is now HyperWhisper Cloud, which #566 exempts for the
                // same reason: the tier resolution already heals the column, and
                // a second opinion written here could disagree with the run.
                return;
            }
            if (normalizedModel is not null)
            {
                mode.CloudTranscriptionModel = normalizedModel;
                return;
            }

            ApplyForeignModelGuard(mode, cloud, priorProvider, priorModel);
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
    /// Drop a <c>cloudTranscriptionModel</c> the request's NEW engine does not
    /// own, so <c>{mode_id, engine}</c> cannot run another vendor's model
    /// (issue #566).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mixed request inherits the baseline mode's model column. Nothing used
    /// to clear it, so <c>{mode_id: &lt;a Deepgram mode&gt;, engine: "openai"}</c>
    /// kept <c>nova-3-general</c> and <see cref="ModeAwareTranscriptionRouter.BuildCloudRequest"/>
    /// put it in the OpenAI request body verbatim. A rejection is the good
    /// outcome; an id that happens to be valid for the new vendor runs, at that
    /// model's price, without the caller asking for it.
    /// </para>
    /// <para>
    /// Windows has guarded this since #528 and macOS since #533; this is the
    /// portable half, with the same two-part test. An inherited id is kept when
    /// EITHER the mode was already on this provider — the caller is re-asserting
    /// the engine, so their saved sub-model stands, including providers such as
    /// HyperWhisper Cloud and Grok whose sub-models are not catalog model rows —
    /// OR the id really is one of this provider's models.
    /// </para>
    /// <para>
    /// The membership half MUST resolve aliases, and this head had no
    /// alias-resolving lookup until now, which is why #565 filed the defect
    /// rather than fixing it in passing.
    /// <see cref="SharedCoreBridge.CloudSttContainsModel"/> is an exact,
    /// case-sensitive scan, so a legacy-but-serviceable id such as AssemblyAI
    /// <c>universal</c> (which resolves to the catalogued <c>universal-2</c>)
    /// would read as foreign and be silently upgraded to a different-priced
    /// model. That is exactly the failure #528's second correction exists to
    /// prevent, so the guard goes through
    /// <see cref="ModeAwareTranscriptionRouter.CloudModelBelongsToProvider"/>,
    /// which applies the shared <c>hw-catalog</c> alias table first.
    /// </para>
    /// <para>
    /// HyperWhisper Cloud is exempt for the reason Windows records: it
    /// dispatches on the accuracy tier, and both the send path and
    /// <see cref="ModelLabel"/> already run the surviving id through
    /// <see cref="ModeAwareTranscriptionRouter.DispatchedCloudModelId"/>, which
    /// heals a blank, foreign, live-only or out-of-tier value on its own.
    /// Writing a guess here would only add a second opinion that can disagree
    /// with the run.
    /// </para>
    /// <para>
    /// Meta is the mirror image and keeps its own arm, ABOVE the two-part test.
    /// <c>metaMuse</c> has exactly one model, so a stored id that is not it can
    /// only be stale or foreign — there is no legitimate sub-model to preserve,
    /// and letting the "the caller re-asserted this engine" half stand would
    /// hand Meta a Deepgram id for a mode already saved on <c>meta</c>. That is
    /// the very defect this method exists to close, so the unconditional
    /// assignment stays. Only the hard-coded <c>muse-voice-transcribe-1.0</c>
    /// literal goes: the value now comes from the catalog, like every other
    /// provider's. macOS handles <c>engine: "meta"</c> the same unconditional
    /// way, in a block above its own guard.
    /// </para>
    /// </remarks>
    private static void ApplyForeignModelGuard(
        Mode mode, string cloud, string? priorProvider, string? priorModel)
    {
        if (!ModeAwareTranscriptionRouter.TryMapProvider(cloud, out var provider)) return;
        if (provider == CloudTranscriptionProvider.HyperWhisperCloud) return;
        if (provider == CloudTranscriptionProvider.Meta)
        {
            mode.CloudTranscriptionModel = ModeAwareTranscriptionRouter.DefaultCloudModelId(provider);
            return;
        }

        var samePriorProvider =
            ModeAwareTranscriptionRouter.TryMapProvider(priorProvider, out var mapped)
            && mapped == provider;
        var belongsToProvider = !string.IsNullOrWhiteSpace(priorModel)
            && (samePriorProvider
                || ModeAwareTranscriptionRouter.CloudModelBelongsToProvider(provider, priorModel));
        if (belongsToProvider) return;

        mode.CloudTranscriptionModel = ModeAwareTranscriptionRouter.DefaultCloudModelId(provider);
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

    /// <summary>
    /// The <c>model</c> a <c>/transcribe</c> response carries: the model that
    /// ACTUALLY RAN, not the one the request named (issue #533).
    /// </summary>
    /// <remarks>
    /// The cloud arm used to be a bare <c>mode.CloudTranscriptionModel ?? ""</c>.
    /// That field is legitimately UNSET for a HyperWhisper Cloud mode — the
    /// provider chooses by accuracy tier, and <c>ApplyTranscriptionOverrides</c>
    /// writes nothing for <c>engine: "cloud"</c> with no <c>model</c> — so the
    /// endpoint answered <c>model: ""</c> for a run that really did dispatch a
    /// model. Windows fixed the same thing in #528; this is the portable half.
    ///
    /// It resolves through <see cref="ModeAwareTranscriptionRouter.DispatchedCloudModelId"/>,
    /// which is the send path's own resolution, so the label is the dispatched
    /// model by construction and the two cannot drift. An id that is blank,
    /// live-only, out-of-tier, or a legacy tier spelling heals in both places at
    /// once.
    ///
    /// An unmappable <c>cloudProvider</c> (any string can reach the field
    /// through a Local API mode write or a backup restore) has no send path to
    /// consult, so it keeps the raw stored value rather than being forced onto
    /// some other provider's default.
    /// </remarks>
    private static string ModelLabel(Mode? mode)
    {
        if (mode is null) return string.Empty;
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
            return ModeAwareTranscriptionRouter.TryMapProvider(mode.CloudProvider, out var provider)
                ? ModeAwareTranscriptionRouter.DispatchedCloudModelId(
                    provider, mode.CloudAccuracyTier, mode.CloudTranscriptionModel)
                : mode.CloudTranscriptionModel ?? string.Empty;
        return string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase)
            ? mode.LocalParakeetModel ?? mode.Model ?? string.Empty
            : mode.ModelType ?? mode.Model ?? string.Empty;
    }


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
