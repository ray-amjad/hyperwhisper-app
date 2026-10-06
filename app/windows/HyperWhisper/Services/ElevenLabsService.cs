// ELEVENLABS SERVICE
// Cloud transcription via ElevenLabs' Scribe Speech-to-Text API.
//
// API ENDPOINT: POST https://api.elevenlabs.io/v1/speech-to-text
//
// REQUEST FORMAT: multipart/form-data
// - file: Audio file
// - model_id: Model ID (scribe_v2, scribe_v1)
// - language_code: ISO 639-1 language code (optional, auto-detect if not specified)
// - keyterms: Array of strings for vocabulary boosting (scribe_v2 only, up to 100 terms)
//
// RESPONSE FORMAT: { "text": "transcribed text", ... }
//
// AUTHENTICATION: xi-api-key header (NOT Bearer token)
//
// LIMITS:
// - Supported formats: mp3, mp4, m4a, wav, webm, ogg, flac
// - Keyterms: max 100 terms, each < 50 characters, max 5 words per term
//
// VOCABULARY SUPPORT:
// - scribe_v2: Supports keyterms for custom vocabulary boosting
// - scribe_v1: Does NOT support custom vocabulary
//
// scribe_v1 was retired by ElevenLabs 2026-07-09. Legacy "scribe_v1" IDs are resolved
// to scribe_v2 via CloudTranscriptionModels.ResolveElevenLabsModelAlias, at Configure()
// time below — the Rust shared core (hw-net's elevenlabs.rs) passes model_id straight
// to the wire with no alias resolution of its own, so this is the only place the
// redirect actually takes effect before the request is built.
//
// ERROR HANDLING:
// - 401: Invalid API key
// - 429: Rate limited
// - 400: Invalid request
//
// NOTE: Uses TranscriptionApiKeyType.ElevenLabs (separate from post-processing)

using System.Diagnostics;
using System.Net.Http;
using HyperWhisper.Models;
using HyperWhisper.Services.Transcription;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// Cloud transcription service using ElevenLabs' Scribe API.
/// Scribe V2 supports custom vocabulary via keyterms (up to 100 terms).
/// Scribe V1 does NOT support custom vocabulary.
/// </summary>
public class ElevenLabsService : ApiKeyTranscriptionServiceBase
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    private const int DefaultTimeoutSeconds = 120;

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Display name. It names no model: the model is per call (issue #753).
    /// </summary>
    public override string Name => "ElevenLabs";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public ElevenLabsService(HttpMessageHandler? httpHandler = null)
        : base(TimeSpan.FromSeconds(DefaultTimeoutSeconds), "scribe_v2", httpHandler: httpHandler)
    {
    }

    // =========================================================================
    // CONFIGURATION
    // =========================================================================

    /// <summary>
    /// Configures the service with an API key. The model is not configured
    /// here: it travels in each call's request (issue #753).
    /// </summary>
    public override void Configure(string apiKey)
    {
        ApiKey = apiKey;
    }

    /// <inheritdoc />
    protected override string ResolveModelId(string modelId)
        => CloudTranscriptionModels.ResolveElevenLabsModelAlias(modelId);

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes audio using ElevenLabs' Scribe API.
    /// Scribe V2: vocabulary is sent as keyterms (up to 100 terms, each &lt; 50 chars).
    /// Scribe V1: vocabulary is ignored (not supported).
    /// </summary>
    protected override async Task<string> TranscribeCoreAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var (audioPath, language, vocabulary) = (request.AudioPath, request.Language, request.Vocabulary);
        var totalSw = Stopwatch.StartNew();
        var isScribeV2 = request.ModelId == "scribe_v2";

        LoggingService.Info("========== ELEVENLABS CLOUD TRANSCRIPTION ==========");
        LoggingService.Info($"  Model: {request.ModelId}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");

        // Handle vocabulary based on model
        if (vocabulary?.Count > 0)
        {
            if (isScribeV2)
            {
                LoggingService.Info($"  Vocabulary terms: {vocabulary.Count} (will be sent as keyterms)");
            }
            else
            {
                LoggingService.Warn($"  Warning: Vocabulary ignored - Scribe V1 does not support keyterms");
            }
        }

        // STEP 1+2: Validate configuration and audio file (shared gate).
        // ElevenLabs does not cap the file size client-side.
        TranscriptionPreflight.Validate("ElevenLabs", ApiKey, audioPath);

        // STEP 3: Build the request via the Rust shared core, then drive it
        // through the shared executor + core retry loop. The core owns language
        // normalization, keyterms (scribe_v2), tag_audio_events, and the
        // multi-format ({text} / {transcripts} / {words}) response parsing.
        // TODO-verify (Windows/CI): Rust shared-core swap.
        var contentType = TranscriptionPreflight.MimeTypeFor(audioPath, "audio/wav");

        var coreParams = BuildDirectVendorParams(request, contentType);

        return await RustSingleShot.TranscribeAsync(
            Http,
            "ElevenLabs",
            buildRequest: () => HyperwhisperCoreMethods.ElevenlabsBuildTranscribeRequest(coreParams),
            parseResponse: HyperwhisperCoreMethods.ElevenlabsParseTranscribeResponse,
            totalSw: totalSw,
            cancellationToken: cancellationToken);
    }
}
