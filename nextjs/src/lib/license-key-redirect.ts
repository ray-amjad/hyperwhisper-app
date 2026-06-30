export const DEFAULT_LICENSE_KEY_REDIRECT = "/en/user/dashboard";

/**
 * Returns `returnTo` only when it is a same-origin, path-style redirect target;
 * otherwise returns `fallback`. Rejects protocol-relative ("//evil.com") and
 * backslash ("/\\evil.com") prefixes that satisfy a naive `startsWith("/")`
 * check but resolve to an attacker-controlled origin, preventing open redirects.
 */
export function sanitizeReturnTo(
  returnTo: string | null | undefined,
  fallback: string,
) {
  if (
    returnTo &&
    returnTo.startsWith("/") &&
    !returnTo.startsWith("//") &&
    !returnTo.startsWith("/\\")
  ) {
    return returnTo;
  }

  return fallback;
}

export function sanitizeLicenseKeyRedirect(callbackURL: string | undefined) {
  return sanitizeReturnTo(callbackURL, DEFAULT_LICENSE_KEY_REDIRECT);
}
