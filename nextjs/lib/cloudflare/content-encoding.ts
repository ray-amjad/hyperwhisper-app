/**
 * Content negotiation for responses the Worker proxies from another origin.
 *
 * The Mintlify rewrites (config/mintlify-rewrites.mjs) fetch the docs from
 * Mintlify and pass its response headers through, `Content-Encoding` included.
 * The Workers runtime decodes a fetched body as it reads it and encodes the
 * returned body to match the `Content-Encoding` header, so a `br` header goes
 * back out as a `br` body, whatever the client asked for. A client that sent
 * no `Accept-Encoding` (curl, most crawlers fetching /docs/llms.txt) then gets
 * bytes it cannot read. Dropping the header makes the runtime send the decoded
 * body as is.
 *
 * The Worker cannot read the client's own header: Cloudflare rewrites a
 * request's `Accept-Encoding` to `br, gzip` before the Worker sees it and
 * keeps the original in `request.cf.clientAcceptEncoding` (wrangler dev does
 * the same, with "" for a missing header).
 */

/** Whether `coding` is acceptable under this `Accept-Encoding` value. */
export function acceptsEncoding(
  acceptEncoding: string | null,
  coding: string,
): boolean {
  const wanted = coding.trim().toLowerCase();

  if (wanted === "" || wanted === "identity") return true;
  // RFC 9110 lets a server pick any coding when the header is absent; a
  // client that sends none is almost always one that cannot decode, so we
  // send identity.
  if (!acceptEncoding) return false;

  let wildcard: boolean | undefined;

  for (const part of acceptEncoding.split(",")) {
    const [rawName, ...params] = part.split(";");
    const name = rawName.trim().toLowerCase();

    if (!name) continue;
    let q = 1;

    for (const param of params) {
      const [key, value] = param.split("=");

      if (key.trim().toLowerCase() === "q") q = Number(value);
    }
    const accepted = Number.isFinite(q) && q > 0;

    if (name === wanted) return accepted;
    if (name === "*") wildcard = accepted;
  }

  return wildcard ?? false;
}

/** The Accept-Encoding the client sent, before Cloudflare rewrote it. */
export function clientAcceptEncoding(request: Request): string | null {
  const cf = (request as Request & { cf?: { clientAcceptEncoding?: unknown } })
    .cf;

  if (typeof cf?.clientAcceptEncoding === "string") {
    return cf.clientAcceptEncoding;
  }

  return request.headers.get("accept-encoding");
}

/**
 * Returns `response` unchanged when the client accepts its encoding (or it
 * has none), otherwise a copy without `Content-Encoding`, so the runtime
 * sends it uncompressed. `Content-Length` goes too: it counted the encoded
 * bytes.
 */
export function withAcceptedEncoding(
  request: Request,
  response: Response,
): Response {
  const contentEncoding = response.headers.get("content-encoding");

  if (!contentEncoding || !response.body) return response;

  const accept = clientAcceptEncoding(request);
  const codings = contentEncoding.split(",");

  if (codings.every((coding) => acceptsEncoding(accept, coding))) {
    return response;
  }

  const headers = new Headers(response.headers);

  headers.delete("content-encoding");
  headers.delete("content-length");

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}
