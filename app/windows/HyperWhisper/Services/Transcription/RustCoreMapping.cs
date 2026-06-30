// RUST CORE BOUNDARY MAPPERS (Wave 3 / Win-2)
//
// Boundary adapters between the Rust shared core's FFI types and the app's native
// transcription types. Mirrors the macOS `RustCoreMapping` enum in `RustRetry.swift`.
//
//  - HwTranscriptionException -> TranscriptionException (the app's error type)
//  - TranscribeParams builder  (raw vocabulary, explicit audio mime, apiKey, etc.)
//  - CloudTranscriptionProvider -> HwProvider  (health + routed)
//  - routed X-STT-Provider header string -> HwProvider
//  - HW-Cloud / routed 402 credit + 413 size context parsing from a response body
//
// TODO-verify (Windows/CI): Rust shared-core swap — compile-only; verify in CI.

using System.Text.Json;
using HyperWhisper.Models;

// Binding types. Qualify aggressively at call sites where collisions exist
// (`TranscribeParams`, `HwProvider`, `HwTranscript`, `Header` all live here).
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.Transcription;

internal static class RustCoreMapping
{
    /// <summary>
    /// The standard retry give-up mapper: re-run the provider's core parser on a
    /// non-2xx response (which throws the classified <see cref="HwTranscriptionException"/>)
    /// and map it to a <see cref="TranscriptionException"/> tagged with
    /// <paramref name="provider"/> and the response status. A non-throwing parse
    /// (unexpected on a non-2xx) yields <see cref="TranscriptionErrorCode.Unknown"/>.
    /// Dedups the identical per-provider <c>Parse&lt;X&gt;Error</c> statics.
    /// </summary>
    internal static TranscriptionException ParseProviderError(
        Action parse, string provider, HttpResponse resp)
    {
        try
        {
            parse();
            // 2xx never reaches here; a non-error parse is unexpected.
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, "Unexpected non-error response", provider, (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            return MapTranscriptionError(ex, provider, (int)resp.@status);
        }
    }

    /// <summary>
    /// Map a core <see cref="HwTranscriptionException"/> (thrown by every
    /// build/parse FFI fn) to the app's <see cref="TranscriptionException"/>.
    ///
    /// <paramref name="providerName"/> is the display name for messaging.
    /// <paramref name="insufficientCredits"/> + credit numbers carry the HW-Cloud
    /// 402 context for QuotaExceeded; the file-too-large bytes/limit carry the
    /// HW-Cloud / routed 413 context — both pulled natively from the response body.
    /// </summary>
    internal static TranscriptionException MapTranscriptionError(
        HwTranscriptionException error,
        string providerName,
        int? httpStatusCode = null,
        bool insufficientCredits = false,
        int creditsRemaining = 0,
        int creditsRequired = 0,
        long fileTooLargeBytes = 0,
        long fileTooLargeLimit = 0)
    {
        switch (error)
        {
            case HwTranscriptionException.Unauthorized:
                return new TranscriptionException(
                    TranscriptionErrorCode.Unauthorized,
                    $"Invalid {providerName} API key",
                    providerName,
                    httpStatusCode);

            case HwTranscriptionException.QuotaExceeded:
                // HW Cloud / routed: a 402 is "out of credits". Surface the richer
                // numbers when the caller pulled the credit context. The app has no
                // distinct "insufficient credits" code; QuotaExceeded carries it.
                if (insufficientCredits)
                {
                    return new TranscriptionException(
                        TranscriptionErrorCode.QuotaExceeded,
                        $"Insufficient credits (remaining: {creditsRemaining}, required: {creditsRequired})",
                        providerName,
                        httpStatusCode ?? 402);
                }
                return new TranscriptionException(
                    TranscriptionErrorCode.QuotaExceeded,
                    $"{providerName} quota exceeded",
                    providerName,
                    httpStatusCode);

            case HwTranscriptionException.FileTooLarge:
                return new TranscriptionException(
                    TranscriptionErrorCode.FileTooLarge,
                    fileTooLargeLimit > 0
                        ? $"Audio file ({fileTooLargeBytes / 1_048_576.0:F1} MB) exceeds {providerName}'s {fileTooLargeLimit / 1_048_576} MB limit"
                        : $"Audio file too large for {providerName}",
                    providerName,
                    httpStatusCode ?? 413);

            case HwTranscriptionException.RateLimited rateLimited:
                return new TranscriptionException(
                    TranscriptionErrorCode.RateLimited,
                    "Rate limited",
                    providerName,
                    httpStatusCode ?? 429,
                    // Saturate instead of truncating: retryAfterSecs is a ulong from
                    // the core, so an unchecked (int) cast on a huge/hostile value
                    // would wrap to a negative. Clamp to int.MaxValue.
                    rateLimited.@retryAfterSecs.HasValue
                        ? (int)Math.Min(rateLimited.@retryAfterSecs.Value, int.MaxValue)
                        : null);

            case HwTranscriptionException.ProviderUnavailable providerUnavailable:
                return new TranscriptionException(
                    TranscriptionErrorCode.ProviderUnavailable,
                    $"{providerName} unavailable",
                    providerName,
                    providerUnavailable.@status);

            case HwTranscriptionException.NoSpeech:
                return new TranscriptionException(
                    TranscriptionErrorCode.NoSpeechDetected,
                    "No speech detected in audio",
                    providerName,
                    httpStatusCode);

            case HwTranscriptionException.BadRequest badRequest:
                // 400-class. Surface the upstream message; an empty message
                // collapses to a generic invalid-request.
                return new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    string.IsNullOrEmpty(badRequest.@message) ? "Invalid request" : badRequest.@message,
                    providerName,
                    badRequest.@status);

            case HwTranscriptionException.Parse parse:
                return new TranscriptionException(
                    TranscriptionErrorCode.InvalidRequest,
                    string.IsNullOrEmpty(parse.@message)
                        ? $"Invalid response from {providerName}"
                        : parse.@message,
                    providerName,
                    httpStatusCode);

            default:
                return new TranscriptionException(
                    TranscriptionErrorCode.Unknown,
                    error.Message,
                    providerName,
                    httpStatusCode);
        }
    }

    /// <summary>
    /// Build a core <see cref="TranscribeParams"/> from the platform's inputs.
    ///
    /// The core builds the vocabulary CSV itself from <paramref name="vocabulary"/>
    /// (trim + drop-empty, no lowercase/dedup) — pass the RAW term list, do NOT
    /// pre-encode. <paramref name="audioMime"/> is passed explicitly rather than
    /// letting the core re-resolve.
    /// </summary>
    internal static TranscribeParams TranscribeParams(
        string audioPath,
        string audioMime,
        string? language,
        IReadOnlyList<string> vocabulary,
        string apiKey = "",
        string model = "",
        string? prompt = null,
        string? baseUrl = null,
        string? licenseKey = null,
        string? deviceId = null,
        string? routedProvider = null,
        string? routedModel = null,
        string? routedDomain = null)
    {
        return new TranscribeParams(
            @apiKey: apiKey,
            @model: model,
            @language: language,
            @vocabulary: new List<string>(vocabulary),
            @prompt: prompt,
            @temperature: null,
            @audioPath: audioPath,
            @audioMime: audioMime,
            @baseUrl: baseUrl,
            @licenseKey: licenseKey,
            @deviceId: deviceId,
            @routedProvider: routedProvider,
            @routedModel: routedModel,
            @routedDomain: routedDomain);
    }

    /// <summary>
    /// Map the app's <see cref="CloudTranscriptionProvider"/> to an
    /// <see cref="HwProvider"/>. Used by the health probes
    /// (<c>BuildHealthRequest</c> / <c>ParseHealthResponse</c>). The three
    /// HW-Cloud-routed providers map onto the core's routed cases; every direct
    /// vendor maps 1:1.
    /// </summary>
    internal static HwProvider HwProviderFor(CloudTranscriptionProvider provider) => provider switch
    {
        CloudTranscriptionProvider.HyperWhisperCloud => HwProvider.HyperWhisperCloud,
        CloudTranscriptionProvider.OpenAI => HwProvider.Openai,
        CloudTranscriptionProvider.Groq => HwProvider.Groq,
        CloudTranscriptionProvider.Deepgram => HwProvider.Deepgram,
        CloudTranscriptionProvider.AssemblyAI => HwProvider.Assemblyai,
        CloudTranscriptionProvider.ElevenLabs => HwProvider.Elevenlabs,
        CloudTranscriptionProvider.Mistral => HwProvider.Mistral,
        CloudTranscriptionProvider.Soniox => HwProvider.Soniox,
        CloudTranscriptionProvider.Gemini => HwProvider.Gemini,
        CloudTranscriptionProvider.Grok => HwProvider.Grok,
        CloudTranscriptionProvider.MicrosoftAzureSpeech => HwProvider.AzureMai,
        CloudTranscriptionProvider.GoogleSpeech => HwProvider.GoogleChirp,
        _ => HwProvider.HyperWhisperCloud
    };

    /// <summary>
    /// Map a routed <c>X-STT-Provider</c> header value to an
    /// <see cref="HwProvider"/>. Used by the routed path.
    /// </summary>
    internal static HwProvider HwProviderForRouted(string sttProviderHeader) => sttProviderHeader switch
    {
        "azure-mai" => HwProvider.AzureMai,
        "google-chirp" => HwProvider.GoogleChirp,
        _ => HwProvider.HyperWhisperCloud
    };

    /// <summary>
    /// Read the HW-Cloud 402 credit context (<c>credits_remaining</c> /
    /// <c>credits_required</c>) from an error response body. Returns (0, 0) absent.
    /// </summary>
    internal static (int Remaining, int Required) CreditContext(HttpResponse response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.@body);
            var root = doc.RootElement;
            var remaining = root.TryGetProperty("credits_remaining", out var r) && r.TryGetInt32(out var rv) ? rv : 0;
            var required = root.TryGetProperty("credits_required", out var q) && q.TryGetInt32(out var qv) ? qv : 0;
            return (remaining, required);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Read the HW-Cloud / routed 413 size context (<c>actual_size_mb</c> /
    /// <c>max_size_mb</c>) from an error response body, in bytes. Returns (0, 0)
    /// when absent. Mirrors macOS <c>fileTooLargeContext</c>.
    /// </summary>
    internal static (long Bytes, long Limit) FileTooLargeContext(HttpResponse response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.@body);
            var root = doc.RootElement;
            long limit = root.TryGetProperty("max_size_mb", out var m) && m.TryGetInt64(out var mv)
                ? mv * 1_048_576
                : 0;
            long bytes = root.TryGetProperty("actual_size_mb", out var a) && a.TryGetDouble(out var av)
                ? (long)(av * 1_048_576)
                : 0;
            return (bytes, limit);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Read the server-detected language from a HW-Cloud success body. Empty -> null.
    /// </summary>
    internal static string? DetectedLanguage(HttpResponse response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.@body);
            if (doc.RootElement.TryGetProperty("language", out var langEl))
            {
                var lang = langEl.GetString()?.Trim();
                return string.IsNullOrEmpty(lang) ? null : lang;
            }
        }
        catch { }
        return null;
    }
}
