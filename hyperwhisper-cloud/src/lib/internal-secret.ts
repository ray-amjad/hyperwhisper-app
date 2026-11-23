// The shared secret between this service and the Next.js site, both
// directions: the site calls `/internal/license-cache/invalidate` with it, and
// the latency reporter calls the site's `/api/internal/latency` with it. One
// header name and one env read, so the two sides of the protocol cannot drift.

export const INTERNAL_SECRET_HEADER = 'x-internal-secret';

/** Read at call time, so a secret sync (or a test) that changes the env is seen. */
export function internalSecret(): string | undefined {
  return process.env.HYPERWHISPER_INTERNAL_SECRET;
}
