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
// 429/5xx vs transport split: the core's 8-attempt schedule is the GLOBAL bound
// and fully governs HTTP-status retries (429/5xx — the server is up, backing off
// is productive). Transport failures (no HTTP response at all) are additionally
// capped platform-side because grinding through all 8 attempts against a dead
// network stretches a doomed request to ~27 min:
//   - HttpRequestException (connection refused/reset, DNS): max 4 attempts —
//     restores the 1.7.0 ~14s failure envelope.
//   - Per-attempt timeout (OperationCanceledException with the CALLER token not
//     cancelled): max 2 attempts — the one useful retry is the one after
//     onTransportError rebuilds the pool; a second identical timeout means the
//     wait was already spent, fail fast.
// Under-cap transport failures still ask the core NextRetry(attempt, 503, ...)
// for the backoff delay, so the schedule stays core-owned.
//
// Backoff budget (issue #379): 8 attempts of raw exponential backoff is ~127s of
// sleep, so a hard-down provider used to take ~150s to fail. The core owns the
// rule (NextRetryWithinBudget); this driver just carries the running total of the
// delays the core handed it (sleptMs) back into the next decision. The core gives
// up when the next sleep would push that total past budgetMs (default
// RetryDefaultBudgetMs() == 30s), which stops at attempt 5 after 15s of backoff,
// before the 16/32/64s sleeps are reached. budgetMs: 0 means unbounded — exactly
// the old behaviour. This is a THIRD, orthogonal bound: MaxTransportAttempts and
// MaxTimeoutAttempts are per-failure-kind caps and are unchanged.
//
// sleptMs is BACKOFF ONLY — the time a failed request itself took is deliberately
// NOT charged to it. A large upload (GrokSttService allows a 5-min base / 30-min
// cap per attempt) would otherwise blow a 30s budget before its first 502 even
// arrived and get zero retries, and a 30s perAttemptTimeout would make
// MaxTimeoutAttempts below unreachable. Request duration is bounded by those
// per-attempt timeouts and by the two caps, not by the budget.
//
// TODO-verify (Windows/CI): Rust shared-core swap — compile-only; verify in CI.

using System.Net.Http;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.Transcription;

internal static class RustRetry
{
    /// <summary>Max attempts that may end in a connection-level transport error
    /// (HttpRequestException) before failing terminally. See header comment.</summary>
    internal const int MaxTransportAttempts = 4;

    /// <summary>Max attempts that may end in a per-attempt timeout before failing
    /// terminally (i.e. one retry, giving the post-onTransportError fresh pool a
    /// single chance). See header comment.</summary>
    internal const int MaxTimeoutAttempts = 2;

    /// <summary>
    /// Source-compatible overload for providers with a fixed <see cref="HttpClient"/>
    /// for the life of the service. Services that can swap their client mid-sequence
    /// (DNS-recovery rebuild) must use the <c>Func&lt;HttpClient&gt;</c> shape so each
    /// attempt resolves the CURRENT client instead of pinning the pre-rebuild one.
    /// </summary>
    internal static Task<HttpResponse> PerformAsync(
        HttpClient client,
        Func<HttpRequest> buildRequest,
        Func<HttpResponse, TranscriptionException> parseError,
        CancellationToken cancellationToken,
        Func<Exception, Task>? onTransportError = null,
        TimeSpan? perAttemptTimeout = null,
        ulong? budgetMs = null)
    {
        return PerformAsync(() => client, buildRequest, parseError, cancellationToken, onTransportError, perAttemptTimeout, budgetMs);
    }

    /// <summary>
    /// Drive <paramref name="buildRequest"/>'s output through the executor + core
    /// retry loop.
    /// <list type="bullet">
    /// <item>On a 2xx response, returns the captured <see cref="HttpResponse"/>.</item>
    /// <item>On a non-2xx, parses <c>Retry-After</c> natively, asks the core
    /// <c>NextRetryWithinBudget(...)</c>, and either sleeps <c>delayMs</c> and
    /// retries or gives up.</item>
    /// <item>On a transport error with no HTTP response (network blip / timeout),
    /// treats it as a retryable 503-equivalent
    /// (<c>NextRetryWithinBudget(attempt, 503, "", null, …)</c>).</item>
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
    /// flapping network can't thrash the pool.
    ///
    /// <paramref name="clientProvider"/> is resolved fresh on EVERY attempt so a
    /// service that swaps its <see cref="HttpClient"/> mid-sequence (e.g. the DNS
    /// rebuild in <c>onTransportError</c>) actually sends the next attempt on the
    /// new pool instead of the stale pre-rebuild one.
    ///
    /// <paramref name="perAttemptTimeout"/>, when set, bounds each individual
    /// attempt (each attempt gets the full budget); expiry counts against
    /// <see cref="MaxTimeoutAttempts"/>.
    ///
    /// <paramref name="budgetMs"/> bounds the TOTAL BACKOFF this sequence may
    /// sleep — not its wall clock, and explicitly not the time the failed
    /// requests themselves took. The core gives up rather than start a sleep that
    /// would push the running total past it. <c>null</c> = the core's interactive
    /// <c>RetryDefaultBudgetMs()</c> (30s); <c>0</c> = unbounded (the pre-#379
    /// behaviour), which a future batch caller can opt into.
    /// </summary>
    internal static async Task<HttpResponse> PerformAsync(
        Func<HttpClient> clientProvider,
        Func<HttpRequest> buildRequest,
        Func<HttpResponse, TranscriptionException> parseError,
        CancellationToken cancellationToken,
        Func<Exception, Task>? onTransportError = null,
        TimeSpan? perAttemptTimeout = null,
        ulong? budgetMs = null)
    {
        uint attempt = 0;
        // One-shot-per-sequence gate for the recovery hook.
        var didRecoverThisSequence = false;
        // Platform-side transport caps (see header comment). The core's
        // MAX_ATTEMPTS=8 remains the global bound across ALL failure kinds.
        var transportFailures = 0;
        var timeoutFailures = 0;
        var budget = budgetMs ?? HyperwhisperCoreMethods.RetryDefaultBudgetMs();
        // Running total of the backoff the core has asked this sequence to sleep.
        // Accumulated across the whole loop and never reset, so the budget bounds
        // the sequence rather than any single sleep.
        var sleptMs = 0UL;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt += 1;

            var request = buildRequest();

            // Per-attempt timeout: linked CTS created INSIDE the loop so each
            // attempt gets the full budget (backoff sleeps run on the caller
            // token and must not consume the next attempt's budget).
            using var attemptCts = perAttemptTimeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            attemptCts?.CancelAfter(perAttemptTimeout!.Value);
            var attemptToken = attemptCts?.Token ?? cancellationToken;

            HttpResponse response;
            try
            {
                response = await RustHttpExecutor
                    .ExecuteAsync(request, clientProvider(), attemptToken)
                    .ConfigureAwait(false);
            }
            // Ordering is load-bearing: genuine caller cancellation must be
            // tested against the CALLER token (the linked attempt token is also
            // cancelled when the per-attempt timeout fires) and never retried.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // No HTTP response. Classify by exception type — the caller token
                // is NOT cancelled here (filtered above), so any cancellation
                // exception is the per-attempt timeout firing.
                var isTimeout = ex is OperationCanceledException;
                var failures = isTimeout ? ++timeoutFailures : ++transportFailures;
                var cap = isTimeout ? MaxTimeoutAttempts : MaxTransportAttempts;

                if (failures >= cap)
                {
                    LoggingService.Warn(
                        $"RustRetry: {(isTimeout ? "timeout" : "transport")} failure {failures}/{cap} on attempt {attempt} — giving up: {ex.Message}");
                    throw new TranscriptionException(
                        TranscriptionErrorCode.NetworkError,
                        isTimeout ? $"Request timed out after {failures} attempts: {ex.Message}" : ex.Message,
                        providerName: null,
                        innerException: ex);
                }

                // Under cap — the core still owns the backoff schedule; treat as a
                // retryable 503-equivalent.
                var decision = HyperwhisperCoreMethods.NextRetryWithinBudget(
                    @attempt: attempt,
                    @status: 503,
                    @body: "",
                    @retryAfter: null,
                    @sleptMs: sleptMs,
                    @budgetMs: budget);

                switch (decision)
                {
                    case RetryDecision.Retry retry:
                        LoggingService.Warn(
                            $"RustRetry: {(isTimeout ? "timeout" : "transport")} failure {failures}/{cap} on attempt {attempt} — retrying in {retry.@delayMs}ms: {ex.Message}");
                        if (!didRecoverThisSequence && onTransportError != null)
                        {
                            didRecoverThisSequence = true;
                            await onTransportError(ex).ConfigureAwait(false);
                        }
                        var admittedDelayMs = AdmittedSleepMs(retry.@delayMs, sleptMs, budget);
                        sleptMs += admittedDelayMs;
                        await SleepAsync(admittedDelayMs, cancellationToken).ConfigureAwait(false);
                        continue;

                    case RetryDecision.GiveUp:
                    default:
                        LogGiveUp(attempt, 503, "", null, sleptMs, budget);
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

            // Floor at 0: a negative Retry-After (e.g. "-1") is meaningless and a
            // raw `(ulong)(-1)` would wrap to a huge delay → Task.Delay throws.
            var retryAfterMs = retryAfter.HasValue ? (ulong)Math.Max(0, retryAfter.Value) : (ulong?)null;

            var nonOkDecision = HyperwhisperCoreMethods.NextRetryWithinBudget(
                @attempt: attempt,
                @status: response.@status,
                @body: bodyText,
                @retryAfter: retryAfterMs,
                @sleptMs: sleptMs,
                @budgetMs: budget);

            switch (nonOkDecision)
            {
                case RetryDecision.Retry retry:
                    var admittedDelayMs = AdmittedSleepMs(retry.@delayMs, sleptMs, budget);
                    sleptMs += admittedDelayMs;
                    await SleepAsync(admittedDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;

                case RetryDecision.GiveUp:
                default:
                    LogGiveUp(attempt, response.@status, bodyText, retryAfterMs, sleptMs, budget);
                    // The core's RateLimited carries no Retry-After (it doesn't
                    // read the header); enrich the give-up error with the value we
                    // parsed here so the "try again in N seconds" UI is preserved.
                    throw EnrichRateLimited(parseError(response), retryAfter);
            }
        }
    }

    /// <summary>
    /// Log a give-up with a reason a support engineer can act on: the backoff
    /// budget running out looks nothing like the attempt ceiling being hit, and
    /// the two want different fixes. Determined by re-asking the core WITHOUT the
    /// budget — if it would still have retried, the budget is what stopped us.
    /// One extra FFI call, only ever on the give-up path. Log line only: nothing
    /// downstream branches on this.
    /// </summary>
    private static void LogGiveUp(uint attempt, ushort status, string body, ulong? retryAfter, ulong sleptMs, ulong budgetMs)
    {
        var unbudgeted = HyperwhisperCoreMethods.NextRetry(
            @attempt: attempt,
            @status: status,
            @body: body,
            @retryAfter: retryAfter);

        LoggingService.Warn(unbudgeted is RetryDecision.Retry
            ? $"RustRetry: giving up on the retry BUDGET after attempt {attempt} — {sleptMs}ms of backoff slept of {budgetMs}ms (status {status})"
            : $"RustRetry: giving up on the retry POLICY after attempt {attempt} of {HyperwhisperCoreMethods.RetryMaxAttempts()} — {sleptMs}ms of backoff slept (status {status})");
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

    /// <summary>
    /// Adds 0-30% jitter and caps the actual admitted sleep at the declared
    /// remaining budget. <paramref name="jitterUnit"/> is a pure test seam;
    /// production callers use <see cref="Random.Shared"/> across its full range.
    /// </summary>
    internal static ulong AdmittedSleepMs(
        ulong coreDelayMs,
        ulong sleptMs,
        ulong budgetMs,
        double? jitterUnit = null)
    {
        var unit = Math.Clamp(jitterUnit ?? Random.Shared.NextDouble(), 0, 1);
        var jittered = (ulong)(coreDelayMs * (1.0 + unit * 0.3));
        if (budgetMs == 0) return jittered;
        var remaining = budgetMs > sleptMs ? budgetMs - sleptMs : 0;
        return Math.Min(jittered, remaining);
    }

    private static Task SleepAsync(ulong admittedDelayMs, CancellationToken cancellationToken)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(admittedDelayMs), cancellationToken);
    }
}
