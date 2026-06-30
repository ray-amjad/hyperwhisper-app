// RUST RETRY WRAPPER (Wave 3 / Win-2)
//
// The retry policy is owned by the Rust core: `NextRetry(attempt, status, body,
// retryAfter)` classifies the (status, body) and returns a `RetryDecision` —
// `Retry(delayMs)` or `GiveUp`. This wrapper drives a single request through that
// decision loop via `RustHttpExecutor`, keeping ALL I/O, cancellation, and
// `Retry-After` header parsing on the platform side. Mirrors the macOS
// `RustRetry.swift`.
//
// Behavioral note (flagged in the PR): this unifies the previously-divergent
// per-provider retry loops onto the core's `NextRetry`. `NextRetry` is 1-based on
// the attempt that just FAILED and gives up at attempt >= RetryMaxAttempts()
// (== 8; exponential 1s, 2s, 4s, … 64s backoff), honoring `Retry-After` clamped
// to RetryMaxRetryAfterSecs() (== 10s). The core is RNG-free, so a small 0–30%
// jitter is added platform-side at the sleep point (see SleepAsync) to avoid a
// thundering herd. Poll loops in the multi-step providers do NOT go through this
// wrapper.
//
// TODO-verify (Windows/CI): Rust shared-core swap — compile-only; verify in CI.

using System.Net.Http;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.Transcription;

internal static class RustRetry
{
    /// <summary>
    /// Drive <paramref name="buildRequest"/>'s output through the executor + core
    /// retry loop.
    /// <list type="bullet">
    /// <item>On a 2xx response, returns the captured <see cref="HttpResponse"/>.</item>
    /// <item>On a non-2xx, parses <c>Retry-After</c> natively, asks the core
    /// <c>NextRetry(...)</c>, and either sleeps <c>delayMs</c> and retries or
    /// gives up.</item>
    /// <item>On a transport error with no HTTP response (network blip / timeout),
    /// treats it as a retryable 503-equivalent
    /// (<c>NextRetry(attempt, 503, "", null)</c>).</item>
    /// <item>On cancellation, throws <see cref="OperationCanceledException"/>.</item>
    /// <item>On give-up, throws the caller-mapped <see cref="TranscriptionException"/>
    /// derived from the last status/body (via <paramref name="parseError"/>), so
    /// callers surface the real failure rather than a generic one.</item>
    /// </list>
    /// <paramref name="buildRequest"/> is a delegate so the same
    /// <see cref="HttpRequest"/> is re-issued each attempt (the body is a file ref,
    /// so re-streaming is cheap and correct).
    ///
    /// <paramref name="onTransportError"/> is an OPTIONAL one-shot recovery hook
    /// invoked in the transport-error path BEFORE the next retry sleeps. Fired at
    /// most once per call (mirroring macOS' <c>didResetThisSequence</c> gate) so a
    /// flapping network can't thrash the pool. Default null = no-op.
    /// </summary>
    internal static async Task<HttpResponse> PerformAsync(
        HttpClient client,
        Func<HttpRequest> buildRequest,
        Func<HttpResponse, TranscriptionException> parseError,
        CancellationToken cancellationToken,
        Func<Exception, Task>? onTransportError = null)
    {
        uint attempt = 0;
        // One-shot-per-sequence gate for the recovery hook.
        var didRecoverThisSequence = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt += 1;

            var request = buildRequest();

            HttpResponse response;
            try
            {
                response = await RustHttpExecutor
                    .ExecuteAsync(request, client, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Genuine cancellation — never retry.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // No HTTP response (network error, or a timeout surfacing as
                // TaskCanceledException with the token NOT cancelled) — treat as a
                // retryable 503-equivalent.
                var decision = HyperwhisperCoreMethods.NextRetry(
                    @attempt: attempt,
                    @status: 503,
                    @body: "",
                    @retryAfter: null);

                switch (decision)
                {
                    case RetryDecision.Retry retry:
                        if (!didRecoverThisSequence && onTransportError != null)
                        {
                            didRecoverThisSequence = true;
                            await onTransportError(ex).ConfigureAwait(false);
                        }
                        await SleepAsync(retry.@delayMs, cancellationToken).ConfigureAwait(false);
                        continue;

                    case RetryDecision.GiveUp:
                    default:
                        throw new TranscriptionException(
                            TranscriptionErrorCode.NetworkError,
                            ex.Message,
                            providerName: null,
                            innerException: ex);
                }
            }

            // 2xx → success.
            if (response.@status is >= 200 and <= 299)
            {
                return response;
            }

            // Non-2xx → consult the core retry decision.
            var bodyText = System.Text.Encoding.UTF8.GetString(response.@body);
            var retryAfter = ParseRetryAfterHeader(response);

            var nonOkDecision = HyperwhisperCoreMethods.NextRetry(
                @attempt: attempt,
                @status: response.@status,
                @body: bodyText,
                // Floor at 0: a negative Retry-After (e.g. "-1") is meaningless and a
                // raw `(ulong)(-1)` would wrap to a huge delay → Task.Delay throws.
                @retryAfter: retryAfter.HasValue ? (ulong)Math.Max(0, retryAfter.Value) : null);

            switch (nonOkDecision)
            {
                case RetryDecision.Retry retry:
                    await SleepAsync(retry.@delayMs, cancellationToken).ConfigureAwait(false);
                    continue;

                case RetryDecision.GiveUp:
                default:
                    // The core's RateLimited carries no Retry-After (it doesn't
                    // read the header); enrich the give-up error with the value we
                    // parsed here so the "try again in N seconds" UI is preserved.
                    throw EnrichRateLimited(parseError(response), retryAfter);
            }
        }
    }

    /// <summary>
    /// When <paramref name="error"/> is a RateLimited with no RetryAfterSeconds,
    /// fill in the <c>Retry-After</c> value parsed from the response header.
    /// Otherwise pass the error through unchanged.
    /// </summary>
    private static TranscriptionException EnrichRateLimited(TranscriptionException error, int? retryAfter)
    {
        if (retryAfter.HasValue
            && error.Code == TranscriptionErrorCode.RateLimited
            && !error.RetryAfterSeconds.HasValue)
        {
            return new TranscriptionException(
                error.Code,
                error.Message,
                error.ProviderName,
                error.HttpStatusCode,
                // Clamp to ≥0 so a negative Retry-After can't surface a
                // "try again in -1 seconds" message to the user.
                Math.Max(0, retryAfter.Value),
                error.InnerException,
                error.ProviderDiagnostics);
        }
        return error;
    }

    /// <summary>
    /// Parse the integer <c>Retry-After</c> header from a binding
    /// <see cref="HttpResponse"/> (case-insensitive). Mirrors macOS
    /// <c>parseRetryAfterHeader</c>.
    /// </summary>
    private static int? ParseRetryAfterHeader(HttpResponse response)
    {
        foreach (var header in response.@headers)
        {
            if (string.Equals(header.@name, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(header.@value.Trim(), out var seconds))
                {
                    return seconds;
                }
                return null;
            }
        }
        return null;
    }

    private static Task SleepAsync(ulong delayMs, CancellationToken cancellationToken)
    {
        // Add 0–30% randomized jitter on top of the core's deterministic backoff
        // so concurrent clients don't all retry in lockstep (thundering herd). The
        // core forbids RNG, so the jitter lives here — mirrors macOS RustRetry.sleep.
        var jittered = delayMs * (1.0 + Random.Shared.NextDouble() * 0.3);
        return Task.Delay(TimeSpan.FromMilliseconds(jittered), cancellationToken);
    }
}
