// THE CHARGE BODY
//
// The JSON `POST /api/license/credits` carries: the key, the amount, and a
// metadata object the licence API only logs. The site refuses a body over
// 16 KB, and a refused charge is a transcription nobody paid for. So the body
// is built here and bounded in UTF-8 bytes, whatever the metadata holds:
//
//   1. each string value is cut to MAX_METADATA_STRING_BYTES of JSON
//      (escapes counted, never splitting a character);
//   2. a value that is not a string, number, boolean or null is dropped;
//   3. if the body is still over CHARGE_BODY_MAX_BYTES, metadata entries are
//      dropped from the end until it fits.
//
// `license_key` and `amount` are never touched: what is charged, and to whom,
// cannot change. The key has passed auth before any charge, so it is a real
// key of a few dozen bytes; only an absurd key could push the body past the
// bound on its own, and then the body is sent as is.
//
// When anything was cut or dropped the metadata says so (`metadata_truncated`).

/** Well under the licence API's 16 KB limit (`MAX_BODY_BYTES` in the site's route). */
export const CHARGE_BODY_MAX_BYTES = 4 * 1024;

/** Room for any real label (a UUID is 36, a provider or model id under 64). */
export const MAX_METADATA_STRING_BYTES = 128;

/** The most bytes one UTF-16 code unit can take as JSON: a `\uXXXX` escape. */
const MAX_JSON_BYTES_PER_CODE_UNIT = 6;

const encoder = new TextEncoder();

export function utf8ByteLength(text: string): number {
  return encoder.encode(text).length;
}

/** Bytes `text` takes inside a JSON string literal, without the quotes. */
function jsonStringBytes(text: string): number {
  return utf8ByteLength(JSON.stringify(text)) - 2;
}

/**
 * The longest prefix of `value` whose JSON string form (quotes excluded) fits
 * in `maxBytes`, cut on a character boundary. Walks at most `maxBytes`
 * characters, so a huge value costs no more than a short one.
 */
export function truncateForJson(value: string, maxBytes: number): string {
  if (value.length * MAX_JSON_BYTES_PER_CODE_UNIT <= maxBytes) return value;
  let used = 0;
  let end = 0;
  for (const char of value) {
    const cost = jsonStringBytes(char);
    if (used + cost > maxBytes) break;
    used += cost;
    end += char.length;
  }
  return value.slice(0, end);
}

export interface ChargeBody {
  /** The JSON to send. */
  readonly body: string;
  /** Some metadata was cut or dropped to keep the body under the bound. */
  readonly truncated: boolean;
}

export function buildChargeBody(
  licenseKey: string,
  amount: number,
  metadata: Record<string, unknown>,
): ChargeBody {
  const entries: Array<[string, string | number | boolean | null]> = [];
  let truncated = false;
  for (const [key, value] of Object.entries(metadata)) {
    if (value === undefined) continue;
    if (typeof value === 'string') {
      const cut = truncateForJson(value, MAX_METADATA_STRING_BYTES);
      if (cut !== value) truncated = true;
      entries.push([key, cut]);
    } else if (typeof value === 'number' || typeof value === 'boolean' || value === null) {
      entries.push([key, value]);
    } else {
      truncated = true;
    }
  }

  for (;;) {
    const sent: Record<string, unknown> = Object.fromEntries(entries);
    if (truncated) sent.metadata_truncated = true;
    const body = JSON.stringify({ license_key: licenseKey, amount, metadata: sent });
    if (utf8ByteLength(body) <= CHARGE_BODY_MAX_BYTES || entries.length === 0) {
      return { body, truncated };
    }
    entries.pop();
    truncated = true;
  }
}
