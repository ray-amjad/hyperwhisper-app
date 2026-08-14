/**
 * Classification of errors thrown by the Polar SDK.
 *
 * The SDK signals "no such license key" by THROWING, not by returning a
 * non-granted status, so the `catch` around a Polar call sees an unknown key
 * and a Polar outage on the same line. Telling them apart is what lets the
 * server label the first `not_entitled` and the second `lookup_failed` — see
 * `LicenseInvalidReason` in `license-validation-probe.ts` for why that
 * distinction matters and who consumes it.
 */

/**
 * Whether a thrown Polar SDK error means "Polar positively reports that this
 * license key does not exist" — as opposed to "we could not ask Polar".
 *
 * ## How the SDK signals it (@polar-sh/sdk 0.34.17)
 *
 * `customerPortal.licenseKeys.validate` maps a 404 with a well-formed JSON body
 * to a `ResourceNotFound`, which extends `PolarError` and sets:
 *
 * - `name`       — `"ResourceNotFound"` (assigned in the constructor)
 * - `error`      — `"ResourceNotFound"` (a required string literal in the
 *                  response schema, so the body was parsed and validated)
 * - `statusCode` — `404` (`PolarError` copies `response.status`)
 *
 * ## Why all three are required, and why this is not an `instanceof`
 *
 * This check must fail toward `lookup_failed`. Getting it wrong in the
 * `not_entitled` direction marks a real outage as an ordinary rejection and
 * silences the alert, which is the exact failure this classification exists to
 * prevent. So it is deliberately narrow, and every loosening below was
 * rejected on purpose:
 *
 * - `statusCode === 404` alone is NOT enough. A 404 whose body is not JSON —
 *   a CDN/proxy error page, or a misconfigured base URL hitting a wrong route
 *   — does not match the SDK's 404 matcher and surfaces as a generic
 *   `SDKError` that still carries `statusCode === 404`. That is an incident,
 *   not a verdict. Requiring the parsed `error` literal excludes it.
 * - `instanceof ResourceNotFound` is NOT used. The SDK ships parallel ESM and
 *   CommonJS builds with separate class identities, so an `instanceof` against
 *   a deep subpath import can silently evaluate false depending on how each
 *   copy resolved — which would be a safe failure here, but a confusing one.
 *   Reading the fields the constructor sets is identity-proof, and needs no
 *   import from an unstable internal path (`models/errors/` has no barrel).
 *
 * Anything else — a 5xx `SDKError`, an `Unauthorized` from a rotated token, a
 * `ConnectionError` / `RequestTimeoutError` / `RequestAbortedError` (transport
 * failures, which extend `HTTPClientError` and carry no `statusCode` at all),
 * a `ResponseValidationError`, a plain `Error`, or an SDK shape that changed
 * under us — returns false and is treated as a failed lookup.
 */
export function isPolarKeyNotFoundError(err: unknown): boolean {
  if (typeof err !== "object" || err === null) {
    return false;
  }

  const candidate = err as {
    statusCode?: unknown;
    name?: unknown;
    error?: unknown;
  };

  return (
    candidate.statusCode === 404 &&
    candidate.name === "ResourceNotFound" &&
    candidate.error === "ResourceNotFound"
  );
}
