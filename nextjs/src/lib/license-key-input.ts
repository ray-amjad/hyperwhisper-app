/**
 * Whether a submitted licence key can be looked up at all.
 *
 * Postgres refuses a NUL byte (U+0000) in a text parameter ("invalid byte
 * sequence for encoding UTF8: 0x00", SQLSTATE 22021), so a key containing one
 * makes `findAccountByKey` throw, and every caller's catch-all reported that as
 * a server fault: a 500 the client retries and the macOS app counts as a
 * `lookup_failed` incident (#2079). No real key contains NUL, so such a key is
 * a malformed request, refused before it reaches the database.
 *
 * Only NUL is refused here. A real database fault must still be a 500; do not
 * widen this into catching driver errors and calling them bad keys.
 */
export function isLookupableLicenseKey(key: string): boolean {
  return !key.includes("\0");
}
