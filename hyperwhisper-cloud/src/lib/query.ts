// Raw query-string parsing that preserves literal `+`.
//
// Hono's `c.req.query()` decodes query values with an extra `+` → space step
// (HTML-form semantics). RFC 3986 query strings have no such rule, and neither
// platform client form-encodes: a vocabulary term like `C++` sent as
// `initial_prompt=C%2B%2B` (or even a raw `C++`) must arrive as `C++`, not
// `C  `. This helper re-parses the query string manually and decodes with
// `decodeURIComponent`, which leaves `+` alone.
//
// Scope: every route reads its query params through this helper, never
// `c.req.query()` or `URLSearchParams.get`, which both apply the `+` → space
// step (live-dictation `vocabulary` carries `C++` as much as `initial_prompt`).

/**
 * Read a single query parameter from `url`, percent-decoding the value while
 * preserving literal `+`. Returns the FIRST occurrence, `undefined` when the
 * parameter is absent or its value is empty (preserving the callers'
 * `|| undefined` semantics), and the raw (undecoded) value when the value
 * contains a malformed percent-escape.
 */
export function rawQuery(url: string, name: string): string | undefined {
  const queryStart = url.indexOf('?');
  if (queryStart === -1) return undefined;

  // Drop a fragment if present (defensive; clients don't send one).
  const hashStart = url.indexOf('#', queryStart);
  const query = url.slice(queryStart + 1, hashStart === -1 ? undefined : hashStart);

  for (const pair of query.split('&')) {
    const eq = pair.indexOf('=');
    const rawKey = eq === -1 ? pair : pair.slice(0, eq);

    let key: string;
    try {
      key = decodeURIComponent(rawKey);
    } catch {
      key = rawKey;
    }
    if (key !== name) continue;

    const rawValue = eq === -1 ? '' : pair.slice(eq + 1);
    let value: string;
    try {
      value = decodeURIComponent(rawValue);
    } catch {
      // Malformed escape (e.g. a stray `%`): fall back to the raw value rather
      // than throwing a 500 for one bad byte. The well-formed escapes around it
      // are still decoded, as `URLSearchParams` does, so one stray `%` in a
      // live-dictation `vocabulary` does not send every term undecoded.
      value = decodeLikeSearchParams(rawValue);
    }
    return value === '' ? undefined : value;
  }

  return undefined;
}

/**
 * Decode the way `URLSearchParams` does (a stray `%` stays, an invalid UTF-8
 * escape becomes U+FFFD) except that a literal `+` stays `+`.
 */
function decodeLikeSearchParams(raw: string): string {
  return new URLSearchParams(`v=${raw.replace(/\+/g, '%2B')}`).get('v') ?? raw;
}
