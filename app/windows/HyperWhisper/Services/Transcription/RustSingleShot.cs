// RUST SINGLE-SHOT TRANSCRIBE RUNNER
//
// The one copy of the sequence a direct-vendor BYOK provider runs when it
// transcribes in a SINGLE request. Each such service used to spell it out
// inline, identically:
//
//   1. RustRetry.PerformAsync(client, buildRequest, parseError, token[, timeout])
//   2. catch HwTranscriptionException from the BUILD fn -> MapTranscriptionError
//   3. cancellationToken.ThrowIfCancellationRequested()
//   4. run the core PARSE fn on the 2xx response
//   5. catch HwTranscriptionException from the PARSE fn -> MapTranscriptionError
//   6. log the "COMPLETE" banner, character count and elapsed time
//   7. return transcript.text
//
// Only the provider name and the two core FFI functions differed, and all three
// are parameters. Two things are NOT parameters, and that is what decides
// whether a provider can run on this:
//
//   - the retry give-up mapper is always ParseProviderError below, which adds
//     nothing to the core's own classification of the error body;
//   - the tail is always the three-line banner below, derived from `provider`.
//
// A provider needing either to differ keeps its own sequence.

using System.Diagnostics;
using System.Net.Http;
using HyperWhisper.Models;
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
                parseError: resp => ParseProviderError(parseResponse, provider, resp),
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

    /// <summary>
    /// The give-up mapper handed to <see cref="RustRetry"/> above. Re-runs
    /// <paramref name="parseResponse"/> — the same core parser used on success —
    /// over the non-2xx response, which throws the core's classified
    /// <see cref="HwTranscriptionException"/>, and maps it to a
    /// <see cref="TranscriptionException"/> tagged with <paramref name="provider"/>
    /// and the response status. A non-throwing parse (unexpected on a non-2xx)
    /// yields <see cref="TranscriptionErrorCode.Unknown"/>; its transcript is
    /// discarded — only the classification is of interest here.
    /// </summary>
    private static TranscriptionException ParseProviderError(
        Func<HttpResponse, HwTranscript> parseResponse, string provider, HttpResponse resp)
    {
        try
        {
            parseResponse(resp);
            // 2xx never reaches here; a non-error parse is unexpected.
            return new TranscriptionException(
                TranscriptionErrorCode.Unknown, "Unexpected non-error response", provider, (int)resp.@status);
        }
        catch (HwTranscriptionException ex)
        {
            return RustCoreMapping.MapTranscriptionError(ex, provider, (int)resp.@status);
        }
    }
}
