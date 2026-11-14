import { sha256Hex } from "@/lib/shared/sha256";

/**
 * The shared pieces of the website's structured auth log lines (#2955):
 * `logSignIn` (`./auth-license-key-plugin.ts`), `logMagicLinkVerify`
 * (`./auth.ts`) and `logRotate` (`server/api/routers/customer-rotate-key.ts`).
 * Each logger still builds its own line, so its field list and JSON key order
 * stay its own; only the client-IP hash and the outcome → console level live
 * here, so they cannot drift between copies.
 */

/**
 * The client IP as a 12-character SHA-256 hex prefix, or `null` when the
 * request carries no IP header. Repeated attempts from one client can be
 * correlated without the raw IP reaching a log.
 *
 * Deliberately NOT `getClientIPFromHeaders` (`server/api/routers/download-ip.ts`):
 * that one validates with `isIP`, answers `"unknown"` and also reads
 * `x-real-ip`, which would change the logged hashes.
 */
export function authLogIpHash(headers: Headers | undefined): string | null {
  // cf-connecting-ip is set by Cloudflare and cannot be forged; the
  // x-forwarded-for fallback only matters off Cloudflare (local dev, tests).
  const ip =
    headers?.get("cf-connecting-ip")?.trim() ||
    headers?.get("x-forwarded-for")?.split(",")[0]?.trim();

  return ip ? sha256Hex(ip).slice(0, 12) : null;
}

/**
 * Writes `line` as one JSON line at the level its outcome calls for: `"ok"` →
 * `console.log`, `"rejected"` → `console.warn`, anything else (`"failed"`,
 * `"error"`) → `console.error`.
 */
export function emitAuthEvent(
  line: Record<string, unknown>,
  outcome: string,
): void {
  const json = JSON.stringify(line);

  // The website's console output is its server log (Workers Logs), so this is the
  // sink, not leftover debugging.
  /* eslint-disable no-console */
  if (outcome === "ok") {
    console.log(json);
  } else if (outcome === "rejected") {
    console.warn(json);
  } else {
    console.error(json);
  }
  /* eslint-enable no-console */
}
