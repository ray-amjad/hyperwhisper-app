import { isIP } from "node:net";

function firstForwardedIP(value: string | null): string | undefined {
  return value?.split(",")[0]?.trim();
}

function isPlainIP(candidate: string): boolean {
  return !candidate.includes("%") && isIP(candidate) !== 0;
}

/**
 * Extract a well-formed client IP from tRPC context headers.
 */
export function getClientIPFromHeaders(headers: Headers): string {
  const candidates = [
    firstForwardedIP(headers.get("x-vercel-forwarded-for")),
    headers.get("cf-connecting-ip")?.trim(),
    firstForwardedIP(headers.get("x-forwarded-for")),
    headers.get("x-real-ip")?.trim(),
  ];

  for (const candidate of candidates) {
    if (candidate && isPlainIP(candidate)) return candidate;
  }

  return "unknown";
}
