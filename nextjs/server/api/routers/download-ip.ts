import { isIP } from "node:net";

function firstForwardedIP(value: string | null): string | undefined {
  return value?.split(",")[0]?.trim();
}

function isPlainIP(candidate: string): boolean {
  return !candidate.includes("%") && isIP(candidate) !== 0;
}

/**
 * Extract a well-formed client IP from request headers.
 *
 * `cf-connecting-ip` comes first: Cloudflare sets it on every request to the
 * Worker and overwrites any value the client sent, so it is the one header a
 * caller cannot forge. `x-forwarded-for` is NOT trustworthy on Cloudflare (it
 * appends to a client-supplied chain, so the first entry is the client's
 * choice); it and `x-real-ip` remain only as fallbacks for local dev, where
 * there is no Cloudflare edge. `x-vercel-forwarded-for` is ignored: off Vercel
 * any client can send it.
 */
export function getClientIPFromHeaders(headers: Headers): string {
  const candidates = [
    headers.get("cf-connecting-ip")?.trim(),
    firstForwardedIP(headers.get("x-forwarded-for")),
    headers.get("x-real-ip")?.trim(),
  ];

  for (const candidate of candidates) {
    if (candidate && isPlainIP(candidate)) return candidate;
  }

  return "unknown";
}
