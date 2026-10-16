/**
 * Keeps the Account Key out of analytics (#1897).
 *
 * The macOS app opens `/credits?license_key=HW-…`, and `/credits` redirects to
 * `/user/sign-in?licenseKey=HW-…`. PostHog records the page URL on every event
 * (`$current_url`, `$referrer`, and the `$initial_*` person properties), so
 * without this the key — a bearer credential that signs into the portal and
 * spends Cloud credits — would be sent to PostHog in full.
 *
 * Pure, with no env or browser import, so it can be tested directly.
 */

export const REDACTED = "[redacted]";

// A query or fragment parameter whose raw value starts with `HW-`, whatever
// the parameter is called (`licenseKey`, `license_key`, `id`, …). The value
// runs to the next `&` or `#`. `encodeURIComponent` leaves `HW-` and the key's
// letters, digits and dashes as they are, so the raw form is the key itself.
const KEY_PARAM_PATTERN = /([?&#][^=&#]*=)HW-[^&#]*/gi;

// A key anywhere else: a path segment, free text, or a parameter nested and
// encoded inside another one (`returnTo=%2F…%3FlicenseKey%3DHW-…`). Keys are
// `HW-` and four groups of four (lib/services/license-key.ts). No `\b` in
// front: in `%3DHW-` the `D` before the key is a word character.
const ACCOUNT_KEY_PATTERN = /HW-[A-Z0-9]{4}(?:-[A-Z0-9]{4}){3}(?![A-Z0-9])/gi;

/**
 * Returns `url` with every Account Key in it replaced by `[redacted]`: a
 * parameter whose value starts with `HW-`, and any `HW-XXXX-XXXX-XXXX-XXXX`
 * elsewhere. Everything else, `returnTo` included, is left byte for byte as it
 * was. Works on any string, a URL or not.
 */
export function redactAccountKeyFromUrl(url: string): string {
  if (!/HW-/i.test(url)) return url;
  return url
    .replace(KEY_PARAM_PATTERN, `$1${REDACTED}`)
    .replace(ACCOUNT_KEY_PATTERN, REDACTED);
}

type Properties = Record<string, unknown>;

function redactProperties(properties: Properties | undefined): void {
  if (!properties) return;
  for (const [name, value] of Object.entries(properties)) {
    if (typeof value === "string" && /HW-/i.test(value)) {
      properties[name] = redactAccountKeyFromUrl(value);
    }
  }
}

interface RedactableEvent {
  properties?: Properties;
  $set?: Properties;
  $set_once?: Properties;
}

/**
 * PostHog `before_send` hook: redacts an Account Key from every string
 * property of the event and of its `$set` / `$set_once` person properties.
 * Never drops an event.
 */
export function redactAccountKeyFromEvent<T extends RedactableEvent | null>(
  event: T,
): T {
  if (!event) return event;
  redactProperties(event.properties);
  redactProperties(event.$set);
  redactProperties(event.$set_once);
  return event;
}

/**
 * The same URL with the `licenseKey` query parameter removed, for
 * `history.replaceState`, so the key leaves the address bar and the browser
 * history. Path, other parameters and fragment are kept.
 */
export function withoutLicenseKeyParam(url: string): string {
  const parsed = new URL(url);
  parsed.searchParams.delete("licenseKey");
  return parsed.pathname + parsed.search + parsed.hash;
}
