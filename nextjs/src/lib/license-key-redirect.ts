export const DEFAULT_LICENSE_KEY_REDIRECT = "/en/user/dashboard";

/**
 * Returns `returnTo` only when it is a same-origin, path-style redirect target;
 * otherwise returns `fallback`. Rejects protocol-relative ("//evil.com") and
 * backslash ("/\\evil.com") prefixes that satisfy a naive `startsWith("/")`
 * check but resolve to an attacker-controlled origin, preventing open redirects.
 *
 * Tab, CR and LF are removed before the prefix checks. The URL parser strips
 * them, so "/\t/evil.com" passes a `startsWith("//")` guard and only becomes
 * "//evil.com" once the browser resolves it.
 */
export function sanitizeReturnTo(
  returnTo: string | null | undefined,
  fallback: string,
) {
  if (!returnTo) {
    return fallback;
  }

  const candidate = returnTo.replace(/[\t\r\n]/g, "");

  if (
    candidate.startsWith("/") &&
    !candidate.startsWith("//") &&
    !candidate.startsWith("/\\")
  ) {
    return candidate;
  }

  return fallback;
}

export function sanitizeLicenseKeyRedirect(callbackURL: string | undefined) {
  return sanitizeReturnTo(callbackURL, DEFAULT_LICENSE_KEY_REDIRECT);
}
