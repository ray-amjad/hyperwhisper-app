import { NextResponse } from "next/server";

/**
 * Bounded request-body reads (#2904).
 *
 * `req.text()` and `req.json()` buffer whatever the caller sends, up to the
 * platform's own request limit (100 MB on Cloudflare Free/Pro). On a Worker
 * isolate with 128 MB of memory a few of those in parallel kill the isolate and
 * every request it was serving, so a public route reads its body through
 * `readBoundedText` instead.
 */

/** The reply a route sends for a body over its cap. */
export function payloadTooLarge(): NextResponse {
  return NextResponse.json({ error: "Payload too large" }, { status: 413 });
}

/**
 * True when the request DECLARES a `content-length` over `maxBytes`.
 *
 * Only a plain run of decimal digits counts as a declared length. A missing,
 * empty, negative, fractional or otherwise malformed value is ignored rather
 * than thrown on or rejected: the streaming cap in `readBoundedText` still
 * bounds that body, so ignoring a lie costs nothing.
 */
export function declaresLengthOver(
  req: { headers: Headers },
  maxBytes: number,
): boolean {
  const header = req.headers.get("content-length")?.trim();
  if (!header || !/^\d+$/.test(header)) return false;
  return Number(header) > maxBytes;
}

/**
 * Reads the request body as UTF-8 text, never holding more than `maxBytes`
 * bytes of it.
 *
 * Returns the text, or a 413 reply when the body is over the cap: at once for
 * a declared `content-length` over it (the body is not read), otherwise as soon
 * as the running BYTE count of the stream passes it, after which the stream is
 * cancelled. A body of exactly `maxBytes` bytes is accepted.
 *
 * The text decodes as `req.text()` does (UTF-8, a leading BOM dropped, bad
 * bytes replaced), and a character split across two chunks decodes whole.
 *
 * A body stream that fails part-way rejects, as `req.text()` would, so a
 * route keeps its existing read-error reply.
 */
export async function readBoundedText(
  req: Request,
  maxBytes: number,
): Promise<string | NextResponse> {
  if (declaresLengthOver(req, maxBytes)) return payloadTooLarge();

  const body = req.body;
  if (!body) return "";

  const reader = body.getReader();
  const decoder = new TextDecoder("utf-8");
  const parts: string[] = [];
  let bytes = 0;

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;

    bytes += value.byteLength;
    if (bytes > maxBytes) {
      // Tell the source to stop sending. A cancel that fails changes nothing
      // about the answer, so its error is not allowed to replace the 413.
      await reader.cancel().catch(() => undefined);
      return payloadTooLarge();
    }

    parts.push(decoder.decode(value, { stream: true }));
  }

  parts.push(decoder.decode());
  return parts.join("");
}
