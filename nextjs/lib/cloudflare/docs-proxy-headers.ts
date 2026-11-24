/**
 * Credential stripping for the Mintlify docs proxy (#2636).
 *
 * The Mintlify rewrites (config/mintlify-rewrites.mjs) proxy `/docs` and its
 * asset paths to Mintlify with the client's headers. The site's session
 * cookie is host-only with path `/`, so a signed-in browser sends it on every
 * docs request, and the proxy would hand that credential to a third party.
 * In the other direction, a `Set-Cookie` from Mintlify would land on our own
 * domain.
 *
 * On those paths only, the Worker drops `Cookie` and `Authorization` before
 * the request reaches the Next handler, and drops `Set-Cookie` from the
 * response. Routing and caching are untouched.
 */
import { mintlifyRewrites } from "../../config/mintlify-rewrites.mjs";

type Matcher = (pathname: string) => boolean;

/** A matcher for one rewrite `source` (`/x`, `/x/:path*`, `/x/:path+`). */
function sourceMatcher(source: string): Matcher {
  const param = source.match(/^(.*)\/:[^/*+]+([*+]?)$/);

  if (!param) return (pathname) => pathname === source;

  const [, prefix, modifier] = param;

  // `:path*` also matches the bare prefix; `:path` and `:path+` need a segment.
  if (modifier === "*") {
    return (pathname) =>
      pathname === prefix || pathname.startsWith(`${prefix}/`);
  }

  return (pathname) =>
    pathname.startsWith(`${prefix}/`) && pathname.length > prefix.length + 1;
}

const matchers = mintlifyRewrites.map((rewrite) =>
  sourceMatcher(rewrite.source.toLowerCase()),
);

/**
 * Whether `pathname` is proxied to Mintlify. Case-insensitive and
 * percent-decoded, so a path that only differs in case or encoding is
 * stripped too: stripping a non-docs request that never reaches Mintlify
 * costs nothing, missing a docs one would leak the session.
 */
export function isDocsProxyPath(pathname: string): boolean {
  // The encoded and the decoded form both count: `/%64ocs` must not slip by.
  const paths = [pathname.toLowerCase()];

  try {
    paths.push(decodeURIComponent(pathname).toLowerCase());
  } catch {
    // Not valid UTF-8: worker.ts answers 400 before this runs.
  }

  return paths.some((path) => matchers.some((matches) => matches(path)));
}

/** Request headers never forwarded to Mintlify. */
export const STRIPPED_REQUEST_HEADERS = ["cookie", "authorization"] as const;

/** `request` without `Cookie` and `Authorization`, or itself when it has neither. */
export function withoutRequestCredentials(request: Request): Request {
  if (!STRIPPED_REQUEST_HEADERS.some((name) => request.headers.has(name))) {
    return request;
  }

  const headers = new Headers(request.headers);

  for (const name of STRIPPED_REQUEST_HEADERS) headers.delete(name);

  return new Request(request, { headers });
}

/** `response` without `Set-Cookie`, or itself when it has none. */
export function withoutSetCookie(response: Response): Response {
  if (!response.headers.has("set-cookie")) return response;

  const headers = new Headers(response.headers);

  headers.delete("set-cookie");

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}

/**
 * Sends `request` to `next` (the Next handler, which runs the rewrite)
 * without credentials, and returns its response without `Set-Cookie`.
 */
export async function fetchDocsProxy(
  request: Request,
  next: (request: Request) => Promise<Response>,
): Promise<Response> {
  return withoutSetCookie(await next(withoutRequestCredentials(request)));
}
