/**
 * What a license route may log when a request body did not parse as JSON (#719).
 *
 * The one place that decides what such a log line may carry, so the three
 * license routes cannot drift apart: the error's name, the content type and
 * the declared length, never the payload. A SyntaxError's message quotes the
 * input around the fault (a short one in full), and on these unauthenticated
 * paths that input is the licence key. Do not add `err`, `err.message` or the
 * body here.
 */
export function unparseableRequestFields(
  req: { headers: Headers },
  err: unknown,
): {
  errorName: string;
  contentType: string | null;
  contentLength: string | null;
} {
  return {
    errorName: err instanceof Error ? err.name : typeof err,
    contentType: req.headers.get("content-type"),
    contentLength: req.headers.get("content-length"),
  };
}
