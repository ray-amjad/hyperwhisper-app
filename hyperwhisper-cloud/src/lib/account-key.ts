// The account key: its one spelling and the request fields it arrives under.
//
// A leaf module on purpose: `internal-license-cache.ts` reads the key too, and
// it must not import `middleware/auth` (and through it `lib/redis`, which many
// suites replace with `mock.module`).

/**
 * The one spelling of an account key this service keys anything on (#2461).
 * The site trims the key before its lookup, so ` KEY` and `KEY` are one
 * account; the licence cache, the credit hold and `auth.identifier` must all
 * use the trimmed form, or a padded variant is a second hold budget for the
 * same balance. Case is kept: the site matches the key case-sensitively.
 * Whitespace only, or a value that is not a string, is no key at all.
 */
export function canonicalAccountKey(raw: unknown): string | undefined {
  // `/post-process` reads the key from an untyped JSON body, so a number or an
  // object can arrive here: that is no key, never a throw.
  if (typeof raw !== 'string') return undefined;
  const trimmed = raw.trim();
  return trimmed ? trimmed : undefined;
}

/**
 * `account_key` is the canonical name; `license_key` is the legacy alias that
 * installed native apps still send. Both carry the same key string.
 */
export const ACCOUNT_KEY_FIELDS = ['account_key', 'license_key'] as const;

/**
 * The key from the first of `fields` that holds one, in canonical form.
 * `read` returns a field's value: a query param (`rawQuery`) or a body field.
 */
export function readAccountKey(
  read: (field: string) => unknown,
  fields: readonly string[] = ACCOUNT_KEY_FIELDS,
): string | undefined {
  for (const field of fields) {
    const key = canonicalAccountKey(read(field));
    if (key) return key;
  }
  return undefined;
}
