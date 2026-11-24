// SHARED FAILURE VOCABULARY
// The provider error classes live in providers/types.ts. The kind of failure is
// a stable contract that lib/ also reads (latency-report.ts stores it on the
// public /latency page), so it lives down here and providers import it.

/**
 * Why a provider attempt was deemed unavailable. Lets the route and dashboards
 * distinguish "the upstream was slow and we gave up" (`timeout` — the request
 * might have succeeded with more budget) from "the upstream actually failed"
 * (`upstream_5xx` / `rate_limit`) and from "we got a 2xx we couldn't use"
 * (`bad_response` — e.g. ElevenLabs' geo-block HTML or an empty gzip body),
 * WITHOUT having to correlate a separate provider-level log line by requestId.
 */
export type ProviderUnavailableKind =
  | 'timeout'        // our per-request budget elapsed; upstream may have succeeded given more time
  | 'network_error'  // connection failed/reset before any response
  | 'rate_limit'     // upstream 429
  | 'upstream_5xx'   // upstream 5xx server error
  | 'bad_response'   // 2xx with an unusable body (geo-block HTML, empty, non-JSON)
  | 'unknown';
