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

// Redacts every string in `value`, at any depth, in place. PostHog nests the
// page URL inside objects and arrays: `$web_vitals_*_event.navigationURL` and
// its `$current_url`, and the session recording's network entries in
// `$snapshot_data`, which are named with the URL the page was first loaded
// with, key included (#1920). `seen` stops a cycle from recursing forever.
function redactNested(value: unknown, seen: WeakSet<object>): void {
  if (typeof value !== "object" || value === null || seen.has(value)) return;
  seen.add(value);

  const entries: [string | number, unknown][] = Array.isArray(value)
    ? value.map((item, index) => [index, item])
    : Object.entries(value);

  for (const [name, item] of entries) {
    if (typeof item === "string") {
      if (/HW-/i.test(item)) {
        (value as Record<string | number, unknown>)[name] =
          redactAccountKeyFromUrl(item);
      }
    } else {
      redactNested(item, seen);
    }
  }
}

interface RedactableEvent {
  properties?: Properties;
  $set?: Properties;
  $set_once?: Properties;
}

/**
 * PostHog `before_send` hook: redacts an Account Key from every string in the
 * event's properties and its `$set` / `$set_once` person properties, nested
 * objects and arrays included. posthog-js sends session recording snapshots
 * (`$snapshot`) through `capture`, so this covers them too. Never drops an
 * event.
 */
export function redactAccountKeyFromEvent<T extends RedactableEvent | null>(
  event: T,
): T {
  if (!event) return event;

  const seen = new WeakSet<object>();

  redactNested(event.properties, seen);
  redactNested(event.$set, seen);
  redactNested(event.$set_once, seen);

  return event;
}

// The query parameters that carry the Account Key into a page: `licenseKey`
// (`/user/sign-in`) and `license_key` (`/credits`, `/checkout`). Any other
// parameter counts too when its decoded value holds a key, like the legacy
// `/credits?id=HW-…` or a key nested in `returnTo`. Only the full key shape
// counts there, so `utm_campaign=hw-launch` stays.
const ACCOUNT_KEY_PARAMS = ["licenseKey", "license_key"];
const HOLDS_ACCOUNT_KEY = new RegExp(ACCOUNT_KEY_PATTERN.source, "i");

function accountKeyParamNames(searchParams: URLSearchParams): string[] {
  return Array.from(new Set(searchParams.keys())).filter(
    (name) =>
      ACCOUNT_KEY_PARAMS.includes(name) ||
      searchParams.getAll(name).some((value) => HOLDS_ACCOUNT_KEY.test(value)),
  );
}

/** Whether `url` carries an Account Key query parameter. */
export function hasAccountKeyParam(url: string): boolean {
  return accountKeyParamNames(new URL(url).searchParams).length > 0;
}

/**
 * The same URL with its Account Key query parameters removed (`licenseKey`,
 * `license_key`, and any parameter whose value holds a key), for
 * `history.replaceState`, so the key leaves the address bar and the browser
 * history. Path, other parameters and fragment are kept.
 */
export function withoutAccountKeyParams(url: string): string {
  const parsed = new URL(url);

  for (const name of accountKeyParamNames(parsed.searchParams)) {
    parsed.searchParams.delete(name);
  }

  return parsed.pathname + parsed.search + parsed.hash;
}
