using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Win32.SafeHandles;

namespace HyperWhisper.Services.LocalApi.Endpoints;

/// <summary>
/// `POST /transcribe` — accept either a file path or a base64 blob, resolve
/// against a saved or transient Mode, dispatch through the orchestrator, and
/// return the (possibly post-processed) text. Wire shape mirrors macOS
/// `TranscribeEndpoint` so the same MCP wrapper / cURL snippet works against
/// either build. `/post-process` is the formatting endpoint, so this route skips
/// the GUI post-processing pipeline even when the resolved Mode enables it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TranscribeEndpoints
{
    private const uint FinalPathNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    public static void Map(IEndpointRouteBuilder app, LocalApiServer server)
    {
        app.MapPost("/transcribe", async (HttpContext ctx) =>
        {
            TranscribeRequest? req;
            try
            {
                req = await ctx.Request.ReadFromJsonAsync<TranscribeRequest>(LocalApiResponder.JsonOptions);
            }
            catch
            {
                return LocalApiResponder.BadRequest(
                    "Invalid JSON body",
                    "Required: file (absolute path) plus either mode_id, or engine + model.");
            }
            if (req == null)
            {
                return LocalApiResponder.BadRequest("Empty request body");
            }

            // Resolve audio source — `file` xor `audio_base64`. The base64 path
            // writes a temp file under %TEMP%; the `finally` block deletes it
            // on every exit (success or any error).
            string audioPath;
            bool tempFileCreated;
            FileStream? audioPathReadLock;
            try
            {
                (audioPath, tempFileCreated, audioPathReadLock) = ResolveAudioSource(req);
            }
            catch (ApiInputException aiex)
            {
                return LocalApiResponder.Failure(aiex.Code, aiex.Message, aiex.Hint);
            }

            try
            {
                var orchestrator = server.TranscriptionOrchestrator;
                if (orchestrator == null)
                {
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.EngineUnavailable,
                        "Transcription orchestrator not initialized");
                }

                // Resolve Mode — saved, saved+overrides, or engine-only transient.
                Mode effectiveMode;
                try
                {
                    effectiveMode = ResolveMode(req);
                }
                catch (ApiInputException aiex)
                {
                    return LocalApiResponder.Failure(aiex.Code, aiex.Message, aiex.Hint);
                }

                var vocabulary = effectiveMode.CustomVocabulary;
                // Dispatch to whichever local engine the resolved Mode names.
                // Without this, `engine=parakeet` requests silently fall through
                // to Whisper (or fail with a misleading error).
                var localProvider = IsParakeetMode(effectiveMode)
                    ? server.ParakeetTranscriptionProvider
                    : server.LocalTranscriptionProvider;
                var applicationContext = req.ApplicationContext?.ToApplicationContext();

                if (string.Equals(effectiveMode.ProviderType, "local", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await EnsureLocalModelLoadedAsync(server, effectiveMode, localProvider, ctx.RequestAborted);
                    }
                    catch (ApiInputException aiex)
                    {
                        return LocalApiResponder.Failure(aiex.Code, aiex.Message, aiex.Hint);
                    }
                    catch (TranscriptionException tex)
                    {
                        var (code, message, hint) = LocalApiResponder.MapTranscriptionException(tex);
                        return LocalApiResponder.Failure(code, message, hint);
                    }
                    catch (OperationCanceledException)
                    {
                        return LocalApiResponder.Failure(
                            LocalApiErrorCode.Timeout,
                            "Transcription was cancelled while loading the local model");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error("LocalAPI /transcribe: local model load failed", ex);
                        return LocalApiResponder.Failure(
                            LocalApiErrorCode.EngineUnavailable,
                            ex.Message);
                    }
                }

                var started = DateTime.UtcNow;
                TranscriptionResult result;
                try
                {
                    result = await orchestrator.TranscribeAsync(
                        audioPath: audioPath,
                        mode: effectiveMode,
                        vocabulary: vocabulary,
                        localTranscriptionProvider: localProvider,
                        applicationContext: applicationContext,
                        cancellationToken: ctx.RequestAborted,
                        callSite: TranscriptionCallSite.Api,
                        applyPostProcessing: false);
                }
                catch (TranscriptionException tex)
                {
                    var (code, message, hint) = LocalApiResponder.MapTranscriptionException(tex);
                    return LocalApiResponder.Failure(code, message, hint);
                }
                catch (OperationCanceledException)
                {
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.Timeout,
                        "Transcription was cancelled");
                }
                catch (Exception ex)
                {
                    LoggingService.Error("LocalAPI /transcribe: orchestrator threw", ex);
                    return LocalApiResponder.Failure(
                        LocalApiErrorCode.TranscriptionFailed,
                        ex.Message);
                }

                var latencyMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds);

                var response = new TranscribeResponse
                {
                    Text = result.RawText,
                    Engine = EngineLabel(effectiveMode),
                    Model = ModelLabel(effectiveMode),
                    Language = EffectiveLanguage(effectiveMode),
                    Timings = new TranscribeTimings { LoadMs = 0, DecodeMs = latencyMs },
                    LatencyMs = latencyMs
                };
                return LocalApiResponder.Ok(response);
            }
            finally
            {
                audioPathReadLock?.Dispose();

                if (tempFileCreated)
                {
                    try { File.Delete(audioPath); }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"LocalAPI /transcribe: failed to delete temp file {audioPath}: {ex.Message}");
                    }
                }
            }
        });
    }

    private static async Task EnsureLocalModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider? provider,
        CancellationToken cancellationToken)
    {
        if (provider == null)
        {
            throw new ApiInputException(
                LocalApiErrorCode.EngineUnavailable,
                "Local transcription provider not initialized");
        }

        if (IsParakeetMode(mode))
        {
            await EnsureParakeetModelLoadedAsync(server, mode, provider, cancellationToken);
            return;
        }

        await EnsureWhisperModelLoadedAsync(server, mode, provider, cancellationToken);
    }

    private static async Task EnsureWhisperModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is not TranscriptionService transcriptionService)
        {
            throw new ApiInputException(
                LocalApiErrorCode.EngineUnavailable,
                "Whisper local provider is not available");
        }

        var modelType = string.IsNullOrWhiteSpace(mode.ModelType) ? mode.Model : mode.ModelType;
        var modelInfo = WhisperModelInfo.AllModels.FirstOrDefault(m => m.Type == modelType);
        if (modelInfo == null)
        {
            throw new ApiInputException(
                LocalApiErrorCode.ModelNotFound,
                $"Whisper model '{modelType}' is not recognized");
        }

        var modelService = server.WhisperModels;
        if (modelService == null)
        {
            throw new ApiInputException(
                LocalApiErrorCode.EngineUnavailable,
                "Whisper model service not initialized");
        }

        if (!modelService.IsModelDownloaded(modelInfo))
        {
            throw new ApiInputException(
                LocalApiErrorCode.ModelNotInstalled,
                $"Whisper model '{modelInfo.DisplayName}' is not installed",
                "Open HyperWhisper and download the model you want to use before calling /transcribe.");
        }

        var modelPath = modelService.GetModelPath(modelInfo);
        if (transcriptionService.IsInitialized &&
            string.Equals(transcriptionService.LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoggingService.Info($"LocalAPI /transcribe: Loading Whisper model {modelInfo.DisplayName}");
        await transcriptionService.InitializeAsync(modelPath, _ => { }, cancellationToken);
    }

    private static async Task EnsureParakeetModelLoadedAsync(
        LocalApiServer server,
        Mode mode,
        ITranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        if (provider is not ParakeetTranscriptionService parakeetService)
        {
            throw new ApiInputException(
                LocalApiErrorCode.EngineUnavailable,
                "Parakeet local provider is not available");
        }

        var modelId = string.IsNullOrWhiteSpace(mode.LocalParakeetModel)
            ? mode.Model
            : mode.LocalParakeetModel;
        var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
        if (modelInfo == null)
        {
            throw new ApiInputException(
                LocalApiErrorCode.ModelNotFound,
                $"Parakeet-family model '{modelId}' is not recognized");
        }

        var modelService = server.ParakeetModels;
        if (modelService == null)
        {
            throw new ApiInputException(
                LocalApiErrorCode.EngineUnavailable,
                "Parakeet model service not initialized");
        }

        if (!modelService.IsModelDownloaded(modelInfo))
        {
            throw new ApiInputException(
                LocalApiErrorCode.ModelNotInstalled,
                $"Parakeet-family model '{modelInfo.DisplayName}' is not installed",
                "Open HyperWhisper and download the model you want to use before calling /transcribe.");
        }

        var language = mode.Language == "auto" ? null : mode.Language;
        var effectiveLanguage = language ?? "auto";

        if (parakeetService.IsInitialized &&
            string.Equals(parakeetService.LoadedModelId, modelInfo.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parakeetService.LoadedLanguage, effectiveLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LoggingService.Info($"LocalAPI /transcribe: Loading Parakeet-family model {modelInfo.DisplayName}");
        await parakeetService.InitializeAsync(modelService.GetModelDirectory(modelInfo), language);
    }

    // =========================================================================
    // Audio source resolution
    // =========================================================================

    /// <summary>
    /// Resolve the `file` xor `audio_base64` request fields into a concrete
    /// path on disk. Returns the path and a flag the caller uses in its
    /// `finally` to clean up only temp files this method created.
    /// </summary>
    private static (string path, bool isTempFile, FileStream? readLock) ResolveAudioSource(TranscribeRequest req)
    {
        var trimmedFile = req.File?.Trim();
        var trimmedBase64 = req.AudioBase64?.Trim();

        var hasFile = !string.IsNullOrEmpty(trimmedFile);
        var hasBase64 = !string.IsNullOrEmpty(trimmedBase64);

        if (hasFile && hasBase64)
        {
            throw new ApiInputException(
                LocalApiErrorCode.InvalidRequest,
                "Pass either 'file' or 'audio_base64', not both");
        }
        if (!hasFile && !hasBase64)
        {
            throw new ApiInputException(
                LocalApiErrorCode.InvalidRequest,
                "Provide 'file' (absolute path) or 'audio_base64' + 'mime_type'");
        }

        if (hasFile)
        {
            // Canonicalize first, then contain the resolved path to the directories
            // HyperWhisper itself owns (recordings / legacy / temp roots). Without
            // this, a same-user process holding the loopback bearer token could name
            // any app-readable absolute path and have it opened — and, with a cloud
            // engine, shipped off-box (confused-deputy file read / exfiltration, #740).
            // Reuse the same trusted-root containment that guards audio deletion.
            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(trimmedFile!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.InvalidRequest,
                    "The 'file' field is not a valid path.");
            }

            // Reject anything outside the allow-listed roots with a single, uniform
            // error that does NOT reveal whether the path exists or is readable —
            // this closes the FileNotFound-vs-FileAccessDenied existence/permission
            // oracle. The check runs before any File.Exists/OpenRead probe.
            if (!HistoryService.IsTrustedAudioPath(canonicalPath))
            {
                throw new ApiInputException(
                    LocalApiErrorCode.FileNotAllowed,
                    "The 'file' path is outside HyperWhisper's recording folders.",
                    "Use 'audio_base64' to transcribe audio from arbitrary locations, "
                        + "or pass a file inside the configured recordings folder.");
            }

            if (!File.Exists(canonicalPath))
            {
                throw new ApiInputException(
                    LocalApiErrorCode.FileNotFound,
                    "Audio file not found in the recordings folder.",
                    "Pass a file the running app recorded, or use 'audio_base64'.");
            }

            // The lexical containment check above only sees the spelled path.
            // Because the recordings roots are user-writable, a token holder can
            // plant a junction/symlink inside a trusted root (e.g.
            // <recordings>\link\secret.wav, where `link` reparses outside the
            // root) — the prefix check accepts it, while File.OpenRead and the
            // orchestrator follow the reparse point and read the real target
            // (arbitrary file read / cloud exfiltration, #740). Resolve reparse
            // points to the real on-disk target.
            string resolvedPath;
            try
            {
                resolvedPath = ResolveRealPath(canonicalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Treat an unresolvable target the same as outside-the-root: a
                // uniform refusal that does not leak existence/permission detail.
                throw new ApiInputException(
                    LocalApiErrorCode.FileNotAllowed,
                    "The 'file' path is outside HyperWhisper's recording folders.",
                    "Use 'audio_base64' to transcribe audio from arbitrary locations, "
                        + "or pass a file inside the configured recordings folder.");
            }

            // Only when a reparse point was actually followed (resolved != lexical)
            // do we re-assert containment on the real target. This blocks the
            // junction/symlink escape without rejecting a legitimately reparsed
            // recordings root (e.g. a user whose Documents folder is itself a
            // junction), where the lexical gate above already proved containment.
            if (!string.Equals(resolvedPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
                && !HistoryService.IsTrustedAudioPath(resolvedPath, ResolveRealPath))
            {
                throw new ApiInputException(
                    LocalApiErrorCode.FileNotAllowed,
                    "The 'file' path is outside HyperWhisper's recording folders.",
                    "Use 'audio_base64' to transcribe audio from arbitrary locations, "
                        + "or pass a file inside the configured recordings folder.");
            }

            // Snapshot the approved handle into a private temp file and pass that
            // stable path to the providers. The orchestrator/providers accept only
            // a path today, so returning the caller-controlled recordings path would
            // leave a time-of-check/time-of-use gap where the token holder could
            // replace the validated file or an ancestor before the provider reopens
            // it. Keeping a read lock on the snapshot prevents replacement until
            // transcription finishes, while still allowing providers to open it.
            FileStream sourceStream;
            try
            {
                sourceStream = new FileStream(
                    resolvedPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        Options = FileOptions.SequentialScan
                    });
            }
            catch (UnauthorizedAccessException)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.FileAccessDenied,
                    "Cannot read the requested recording.");
            }
            catch (IOException)
            {
                throw new ApiInputException(
                    LocalApiErrorCode.FileAccessDenied,
                    "Cannot read the requested recording.");
            }

            using (sourceStream)
            {
                string openedPath;
                try
                {
                    openedPath = GetFinalDosPath(sourceStream.SafeFileHandle);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new ApiInputException(
                        LocalApiErrorCode.FileNotAllowed,
                        "The 'file' path is outside HyperWhisper's recording folders.",
                        "Use 'audio_base64' to transcribe audio from arbitrary locations, "
                            + "or pass a file inside the configured recordings folder.");
                }

                if (!HistoryService.IsTrustedAudioPath(openedPath, ResolveRealPath))
                {
                    throw new ApiInputException(
                        LocalApiErrorCode.FileNotAllowed,
                        "The 'file' path is outside HyperWhisper's recording folders.",
                        "Use 'audio_base64' to transcribe audio from arbitrary locations, "
                            + "or pass a file inside the configured recordings folder.");
                }

                var snapshotPath = CreateLocalApiSnapshotPath(openedPath);
                try
                {
                    using (var snapshot = new FileStream(
                        snapshotPath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            Options = FileOptions.SequentialScan
                        }))
                    {
                        sourceStream.CopyTo(snapshot);
                    }

                    var readLock = new FileStream(
                        snapshotPath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                            Options = FileOptions.SequentialScan
                        });
                    return (snapshotPath, true, readLock);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    try { File.Delete(snapshotPath); }
                    catch (Exception cleanupEx)
                    {
                        LoggingService.Debug($"LocalAPI /transcribe: failed to delete temp snapshot {snapshotPath}: {cleanupEx.Message}");
                    }

                    throw new ApiInputException(
                        LocalApiErrorCode.FileAccessDenied,
                        "Cannot read the requested recording.");
                }
            }
        }

        // base64 path
        byte[] data;
        try
        {
            data = Convert.FromBase64String(trimmedBase64!);
        }
        catch (FormatException)
        {
            throw new ApiInputException(
                LocalApiErrorCode.AudioDecodeFailed,
                "'audio_base64' is not valid base64");
        }

        var ext = ExtensionForMime(req.MimeType);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"hyperwhisper-localapi-{Guid.NewGuid():N}.{ext}");
        try
        {
            File.WriteAllBytes(tempPath, data);
        }
        catch (Exception ex)
        {
            throw new ApiInputException(
                LocalApiErrorCode.AudioDecodeFailed,
                $"Failed to write decoded audio to temp file: {ex.Message}");
        }
        return (tempPath, true, null);
    }

    /// <summary>
    /// Resolve <paramref name="canonicalPath"/> (already an absolute, lexically
    /// normalized path) to the real on-disk target, following every reparse point
    /// (NTFS symlink / junction) on the leaf AND on each parent directory
    /// component. <see cref="Path.GetFullPath"/> only normalizes the spelling and
    /// does NOT follow reparse points, so a junction planted inside a trusted,
    /// user-writable recordings root would otherwise let the lexical containment
    /// check pass while the file actually opened lives elsewhere (#740). The
    /// returned path is fully normalized and safe to re-check against the trusted
    /// roots.
    /// </summary>
    private static string ResolveRealPath(string canonicalPath)
    {
        // Resolve the deepest existing component (file or directory) to its final
        // target. returnFinalTarget walks the ENTIRE reparse-point chain along the
        // path — every junction/symlink in any ancestor directory is followed — so
        // both <recordings>\leaflink.wav and <recordings>\dirjunction\f.wav land on
        // their real location. Returns null when the component is not itself a
        // reparse point, in which case there is nothing to follow at this level.
        var leafTarget = File.Exists(canonicalPath)
            ? File.ResolveLinkTarget(canonicalPath, returnFinalTarget: true)
            : Directory.ResolveLinkTarget(canonicalPath, returnFinalTarget: true);
        if (leafTarget != null)
        {
            return Path.GetFullPath(leafTarget.FullName);
        }

        // The leaf itself is not a reparse point, but an ancestor directory may be
        // (e.g. <recordings>\junction\secret.wav). Resolve the parent chain and
        // recombine with the leaf file name.
        var parent = Path.GetDirectoryName(canonicalPath);
        if (string.IsNullOrEmpty(parent))
        {
            return canonicalPath;
        }

        var realParent = ResolveRealPath(parent);
        return Path.GetFullPath(Path.Combine(realParent, Path.GetFileName(canonicalPath)));
    }

    private static string CreateLocalApiSnapshotPath(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".wav";
        }

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "HyperWhisper", "local-api-transcribe");
        Directory.CreateDirectory(snapshotDirectory);
        return Path.Combine(snapshotDirectory, $"snapshot-{Guid.NewGuid():N}{extension}");
    }

    private static string GetFinalDosPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(
            handle,
            buffer,
            (uint)buffer.Capacity,
            FinalPathNameNormalized | VolumeNameDos);

        if (length == 0)
        {
            throw new IOException($"GetFinalPathNameByHandle failed with Win32 error {Marshal.GetLastWin32Error()}");
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                FinalPathNameNormalized | VolumeNameDos);

            if (length == 0 || length >= buffer.Capacity)
            {
                throw new IOException($"GetFinalPathNameByHandle failed with Win32 error {Marshal.GetLastWin32Error()}");
            }
        }

        return Path.GetFullPath(StripExtendedPathPrefix(buffer.ToString()));
    }

    private static string StripExtendedPathPrefix(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
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
    private static Mode ResolveMode(TranscribeRequest req)
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
                throw new ApiInputException(
                    LocalApiErrorCode.InvalidRequest,
                    $"'{modeId}' is not a valid mode id");
            }
            var stored = ModeService.Instance.GetMode(guid);
            if (stored == null)
            {
                throw new ApiInputException(
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
            throw new ApiInputException(
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
    private static Mode BuildTransientMode(Mode? baseline, string? engine, string? model, string? language)
    {
        Mode mode;
        if (baseline != null)
        {
            mode = new Mode
            {
                Id = Guid.NewGuid(),
                Name = "__local_api_transient__",
                Preset = baseline.Preset,
                Language = baseline.Language,
                Model = baseline.Model,
                ModelType = baseline.ModelType,
                Punctuation = baseline.Punctuation,
                Capitalization = baseline.Capitalization,
                ProfanityFilter = baseline.ProfanityFilter,
                CustomInstructions = baseline.CustomInstructions,
                UserSystemPrompt = baseline.UserSystemPrompt,
                LanguageModel = baseline.LanguageModel,
                CloudProvider = baseline.CloudProvider,
                CloudTranscriptionModel = baseline.CloudTranscriptionModel,
                CloudTranscriptionDomain = baseline.CloudTranscriptionDomain,
                ProviderType = baseline.ProviderType,
                PostProcessingMode = baseline.PostProcessingMode,
                PostProcessingProvider = baseline.PostProcessingProvider,
                EnglishSpelling = baseline.EnglishSpelling,
                CloudAccuracyTier = baseline.CloudAccuracyTier,
                RemoveTrailingPeriod = baseline.RemoveTrailingPeriod,
                EnableScreenOCR = baseline.EnableScreenOCR,
                GeminiCustomPrompt = baseline.GeminiCustomPrompt,
                CloudPostProcessingModel = baseline.CloudPostProcessingModel,
                LocalEngine = baseline.LocalEngine,
                LocalParakeetModel = baseline.LocalParakeetModel,
                LocalPostProcessingModel = baseline.LocalPostProcessingModel,
                CustomVocabulary = baseline.CustomVocabulary,
                SortOrder = int.MaxValue,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow
            };
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
                // (ModeDefaults.cs): ElevenLabs Scribe v2 + Anthropic Claude Haiku 4.5.
                CloudAccuracyTier = "elevenLabsScribeV2",
                CloudPostProcessingModel = "anthropic:claude-haiku-4-5",
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
            if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
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
    /// Encode an engine + (optional) model pair onto a Mode's provider/engine
    /// fields. Recognized engine strings are: any cloud-provider identifier
    /// ("openai", "groq", "deepgram", "hyperwhisper", "gemini", …) plus "cloud"
    /// (treated as HyperWhisperCloud), "whisper" / "whisperlocal", and
    /// "parakeet", and "qwen3_asr". Unknown strings are rejected so callers do not silently run
    /// a different engine than they requested.
    /// </summary>
    private static void ApplyEngineModel(Mode mode, string engine, string? model)
    {
        var normalized = engine.ToLowerInvariant();
        CloudTranscriptionProvider cloudProvider;
        if (normalized == "cloud")
        {
            cloudProvider = CloudTranscriptionProvider.HyperWhisperCloud;
        }
        else
        {
            cloudProvider = CloudTranscriptionProviderExtensions.FromIdentifier(normalized);
        }

        if (cloudProvider != CloudTranscriptionProvider.None)
        {
            mode.ProviderType = "cloud";
            mode.Model = "cloud";
            mode.CloudProvider = cloudProvider.GetIdentifier();
            if (!string.IsNullOrEmpty(model))
            {
                mode.CloudTranscriptionModel = model;
            }
            else if (string.IsNullOrEmpty(mode.CloudTranscriptionModel))
            {
                mode.CloudTranscriptionModel = CloudTranscriptionModels.GetDefault(cloudProvider)?.Id ?? "";
            }
            return;
        }

        switch (normalized)
        {
            case "whisperlocal":
            case "whisper":
            case "libwhisper":
                if (string.IsNullOrWhiteSpace(model))
                {
                    throw new ApiInputException(
                        LocalApiErrorCode.EngineUnavailable,
                        "Missing 'model' for whisperLocal engine");
                }

                mode.ProviderType = "local";
                mode.LocalEngine = "whisper";
                mode.ModelType = model;
                mode.Model = mode.ModelType;
                break;
            case "parakeet":
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = model ?? "parakeet-v3";
                mode.Model = mode.LocalParakeetModel;
                break;
            case "qwen3":
            case "qwen3asr":
            case "qwen3_asr":
            case "qwen3-asr":
            case "qwen":
                mode.ProviderType = "local";
                mode.LocalEngine = "parakeet";
                mode.LocalParakeetModel = model ?? "qwen3-asr-0.6b";
                mode.Model = mode.LocalParakeetModel;
                break;
            default:
                throw new ApiInputException(
                    LocalApiErrorCode.EngineUnavailable,
                    $"Unknown engine '{engine}'");
        }
    }

    // =========================================================================
    // Wire-label projection
    // =========================================================================

    private static string EngineLabel(Mode mode)
    {
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return mode.CloudProvider ?? "cloud";
        }
        if (IsParakeetMode(mode))
        {
            return IsQwen3Model(mode) ? "qwen3_asr" : "parakeet";
        }
        return "whisperLocal";
    }

    private static string ModelLabel(Mode mode)
    {
        if (string.Equals(mode.ProviderType, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return mode.CloudTranscriptionModel ?? "";
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

    // =========================================================================
    // Helpers
    // =========================================================================

    private sealed class ApiInputException : Exception
    {
        public string Code { get; }
        public string? Hint { get; }
        public ApiInputException(string code, string message, string? hint = null) : base(message)
        {
            Code = code;
            Hint = hint;
        }
    }
}
