// RUST SINGLE-SHOT TRANSCRIBE RUNNER
//
// Every direct-vendor BYOK provider that transcribes in ONE request (Deepgram,
// ElevenLabs, Grok, Groq, Mistral, OpenAI) drove the shared core through the
// identical seven-step sequence, copied into each service:
//
//   1. RustRetry.PerformAsync(client, buildRequest, parseError, token[, timeout])
//   2. catch HwTranscriptionException from the BUILD fn -> MapTranscriptionError
//   3. cancellationToken.ThrowIfCancellationRequested()
//   4. run the core PARSE fn on the 2xx response
//   5. catch HwTranscriptionException from the PARSE fn -> MapTranscriptionError
//   6. log the "COMPLETE" banner, character count and elapsed time
//   7. return transcript.text
//
// Only the provider name and the two core FFI functions differed, so the shape
// of a single-shot transcription was written out six times and could drift six
// ways. This is the one copy. It emits exactly what the six services emitted,
// log lines included.
//
// NOT for the multi-step providers: AssemblyAI, Gemini and Soniox upload and
// then poll. The two HyperWhisper-Cloud paths are out for DIFFERENT reasons:
//
//   - HyperWhisperCloudService really does carry extra context into
//     MapTranscriptionError (402 credit numbers, 413 size, and the provider
//     diagnostics it attaches on a no-speech parse failure), and its completion
//     banner prints two extra credit lines.
//
//   - HyperWhisperRoutedTranscriptionClient does NOT. Both of its
//     MapTranscriptionError calls are the same plain two-arg form used below,
//     so that is not what keeps it out. It is out because (a) its retry give-up
//     mapper is its own MapRoutedError — which enriches the 402 credit / 413
//     size context off the response body — where this runner hard-codes
//     RustCoreMapping.ParseProviderError, and (b) it logs one
//     "Completed · totalMs=… · chars=…" line instead of the banner below.
//
// Those keep their own sequences.

using System.Diagnostics;
using System.Net.Http;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.Transcription;

internal static class RustSingleShot
{
    /// <summary>
    /// Drive one core-built request through the shared retry loop, parse the
    /// response with the core, and return the transcript text.
    /// </summary>
    /// <param name="httpClient">The service's client, passed to <see cref="RustRetry"/>.</param>
    /// <param name="provider">
    /// Display name used for error tagging ("Groq", "OpenAI", …). Its
    /// upper-case form is the completion log banner, which is how every caller
    /// already spelled it ("Groq" -> "========== GROQ TRANSCRIPTION COMPLETE ==========").
    /// </param>
    /// <param name="buildRequest">The core's <c>&lt;Provider&gt;BuildTranscribeRequest</c>.</param>
    /// <param name="parseResponse">The core's <c>&lt;Provider&gt;ParseTranscribeResponse</c>.</param>
    /// <param name="totalSw">Stopwatch started by the caller, reported as total time.</param>
    /// <param name="perAttemptTimeout">Per-attempt timeout; only Grok sets one.</param>
    internal static async Task<string> TranscribeAsync(
        HttpClient httpClient,
        string provider,
        Func<HttpRequest> buildRequest,
        Func<HttpResponse, HwTranscript> parseResponse,
        Stopwatch totalSw,
        CancellationToken cancellationToken,
        TimeSpan? perAttemptTimeout = null)
    {
        HttpResponse response;
        try
        {
            response = await RustRetry.PerformAsync(
                httpClient,
                buildRequest: buildRequest,
                parseError: resp => RustCoreMapping.ParseProviderError(parseResponse, provider, resp),
                cancellationToken: cancellationToken,
                perAttemptTimeout: perAttemptTimeout);
        }
        catch (HwTranscriptionException ex)
        {
            // Thrown by the core's build fn (request-build validation).
            throw RustCoreMapping.MapTranscriptionError(ex, provider);
        }

        cancellationToken.ThrowIfCancellationRequested();

        HwTranscript transcript;
        try
        {
            transcript = parseResponse(response);
        }
        catch (HwTranscriptionException ex)
        {
            throw RustCoreMapping.MapTranscriptionError(ex, provider);
        }

        LoggingService.Info($"========== {provider.ToUpperInvariant()} TRANSCRIPTION COMPLETE ==========");
        LoggingService.Info($"  Characters: {transcript.@text.Length}");
        LoggingService.Info($"  Total time: {totalSw.ElapsedMilliseconds}ms");
        return transcript.@text;
    }
}
