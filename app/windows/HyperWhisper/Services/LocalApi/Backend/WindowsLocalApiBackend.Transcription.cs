using System.IO;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
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
}
