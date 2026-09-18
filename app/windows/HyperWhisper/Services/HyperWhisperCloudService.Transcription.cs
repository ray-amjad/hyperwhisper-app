// HYPERWHISPER CLOUD SERVICE — TRANSCRIPTION
// The POST /transcribe send path: model resolution, the catalog vocabulary
// gate, the Rust shared-core request/retry sequence, and the diagnostics the
// orchestrator reads back.

using System.Diagnostics;
using System.IO;
using System.Threading;
using HyperWhisper.Configuration;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
// Rust shared-core binding. HwTranscript / HwTranscriptionException / HttpResponse
// collide with System types; qualify uniffi.hyperwhisper_core.HttpResponse below.
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

public partial class HyperWhisperCloudService
{
    /// <summary>
    /// Transcribes audio using HyperWhisper Cloud.
    /// Implements ITranscriptionProvider interface with default accuracy tier (Deepgram Nova-3).
    /// </summary>
    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // Use default accuracy tier (Deepgram Nova-3)
        return TranscribeAsync(audioPath, language, vocabulary, cloudAccuracyTier: null,
            cloudTranscriptionModel: null, cloudTranscriptionDomain: null, cancellationToken);
    }

    /// <summary>
    /// Transcribes audio using HyperWhisper Cloud with accuracy tier selection.
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="language">Language code or "auto" for auto-detect.</param>
    /// <param name="vocabulary">Custom vocabulary terms for better accuracy.</param>
    /// <param name="cloudAccuracyTier">Accuracy route (X-STT-Provider): catalog tier id e.g. "deepgramNova3" (default), "groqWhisper", "elevenLabsScribeV2", "grokStt". Legacy tier labels are also accepted.</param>
    /// <param name="cloudTranscriptionModel">Per-tier model id (X-STT-Model). Empty/null → backend uses the provider default.</param>
    /// <param name="cloudTranscriptionDomain">Domain (X-STT-Domain), e.g. "medical". Null → no domain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <summary>
    /// The X-STT-Model value for a pre-recorded dictation request.
    ///
    /// An empty/null stored model — OR a stale value that does not belong to this
    /// tier (the field is shared with the BYOK path, so a mode can carry e.g.
    /// "whisper-1") — means "use the catalog default for this tier". Validating
    /// the id keeps the header consistent with the picker and avoids a backend
    /// 400 on a mismatched model. Falls back to empty (backend default) when the
    /// catalog has no models for the tier.
    ///
    /// A live-only id (gemini-3.5-transcribe-live) IS a member of its tier, so
    /// plain membership accepts it and the backend answers every dictation with a
    /// 400 ("WebSocket-only model, not served by /transcribe"). The Mode editor's
    /// picker no longer offers one, but a backup restore, a Local API write or a
    /// mode saved before that filter existed can all still put one here, so the
    /// send path rejects it too and falls back to the tier default. Mirrors
    /// macOS's `dictationModels` check in HyperWhisperCloudProvider.swift.
    ///
    /// Extracted from TranscribeAsync so a test can exercise the REAL send path.
    /// It was previously inline, and the only coverage asserted on
    /// CloudSttCatalog.DictationModelsForId — a helper with no production caller,
    /// so the assertion held whether or not the send path guarded anything.
    /// </summary>
    internal static string ResolveDictationModelId(string tierStorageId, string? cloudTranscriptionModel)
    {
        if (tierStorageId == "assemblyAI" && cloudTranscriptionModel == "dictation-medical")
            throw new TranscriptionException(TranscriptionErrorCode.InvalidRequest, "AssemblyAI Dictation does not support Medical Mode.", "AssemblyAI");
        var catalog = Services.AppClassification.CloudSttCatalog.Shared;
        var modelBelongsToTier = !string.IsNullOrEmpty(cloudTranscriptionModel)
            && !Services.AppClassification.CloudSttCatalog.IsLiveOnlyModel(cloudTranscriptionModel)
            && catalog.GetModel(tierStorageId, cloudTranscriptionModel) != null;
        return modelBelongsToTier
            ? cloudTranscriptionModel!
            : (catalog.DefaultModelIdForId(tierStorageId) ?? "");
    }

    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language,
        IReadOnlyList<string>? vocabulary,
        string? cloudAccuracyTier,
        string? cloudTranscriptionModel,
        string? cloudTranscriptionDomain,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        LastDiagnostics = null;

        // Parse accuracy tier (defaults to Deepgram Nova-3)
        // (model resolution lives in ResolveDictationModelId, below)
        var accuracyTier = CloudAccuracyTierExtensions.FromString(cloudAccuracyTier);

        var tierStorageId = accuracyTier.ToStorageValue();
        var resolvedModel = ResolveDictationModelId(tierStorageId, cloudTranscriptionModel);

        var domain = string.IsNullOrEmpty(cloudTranscriptionDomain) ? null : cloudTranscriptionDomain;

        // Get fresh credentials at request time (matches macOS behavior)
        var (identifier, isLicensed) = LicenseManager.Instance.GetTranscriptionIdentifier();

        // Fail fast: the guest/device-credit path is dead server-side
        // (entitlement is enforced there), so an unlicensed request is doomed —
        // surface guidance instead of burning a network round-trip on a 401.
        if (!isLicensed)
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.CloudAccountRequired,
                "HyperWhisper Cloud requires an account key",
                "HyperWhisper Cloud");
        }

        LoggingService.Info("========== HYPERWHISPER CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Auth: {(isLicensed ? "License Key" : "Device Credits")}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Accuracy tier: {accuracyTier} ({accuracyTier.ToSttProvider()})");
        LoggingService.Info($"  Model: {(string.IsNullOrEmpty(resolvedModel) ? "(provider default)" : resolvedModel)}");
        LoggingService.Info($"  Domain: {domain ?? "(none)"}");
        LoggingService.Info($"  Vocabulary terms: {vocabulary?.Count ?? 0}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // STEP 1: Validate audio file
        if (!File.Exists(audioPath))
        {
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {LoggingService.DescribePath(audioPath)}",
                "HyperWhisper Cloud");
        }

        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");

        // Gate `initial_prompt` on the catalog's customVocabulary support flag —
        // the CORE DOES NOT DO THIS (it only builds the CSV: trim + drop-empty),
        // so the native gate restored from 1.7.0 is the only thing preventing
        // vocabulary from being sent to tiers/models that reject or ignore it.
        // Prefer the per-model flag (a tier can mix vocab-capable and not, e.g.
        // ElevenLabs scribe_v2 supports keyterms but scribe_v1 doesn't); fall
        // back to the tier-level flag when the model is unknown/default.
        var modelKnownForVocab = !string.IsNullOrEmpty(resolvedModel)
            && Services.AppClassification.CloudSttCatalog.Shared.GetModel(tierStorageId, resolvedModel) != null;
        var vocabSupported = modelKnownForVocab
            ? Services.AppClassification.CloudSttCatalog.Shared.ModelSupportsCustomVocabulary(tierStorageId, resolvedModel)
            : Services.AppClassification.CloudSttCatalog.Shared.SupportsCustomVocabulary(tierStorageId);

        var effectiveVocabulary = vocabulary;
        if (vocabulary != null && vocabulary.Count > 0 && !vocabSupported)
        {
            LoggingService.Info($"HyperWhisper Cloud dropping initial_prompt · tier={tierStorageId} model={(string.IsNullOrEmpty(resolvedModel) ? "(default)" : resolvedModel)} reason=catalog_unsupported");
            effectiveVocabulary = null;
        }

        // STEP 2: Build the request via the Rust shared core and drive it through
        // the shared executor + core retry loop. The core builds the URL + query
        // (license_key/device_id, language, initial_prompt), the X-STT-* routed
        // headers (from routedProvider/Model/Domain), the Content-Type, and the
        // @raw raw-stream body. We pass the catalog-gated vocab list — the core
        // builds the CSV (trim + drop-empty, no lowercase/dedup) but does NOT
        // gate on customVocabulary support (see above).
        // KEEP native: credit-header extraction, no-speech diagnostics, the DNS
        // HttpClient rebuild (via onTransportError), and the /post-process path.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = RustCoreMapping.TranscribeParams(
            audioPath: audioPath,
            audioMime: contentType,
            language: language,
            vocabulary: effectiveVocabulary ?? Array.Empty<string>(),
            // Core appends `/transcribe` itself — pass the BASE, not the endpoint.
            baseUrl: NetworkConfig.HyperWhisperCloudBaseUrl,
            licenseKey: identifier,
            // Guest/device-credit auth is dead server-side and unreachable past
            // the pre-check above. The core's deviceId param stays (macOS still
            // populates it); this call site just never uses it.
            deviceId: null,
            routedProvider: accuracyTier.ToSttProvider(),
            routedModel: string.IsNullOrEmpty(resolvedModel) ? null : resolvedModel,
            routedDomain: domain,
            // Opt-out only: the core adds X-Latency-Opt-Out: 1 when this is
            // false and nothing at all when it is true. The setting reads TRUE
            // for "share" (default true), so it passes straight through — no
            // inversion here. Read once per transcription rather than per retry
            // attempt: rebuilding coreParams per attempt would cost more than
            // the freshness is worth.
            shareAnonymousSpeedData: SettingsService.Instance.ShareAnonymousSpeedData);

        var rebuiltThisSequence = false;

        uniffi.hyperwhisper_core.HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                // Resolved per attempt so the post-rebuild attempts actually run
                // on the fresh pool (a plain HttpClient argument would pin the
                // stale pre-rebuild client for the whole sequence).
                () => Volatile.Read(ref _httpClient),
                buildRequest: () => ClientInfoHeaders.Apply(
                    HyperwhisperCoreMethods.HyperwhisperCloudBuildTranscribeRequest(coreParams)),
                // Not on RustSingleShot, and not only because of this mapper.
                // This sequence resolves its client per attempt (above), passes
                // an onTransportError hook (below), reads credit and diagnostic
                // headers off the response between the retry call and the parse,
                // maps its parse error with those diagnostics attached, and ends
                // in a five-line banner. RustSingleShot's own header lists which
                // of those it fixes.
                // MapCloudError below adds the 402 credit / 413 size context.
                parseError: MapCloudError,
                cancellationToken: cancellationToken,
                onTransportError: ex =>
                {
                    // One-shot HttpClient rebuild per retry sequence: a DNS-shaped
                    // error (network flip → stale cache) swaps in a fresh client,
                    // which the clientProvider above hands to the NEXT attempt so
                    // it re-resolves DNS. Gated to one rebuild per sequence.
                    if (!rebuiltThisSequence && IsDnsError(ex))
                    {
                        RebuildHttpClient();
                        rebuiltThisSequence = true;
                    }
                    return Task.CompletedTask;
                });
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, "HyperWhisper Cloud");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Credit balances + routed diagnostics come from the captured response
        // headers; the core's Transcript doesn't carry them. (Read once here on
        // the final response — error responses are handled by the retry wrapper.)
        ExtractCreditHeaders(response);
        var requestId = HeaderValue(response, "X-Request-ID");
        var sttProvider = HeaderValue(response, "X-STT-Provider");

        HwTranscript transcript;
        try
        {
            transcript = HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            // 200-but-no-speech surfaces here as a NoSpeech error.
            var diagnostics = new TranscriptionProviderDiagnostics(
                Name, requestId, sttProvider,
                BackendNoSpeechDetected: ex is HwTranscriptionException.NoSpeech,
                (int)response.@status, totalSw.ElapsedMilliseconds, false,
                // Stamped here, not in TranscriptionOrchestrator: this throw unwinds
                // past the orchestrator's enrichment, so a record that did not carry
                // its own source would reach Sentry tagged "unknown" - and this is
                // the exact path HYPERWHISPER-PA arrives on.
                AttemptSource: TranscriptionAttemptSource.CloudInstrumented,
                AttemptElapsedMs: totalSw.Elapsed.TotalMilliseconds);
            LastDiagnostics = diagnostics;
            // Attach the diagnostics we just captured (real HTTP status + latency)
            // to the thrown exception itself, not just the LastDiagnostics property -
            // callers that catch this exception directly (e.g. TranscriptionOrchestrator
            // step 1, which never reaches its own diagnostics read at the bottom of
            // TranscribeCloudAsync because this throw unwinds past it) would otherwise
            // see ProviderDiagnostics as null and every Sentry no-speech event would
            // report backend_http_status=0 / backend_response_latency_ms=0 regardless
            // of what actually happened on the wire.
            throw RustCoreMapping.MapTranscriptionError(
                ex, "HyperWhisper Cloud", (int)response.@status, providerDiagnostics: diagnostics);
        }

        LastDiagnostics = new TranscriptionProviderDiagnostics(
            Name, requestId, sttProvider, false, (int)response.@status,
            totalSw.ElapsedMilliseconds,
            string.IsNullOrWhiteSpace(transcript.@text),
            AttemptSource: TranscriptionAttemptSource.CloudInstrumented,
            AttemptElapsedMs: totalSw.Elapsed.TotalMilliseconds,
            RawResultLength: transcript.@text.Length);

        LoggingService.Info("========== HYPERWHISPER CLOUD COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcript.@text.Length}");
        LoggingService.Info($"  Credits used: {_lastCreditsUsed}");
        LoggingService.Info($"  Credits remaining: {_remainingCredits}");
        LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
        return transcript.@text;
    }
}
