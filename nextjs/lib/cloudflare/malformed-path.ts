/**
 * Paths whose percent-escapes do not decode to UTF-8.
 *
 * Cloudflare's edge already answers 400 for a bad hex escape (`/de/%ZZ`), but
 * passes a well-formed escape of invalid UTF-8 (`/en/blog/%E0%A4%A`) to the
 * Worker. Next then throws `failed to decode param` while matching a dynamic
 * route and OpenNext turns that into a 500, on pages and API routes alike.
 * worker.ts answers 400 first, as the edge does for the other kind.
 */

/** Whether `pathname` holds a percent-escape that is not valid UTF-8. */
export function isMalformedPath(pathname: string): boolean {
  try {
    decodeURIComponent(pathname);

    return false;
  } catch {
    return true;
  }
}

/** The 400 the Worker sends for a malformed path. */
export function malformedPathResponse(): Response {
  return new Response("Bad Request", {
    status: 400,
    headers: { "content-type": "text/plain; charset=utf-8" },
  });
}
