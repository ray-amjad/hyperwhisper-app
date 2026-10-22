using System.IO;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;
using SharedApi = HyperWhisper.LocalApi;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// `POST /transcribe` — resolve the request against a saved or transient Mode,
/// dispatch through the orchestrator, and return the transcript.
/// <para>
/// The shared server has already read the audio: a multipart <c>audio</c>
/// part, a decoded <c>audio_base64</c>, or a <c>file</c> path it checked
/// against <see cref="LocalApiFilePolicy"/> and copied into memory. So the
/// bytes this backend transcribes are always its own copy, which the caller
/// cannot swap once the path was approved (#740). They are written to a
/// private scratch file, because the orchestrator and providers take a path.
/// </para>
/// <para>
/// `/post-process` is the formatting endpoint, so this route declines the AI
/// rewrite even when the resolved Mode enables it. That is the LLM only. The
/// orchestrator's deterministic passes still run and their result is what the
/// `text` field carries: filler-word removal (a global app setting with no LLM
/// in it), dictated "new line" / "new paragraph" break commands, and the user's
/// vocabulary replacements (issues #495, #498).
/// </para>
/// </summary>
internal sealed partial class WindowsLocalApiBackend
{
    public async ValueTask<SharedApi.TranscriptionResult> TranscribeAsync(
        SharedApi.AudioUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var orchestrator = _server.TranscriptionOrchestrator
            ?? throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Transcription orchestrator not initialized");

        // Resolve Mode — saved, saved+overrides, or engine-only transient.
        var effectiveMode = ResolveMode(upload);

        // Dispatch to whichever local engine the resolved Mode names.
        // Without this, `engine=parakeet` requests silently fall through
        // to Whisper (or fail with a misleading error).
        var localProvider = IsParakeetMode(effectiveMode)
            ? _server.ParakeetTranscriptionProvider
            : _server.LocalTranscriptionProvider;
        var applicationContext = LocalApiApplicationContext.From(upload.ApplicationContext)?.ToApplicationContext();

        var (audioPath, readLock) = WriteScratchAudio(upload);
        // Quit and the start-up sweep reach this file through the registry when
        // this request never gets to its finally (#1476).
        LocalApiScratchFiles.Track(audioPath, readLock);

        // Held from the Parakeet model check until this request's transcription has
        // ended, so no other caller's idle reload swaps the model in between (#1608).
        IDisposable? parakeetModelLease = null;
        try
        {
            if (!LocalModeModel.IsCloud(effectiveMode))
            {
                try
                {
                    parakeetModelLease = await EnsureLocalModelLoadedAsync(_server, effectiveMode, localProvider, cancellationToken);
                }
                catch (SharedApi.LocalApiFailureException)
                {
                    throw;
                }
                catch (TranscriptionException tex)
                {
                    throw WindowsLocalApiFailure.Transcription(tex);
                }
                catch (OperationCanceledException)
                {
                    throw WindowsLocalApiFailure.Business(
                        LocalApiErrorCode.Timeout,
                        "Transcription was cancelled while loading the local model");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("LocalAPI /transcribe: local model load failed", ex);
                    throw WindowsLocalApiFailure.Business(LocalApiErrorCode.EngineUnavailable, ex.Message);
                }
            }

            // Whisper's worker start and model load land here when this request
            // paid them (a cold start after an idle exit), so load_ms holds
            // them and decode_ms the rest (#1932).
            using var whisperLoad = WhisperLoadTiming.Begin();
            var started = DateTime.UtcNow;
            TranscriptionResult result;
            try
            {
                result = await orchestrator.TranscribeAsync(
                    audioPath: audioPath,
                    mode: effectiveMode,
                    vocabulary: effectiveMode.CustomVocabulary,
                    localTranscriptionProvider: localProvider,
                    applicationContext: applicationContext,
                    cancellationToken: cancellationToken,
                    callSite: TranscriptionCallSite.Api,
                    applyAiPostProcessing: false);
            }
            catch (TranscriptionException tex)
            {
                throw WindowsLocalApiFailure.Transcription(tex);
            }
            catch (OperationCanceledException)
            {
                throw WindowsLocalApiFailure.Business(LocalApiErrorCode.Timeout, "Transcription was cancelled");
            }
            catch (Exception ex)
            {
                LoggingService.Error("LocalAPI /transcribe: orchestrator threw", ex);
                throw WindowsLocalApiFailure.Business(LocalApiErrorCode.TranscriptionFailed, ex.Message);
            }

            var latencyMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds);
            var loadMs = (int)Math.Round(whisperLoad.Elapsed.TotalMilliseconds);
            return BuildResponse(result, effectiveMode, latencyMs, loadMs);
        }
        finally
        {
            parakeetModelLease?.Dispose();
            readLock.Dispose();
            try { File.Delete(audioPath); }
            catch (Exception ex)
            {
                LoggingService.Debug($"LocalAPI /transcribe: failed to delete temp file {audioPath}: {ex.Message}");
            }
            LocalApiScratchFiles.Untrack(audioPath);
        }
    }

    // =========================================================================
    // Scratch audio
    // =========================================================================

    /// <summary>
    /// Write the upload to a private scratch file and hold a read lock on it.
    /// Readers can still open the file, but another HyperWhisper process's
    /// start-up sweep cannot delete it while this request is live (#1476).
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so the smoke suite can drive the scratch-file contract
    /// through the production writer rather than a copy of it.
    /// </remarks>
    internal static (string Path, FileStream ReadLock) WriteScratchAudio(SharedApi.AudioUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var tempPath = Path.Combine(
            LocalApiScratchFiles.UploadDirectory(Path.GetTempPath()),
            $"{LocalApiScratchFiles.UploadPrefix}{Guid.NewGuid():N}.{ScratchExtension(upload)}");
        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(upload.Content.Span);
            }

            var readLock = new FileStream(
                tempPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                });
            return (tempPath, readLock);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tempPath); }
            catch (Exception cleanupEx)
            {
                LoggingService.Debug($"LocalAPI /transcribe: failed to delete temp file {tempPath}: {cleanupEx.Message}");
            }

            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.AudioDecodeFailed,
                $"Failed to write decoded audio to temp file: {ex.Message}");
        }
    }

    /// <summary>
    /// The scratch file's extension, without the dot.
    /// </summary>
    /// <remarks>
    /// An <c>audio_base64</c> upload is named from the caller's own
    /// <c>mime_type</c> (<see cref="SharedApi.AudioUpload.DeclaredMimeType"/>),
    /// through the old Windows table, so <c>audio/webm;codecs=opus</c> stays
    /// <c>.webm</c> and <c>audio/aac</c> stays <c>.aac</c>. The shared server's
    /// synthetic <c>local-api-upload.wav</c> name cannot be trusted for that:
    /// its table folds every alias it does not know to <c>audio/wav</c>.
    /// Otherwise the upload's own file name wins when it carries a known audio
    /// extension (a <c>file</c> path or a multipart part), then the content type.
    /// </remarks>
    internal static string ScratchExtension(SharedApi.AudioUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return string.IsNullOrWhiteSpace(upload.DeclaredMimeType)
            ? ScratchExtension(upload.FileName, upload.ContentType)
            : ExtensionForMime(upload.DeclaredMimeType);
    }

    private static string ScratchExtension(string? fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return extension is "wav" or "m4a" or "mp3" or "flac" or "ogg" or "webm" or "aac" or "mp4"
            ? extension
            : ExtensionForMime(contentType);
    }

    private static string ExtensionForMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return "wav";
        var normalizedMime = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalizedMime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/m4a" or "audio/x-m4a" or "audio/mp4" => "m4a",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/ogg" or "audio/x-ogg" or "audio/vorbis" => "ogg",
            "audio/webm" => "webm",
            "audio/aac" => "aac",
            _ => "wav"
        };
    }

    // =========================================================================
    // Response
    // =========================================================================

    /// <summary>
    /// Projects a finished <see cref="TranscriptionResult"/> onto the wire shape.
    /// </summary>
    /// <remarks>
    /// The <c>text</c> field carries <see cref="TranscriptionResult.FinalText"/>,
    /// NOT <c>RawText</c>. <c>RawText</c> is the provider's output before the
    /// orchestrator's STEP 3; <c>FinalText</c> is that string after the
    /// deterministic passes the orchestrator always runs — filler-word removal,
    /// dictated "new line" / "new paragraph" break commands, and the user's
    /// vocabulary replacements.
    ///
    /// This route reads the raw field before, which silently discarded all three
    /// (issues #495, #498). It is a pair with the <c>applyAiPostProcessing:
    /// false</c> argument above: with the old orchestrator gate, reading
    /// <c>FinalText</c> alone would still have returned the provider's raw
    /// string, and flipping the gate alone would still have been thrown away
    /// here. Both sites have to say "deterministic passes, no LLM" together, so
    /// they are asserted together in HyperWhisper.SmokeTests.
    ///
    /// Extracted from the handler lambda so that assertion can reach it, in the
    /// same `internal static` style as <see cref="ApplyEngineModel"/> and
    /// <see cref="BuildTransientMode"/>.
    ///
    /// KNOWN EDGE, DELIBERATE: a transcript that is ONLY a break command ("New
    /// paragraph.") or only fillers ("Um, uh") reduces to an empty string, so
    /// this route can now answer <c>ok: true</c> with an empty <c>text</c> where
    /// it previously echoed the provider's string. That is not special-cased
    /// here, because the GUI dictation path inserts exactly the same empty
    /// result for the same audio and Mode — raising a business failure only on
    /// the API would recreate the divergence this change removes. The orchestrator's
    /// no-speech guard still covers the real failure: a provider that returns
    /// nothing at all.
    /// </remarks>
    /// <param name="latencyMs">The whole transcription, load included.</param>
    /// <param name="loadMs">
    /// The part of it spent starting the Whisper worker and loading the model;
    /// zero when the worker was warm, or for another engine. decode_ms is the rest.
    /// </param>
    internal static SharedApi.TranscriptionResult BuildResponse(
        TranscriptionResult result,
        Mode effectiveMode,
        int latencyMs,
        int loadMs = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        loadMs = Math.Clamp(loadMs, 0, Math.Max(0, latencyMs));
        return new SharedApi.TranscriptionResult(
            result.FinalText,
            EngineLabel(effectiveMode),
            ModelLabel(effectiveMode),
            EffectiveLanguage(effectiveMode),
            loadMs,
            latencyMs - loadMs,
            latencyMs);
    }

    /// <returns>
    /// For a Parakeet-family Mode, the model lease the caller holds until its
    /// transcription has ended (#1608); null for Whisper.
    /// </returns>
    private static async Task<IDisposable?> EnsureLocalModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider? provider,
        CancellationToken cancellationToken)
    {
        if (provider == null)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Local transcription provider not initialized");
        }

        if (IsParakeetMode(mode))
        {
            return await EnsureParakeetModelLoadedAsync(server, mode, provider, cancellationToken);
        }

        await EnsureWhisperModelLoadedAsync(server, mode, provider, cancellationToken);
        return null;
    }

    private static async Task EnsureWhisperModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is not TranscriptionService transcriptionService)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Whisper local provider is not available");
        }

        var modelType = string.IsNullOrWhiteSpace(mode.ModelType) ? mode.Model : mode.ModelType;
        var modelInfo = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == modelType);
        if (modelInfo == null)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.ModelNotFound,
                $"Whisper model '{modelType}' is not recognized");
        }

        var modelService = server.WhisperModels;
        if (modelService == null)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Whisper model service not initialized");
        }

        if (!modelService.IsModelDownloaded(modelInfo))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.ModelNotInstalled,
                $"Whisper model '{modelInfo.DisplayName}' is not installed",
                "Open HyperWhisper and download the model you want to use before calling /transcribe.");
        }

        var modelPath = modelService.GetModelPath(modelInfo);
        // Not while an unload is pending (#1609): the model is about to go, so
        // queue behind the unload and load it again.
        if (transcriptionService.IsLoadedAndStaying() &&
            string.Equals(transcriptionService.LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoggingService.Info($"LocalAPI /transcribe: Loading Whisper model {modelInfo.DisplayName}");
        await transcriptionService.InitializeAsync(modelPath, _ => { }, cancellationToken);
    }

    private static async Task<IDisposable> EnsureParakeetModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is not ParakeetTranscriptionService parakeetService)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Parakeet local provider is not available");
        }

        var modelId = string.IsNullOrWhiteSpace(mode.LocalParakeetModel)
            ? mode.Model
            : mode.LocalParakeetModel;
        var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
        if (modelInfo == null)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.ModelNotFound,
                $"Parakeet-family model '{modelId}' is not recognized");
        }

        var modelService = server.ParakeetModels;
        if (modelService == null)
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.EngineUnavailable,
                "Parakeet model service not initialized");
        }

        if (!modelService.IsModelDownloaded(modelInfo))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.ModelNotInstalled,
                $"Parakeet-family model '{modelInfo.DisplayName}' is not installed",
                "Open HyperWhisper and download the model you want to use before calling /transcribe.");
        }

        var language = mode.Language == "auto" ? null : mode.Language;

        if (parakeetService.NeedsReload(modelInfo.Id, mode.Language))
        {
            LoggingService.Info($"LocalAPI /transcribe: Loading Parakeet-family model {modelInfo.DisplayName}");
        }

        // Never InitializeAsync here: that is the GUI's mode switch, and it cancels the
        // transcription in flight, e.g. the user's Transcribe File job (#1608). This waits
        // until the daemon is idle, then reloads (a warm daemon that fits returns at once);
        // a client that disconnects stops waiting. The lease keeps this request's model
        // loaded until its transcription has ended.
        return await parakeetService.ReloadWhenIdleAsync(
            modelInfo.Id,
            modelService.GetModelDirectory(modelInfo),
            language,
            cancellationToken);
    }

    // =========================================================================

    // =========================================================================
    // Mode resolution
    // =========================================================================

    /// <summary>
    /// Pick the Mode to drive the transcription. Three branches, matching the
    /// macOS `TranscribeEndpoint.resolve(...)`:
    ///   1. `mode_id` alone → fetch the saved Mode and use as-is.
    ///   2. `mode_id` + any of engine/model/language → clone into a transient
    ///      Mode, apply the per-call overrides.
    ///   3. No `mode_id` → require `engine`, build a transient Mode from scratch.
    /// Transient modes are never persisted — they only exist for the request.
    /// </summary>
    private static Mode ResolveMode(SharedApi.AudioUpload req)
    {
        var trimmedEngine = req.Engine?.Trim();
        var trimmedModel = req.Model?.Trim();
        var trimmedLanguage = req.Language?.Trim();
        var hasOverride = !string.IsNullOrEmpty(trimmedEngine)
            || !string.IsNullOrEmpty(trimmedModel)
            || !string.IsNullOrEmpty(trimmedLanguage);

        var modeId = req.ModeId?.Trim();
        if (!string.IsNullOrEmpty(modeId))
        {
            if (!Guid.TryParse(modeId, out var guid))
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.ModeNotFound,
                    $"No mode with id '{modeId}'");
            }
            if (!hasOverride)
            {
                return stored;
            }
            return BuildTransientMode(stored, trimmedEngine, trimmedModel, trimmedLanguage);
        }

        if (string.IsNullOrEmpty(trimmedEngine))
        {
            throw WindowsLocalApiFailure.Business(
                LocalApiErrorCode.InvalidRequest,
                "Provide 'mode_id' or 'engine'");
        }

        return BuildTransientMode(baseline: null, trimmedEngine, trimmedModel, trimmedLanguage);
    }

    /// <summary>
    /// Construct an in-memory Mode that the orchestrator can read but which
    /// is never added to the EF context. Either seeded from `baseline` (saved
    /// mode used as defaults) or built from scratch, then patched with the
    /// per-call overrides.
    /// </summary>
    internal static Mode BuildTransientMode(Mode? baseline, string? engine, string? model, string? language)
    {
        Mode mode;
        if (baseline != null)
        {
            mode = LocalApiTransientMode.Create(
                baseline,
                "__local_api_transient__",
                LocalApiTransientModeFields.SharedAndTranscription);
        }
        else
        {
            mode = new Mode
            {
                Id = Guid.NewGuid(),
                Name = "__local_api_transient__",
                Preset = "hyper",
                Language = "auto",
                Model = "base",
                Punctuation = true,
                Capitalization = true,
                ProfanityFilter = false,
                CustomInstructions = "",
                PostProcessingMode = 0,
                ProviderType = "local",
                LocalEngine = "whisper",
                // Defaults mirror the GUI/default-mode recommendation
                // (ModeDefaults.cs): ElevenLabs Scribe v2 + Anthropic Claude Haiku 5.5.
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudPostProcessingModel = "anthropic:claude-haiku-5-5",
                SortOrder = int.MaxValue,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow
            };
        }

        if (!string.IsNullOrEmpty(language))
        {
            mode.Language = language;
        }

        if (!string.IsNullOrEmpty(engine))
        {
            ApplyEngineModel(mode, engine!, model);
        }
        else if (!string.IsNullOrEmpty(model))
        {
            // Engine implied by baseline — patch the right model field.
            if (LocalModeModel.IsCloud(mode))
            {
                mode.CloudTranscriptionModel = model;
            }
            else if (IsParakeetMode(mode))
            {
                mode.LocalParakeetModel = model;
                mode.Model = model;
            }
            else
            {
                mode.ModelType = model;
                mode.Model = model;
            }
        }

        return mode;
    }

    /// <summary>
    /// The refusal for <c>engine: whisper</c> with no or a blank <c>model</c>
    /// (issue #1493). Internal so the smoke tests pin the exact wording.
    /// </summary>
    internal const string WhisperModelRequiredMessage = "A Whisper model is required.";

    /// <summary>The hint that goes with <see cref="WhisperModelRequiredMessage"/>.</summary>
    internal const string WhisperModelRequiredHint = "Send model, for example base.en.";

    /// <summary>
    /// Encode an engine + (optional) model pair onto a Mode's provider/engine
    /// fields. Recognized engine strings are: any cloud-provider identifier
    /// ("openai", "groq", "deepgram", "hyperwhisper", "gemini", …) plus "cloud"
    /// (treated as HyperWhisperCloud), "whisper" / "whisperlocal", and
    /// "parakeet", and "qwen3_asr". Unknown strings are rejected so callers do not silently run
    /// a different engine than they requested.
    /// </summary>
    internal static void ApplyEngineModel(Mode mode, string engine, string? model)
    {
        var normalized = engine.Trim().ToLowerInvariant();
        var providerNormalization = HyperWhisper.Services.AppClassification.CloudSttCatalog.Shared
            .NormalizeCloudProvider(normalized);
        CloudTranscriptionProvider cloudProvider;
        if (normalized == "cloud")
        {
            cloudProvider = CloudTranscriptionProvider.HyperWhisperCloud;
        }
        else
        {
            cloudProvider = CloudTranscriptionProviderExtensions.FromIdentifier(providerNormalization.Provider);
        }

        if (cloudProvider != CloudTranscriptionProvider.None)
        {
            // Capture BEFORE overwriting CloudProvider below: deciding whether an
            // inherited model is foreign needs the provider it came from. Mirrors
            // macOS `applyEngineModel`.
            var priorProvider = mode.CloudProvider;
            var priorModel = mode.CloudTranscriptionModel;

            mode.ProviderType = "cloud";
            mode.Model = "cloud";
            mode.CloudProvider = cloudProvider.GetIdentifier();
            if (!string.IsNullOrEmpty(providerNormalization.AccuracyTier))
            {
                mode.CloudAccuracyTier = providerNormalization.AccuracyTier;
                if (!string.IsNullOrEmpty(model))
                {
                    mode.CloudTranscriptionModel = model;
                }
                // Otherwise leave the field alone. This used to overwrite it with
                // the tier default unconditionally, which silently dropped a
                // sub-model the caller had pinned inside the SAME tier — the
                // Azure MAI entry has two models at different rates, so that
                // changed what ran and what it cost. `ResolveDictationModelId`
                // (below, and on the send path) validates the surviving id
                // against the NEW tier and falls back to that tier's default
                // when it does not belong, so nothing stale can leak through.
                return;
            }
            if (!string.IsNullOrEmpty(model))
            {
                mode.CloudTranscriptionModel = model;
                return;
            }

            // NO EXPLICIT MODEL AND NO INFERRED TIER (issue #500). This arm
            // assigned nothing except for Meta, so `CloudTranscriptionModel`
            // stayed null and `/transcribe` answered `model: ""`.
            //
            // HyperWhisper Cloud needs nothing here: it dispatches on the
            // accuracy tier, and both the send path and `ModelLabel` run the id
            // through `ResolveDictationModelId`, which heals a blank, foreign,
            // live-only or out-of-tier value on its own. Writing a guess here
            // would only add a second opinion that can disagree with the run.
            if (cloudProvider == CloudTranscriptionProvider.HyperWhisperCloud)
            {
                return;
            }

            // A BYOK provider is different: it sends `CloudTranscriptionModel`
            // verbatim, so an id inherited from ANOTHER vendor changes the model
            // that actually runs, not just the label. Keep an inherited id only
            // when it belongs to THIS provider — the mode was already on it (the
            // caller is re-asserting the engine, so their saved sub-model
            // stands), or the id resolves within this provider's catalog.
            // `GetById` rather than a raw list scan, because it applies
            // `ResolveModelAlias`: a legacy-but-serviceable id such as
            // AssemblyAI `universal` is not in the curated list and would
            // otherwise be judged foreign and silently upgraded to a
            // different-priced model.
            var belongsToProvider =
                !string.IsNullOrEmpty(priorModel)
                && (string.Equals(priorProvider, mode.CloudProvider, StringComparison.OrdinalIgnoreCase)
                    || CloudTranscriptionModels.GetById(priorModel, cloudProvider) != null);
            if (belongsToProvider)
            {
                return;
            }

            // Otherwise the provider's own default is what will run. Meta used
            // to be special-cased here; it is not special, it was just the only
            // arm anybody had filled in.
            mode.CloudTranscriptionModel = CloudTranscriptionModels.GetDefault(cloudProvider)?.Id ?? "";
            return;
        }

        // ONE ALIAS TABLE, SHARED WITH macOS AND THE PORTABLE HEAD (issue #356
        // item 3). The local half of the documented `engine` field was a
        // hand-kept `switch` here, another on the portable head and TWO on
        // macOS; `resolve_engine_alias` is the union of all four. It normalises
        // trim-then-lowercase, so ` parakeet` now works here as it always did
        // on Linux.
        //
        // The resolver answers WHICH engine, never whether this build has it —
        // macOS has Nemotron and Apple Speech and Windows does not, so a shared
        // table that decided availability would be wrong on two platforms in
        // opposite directions. The capability verdict is made below and it is
        // ENGINE_UNAVAILABLE, which is already one of the closed fourteen.
        var resolved = HyperwhisperCoreMethods.LocalApiResolveEngineAlias(normalized);
        switch (resolved)
        {
            case HwLocalApiEngineId.WhisperLocal:
                if (string.IsNullOrWhiteSpace(model))
                {
                    // A request mistake, not an outage (issue #1493, Ray via
                    // inbox #295): INVALID_REQUEST with a hint to send
                    // `model`, never a default model. ENGINE_UNAVAILABLE said
                    // "try again later", which a client retries forever. Same
                    // bytes as macOS (`APIInputError.whisperModelRequired`) and
                    // the portable head's message.
                    throw WindowsLocalApiFailure.Business(
                        LocalApiErrorCode.InvalidRequest,
                        WhisperModelRequiredMessage,
                        WhisperModelRequiredHint);
                }

                mode.ProviderType = "local";
                mode.LocalEngine = "whisper";
                mode.ModelType = model;
                mode.Model = mode.ModelType;
                break;
            case HwLocalApiEngineId.Parakeet:
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = model ?? "parakeet-v3";
                mode.Model = mode.LocalParakeetModel;
                break;
            case HwLocalApiEngineId.Qwen3Asr:
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = model ?? "qwen3-asr-0.6b";
                mode.Model = mode.LocalParakeetModel;
                break;
            // Real engine ids this platform does not ship. They used to fall
            // into `Unknown engine`, which told a caller their spelling was
            // wrong when it was not.
            case HwLocalApiEngineId.Nemotron:
            case HwLocalApiEngineId.AppleSpeech:
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.EngineUnavailable,
                    $"Engine '{HyperwhisperCoreMethods.LocalApiEngineWireLabel(resolved.Value)}' is not available on Windows");
            default:
                throw WindowsLocalApiFailure.Business(
                    LocalApiErrorCode.EngineUnavailable,
                    $"Unknown engine '{engine}'");
        }
    }

    // =========================================================================
    // Wire-label projection
    // =========================================================================

    /// <summary>
    /// The <c>engine</c> spelling a response carries, read from the shared
    /// table (issue #356 item 3, review round 1).
    /// </summary>
    /// <remarks>
    /// CLIENT-VISIBLE RESPONSE CHANGE: Qwen3 was labelled <c>qwen3_asr</c> here
    /// and is now <c>qwen3Asr</c>. <c>openapi.yaml</c> publishes
    /// <c>qwen3Asr</c> as the ONLY spelling of that value, and macOS has always
    /// emitted it, so this head was answering with a string its own published
    /// contract does not list. <c>qwen3_asr</c> stays an accepted REQUEST alias
    /// on all three heads through <c>resolve_engine_alias</c>, so a client that
    /// echoes an old response back still works — the round trip is now closed
    /// from both sides rather than only the accept side.
    ///
    /// The cloud arm keeps this head's provider id: the shared table covers the
    /// five local ids only, and the cloud half stays in <c>hw-catalog</c>.
    /// </remarks>
    private static string EngineLabel(Mode mode)
    {
        if (LocalModeModel.IsCloud(mode))
        {
            return mode.CloudProvider ?? "cloud";
        }
        if (IsParakeetMode(mode))
        {
            return HyperwhisperCoreMethods.LocalApiEngineWireLabel(
                IsQwen3Model(mode) ? HwLocalApiEngineId.Qwen3Asr : HwLocalApiEngineId.Parakeet);
        }
        return HyperwhisperCoreMethods.LocalApiEngineWireLabel(HwLocalApiEngineId.WhisperLocal);
    }

    /// <summary>
    /// The <c>model</c> a response carries: the model that ACTUALLY RAN, not the
    /// one the request named (issue #500).
    /// </summary>
    /// <remarks>
    /// The cloud arm used to be a bare <c>mode.CloudTranscriptionModel ?? ""</c>,
    /// which reported an empty string whenever that field was unset — the
    /// ordinary state for a mode driven by an accuracy tier, and for a transient
    /// mode built from an <c>engine</c> with no <c>model</c>.
    ///
    /// Resolution happens HERE, at the projection, and not only where the
    /// transient Mode is built, because <c>ApplyEngineModel</c> runs only when
    /// the request carries an <c>engine</c>. A plain <c>mode_id</c> request never
    /// reaches it, so a fix planted there would leave the two request forms
    /// disagreeing about the same Mode — the opposite of the point.
    ///
    /// HyperWhisper Cloud goes through <see cref="HyperWhisperCloudService.ResolveDictationModelId"/>
    /// — the SAME function the send path uses to choose the `X-STT-Model` header,
    /// reached through the same tier normalisation. The label is therefore the
    /// dispatched model by construction: a blank, foreign, live-only, legacy-alias
    /// or out-of-tier id heals to the tier's default in both places at once, and
    /// the two cannot drift.
    ///
    /// <c>internal</c> for the same reason as <see cref="ApplyEngineModel"/>: the
    /// smoke suite asserts the wire label this produces.
    /// </remarks>
    internal static string ModelLabel(Mode mode)
    {
        if (LocalModeModel.IsCloud(mode))
        {
            var provider = CloudTranscriptionProviderExtensions.FromModeCloudProvider(mode.CloudProvider);
            if (provider == CloudTranscriptionProvider.HyperWhisperCloud)
            {
                return HyperWhisperCloudService.ResolveDictationModelId(
                    CloudAccuracyTierExtensions.FromString(mode.CloudAccuracyTier).ToStorageValue(),
                    mode.CloudTranscriptionModel);
            }
            // A BYOK provider sends this field verbatim, so it is already the
            // model that ran — except when it is unset, where the provider
            // applies its own default.
            return !string.IsNullOrEmpty(mode.CloudTranscriptionModel)
                ? mode.CloudTranscriptionModel
                : CloudTranscriptionModels.GetDefault(provider)?.Id ?? "";
        }
        if (IsParakeetMode(mode))
        {
            return mode.LocalParakeetModel ?? mode.Model ?? "";
        }
        return mode.Model ?? "";
    }

    private static string? EffectiveLanguage(Mode mode)
    {
        var raw = mode.Language?.ToLowerInvariant();
        if (string.IsNullOrEmpty(raw) || raw == "auto") return null;
        return raw;
    }

    private static bool IsParakeetMode(Mode mode) =>
        string.Equals(mode.LocalEngine, "parakeet", StringComparison.OrdinalIgnoreCase);

    private static bool IsQwen3Model(Mode mode)
    {
        var modelId = string.IsNullOrWhiteSpace(mode.LocalParakeetModel)
            ? mode.Model
            : mode.LocalParakeetModel;
        return ParakeetModelInfo.AllModels.Any(m =>
            m.Engine == ParakeetEngine.Qwen3 &&
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

}
