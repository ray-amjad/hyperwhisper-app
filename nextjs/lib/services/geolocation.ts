/**
 * Geolocation Service
 *
 * Resolves IP addresses to country names using the proxycheck.io API.
 * Used to enrich download records with geographic data for social proof.
 */
import { isIP } from "node:net";

const PROXYCHECK_API_KEY = process.env.PROXYCHECK_API_KEY;
const TIMEOUT_MS = 3000;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/**
 * Read `location.country_name` off one per-IP entry, accepting it only when the
 * response really carries a string there. The body is third-party JSON, so the
 * shape is checked rather than asserted: a numeric or nested `country_name`
 * would otherwise reach the caller typed as a string.
 */
function countryNameOf(entry: unknown): string | null {
  if (!isRecord(entry)) return null;

  const location = entry.location;

  if (!isRecord(location)) return null;

  const countryName = location.country_name;

  return typeof countryName === "string" ? countryName : null;
}

/**
 * Locate the per-IP entry in a proxycheck.io v3 response.
 *
 * proxycheck keys its response object by *its own canonicalized* form of the
 * address, which can differ from the raw input string for non-canonical IPv6
 * (e.g. uppercase hextets, compressed/expanded variants, or IPv4-mapped forms
 * like "::ffff:1.2.3.4"). We first try the exact input key, then fall back to
 * the single per-IP entry in the response (the response only ever contains one
 * looked-up address alongside top-level metadata such as `status`).
 */
function lookupEntry(data: unknown, ip: string): unknown {
  if (!isRecord(data)) return undefined;

  const direct = data[ip];

  if (direct && typeof direct === "object") return direct;

  // Fallback: scan for the lone per-IP entry (an object carrying a `location`)
  // when the canonical key differs from the raw input.
  for (const value of Object.values(data)) {
    if (value && typeof value === "object" && "location" in value) {
      return value;
    }
  }
  return undefined;
}

/**
 * Get country name from an IP address via proxycheck.io v3 API.
 *
 * Returns null on any failure (timeout, invalid IP, missing API key, etc.)
 * so callers can safely ignore geolocation failures.
 */
export async function getCountryFromIP(ip: string): Promise<string | null> {
  if (!PROXYCHECK_API_KEY) return null;
  if (!ip || ip === "unknown" || ip === "127.0.0.1" || ip === "::1") return null;
  if (ip.includes("%") || isIP(ip) === 0) return null;

  try {
    const res = await fetch(
      `https://proxycheck.io/v3/${encodeURIComponent(ip)}?key=${PROXYCHECK_API_KEY}`,
      { signal: AbortSignal.timeout(TIMEOUT_MS) }
    );
    if (!res.ok) return null;

    const data = await res.json();

    return countryNameOf(lookupEntry(data, ip));
  } catch {
    return null;
  }
}
