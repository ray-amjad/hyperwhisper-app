/**
 * Credential headers kept away from the Mintlify docs proxy (#2636).
 *
 * The docs are next.config rewrites to Mintlify (config/mintlify-rewrites.mjs)
 * on the site's own origin. OpenNext proxies an external rewrite with fetch,
 * forwarding every request header except `cf-connecting-ip` and copying every
 * response header back. Without this filter the browser's host-only, Path=/
 * Better Auth session cookie (90 days) goes to Mintlify on every docs request,
 * and Mintlify can set or clear cookies on www.hyperwhisper.com.
 *
 * worker.ts runs these before and after the OpenNext handler. Every other
 * path passes through untouched (the same Request and Response objects).
 *
 * The match runs on the raw `url.pathname`, as OpenNext's rewrite matcher
 * does. It is case-insensitive on purpose: OpenNext compiles the rewrite regex
 * without flags today, but Next itself matches rewrite sources
 * case-insensitively, so a looser match can only strip more, never leak.
 * tests/docs-proxy-headers.test.ts checks that every path a Mintlify rewrite
 * source matches is matched here.
 *
 * A Pages Router data URL is matched as the page it names: OpenNext's
 * `fixDataPage` turns `/_next/data/<buildId>/docs.json` into `/docs` before
 * the rewrites run, so it reaches Mintlify too. The buildId is not checked
 * here; a wrong one gets OpenNext's 404 and never leaves the Worker.
 */
const NEXT_DATA_PATH = /^\/_next\/data\/[^/]+(\/.*)\.json$/;

const DOCS_PROXY_PATH =
  /^\/(?:docs(?:\.rsc)?|_mintlify|mintlify-assets)(?:\/|$)|^\/api\/request\/?$/i;

/** Request headers that carry the user's credentials for this site. */
const STRIPPED_REQUEST_HEADERS = [
  "cookie",
  "authorization",
  "proxy-authorization",
] as const;

/**
 * Response headers that would let the docs origin change state on the whole
 * site: set or wipe its cookies, or widen a service worker's scope past /docs.
 */
const STRIPPED_RESPONSE_HEADERS = [
  "set-cookie",
  "clear-site-data",
  "service-worker-allowed",
] as const;

/** Whether `pathname` is sent to Mintlify by a docs rewrite. */
export function isDocsProxyPath(pathname: string): boolean {
  return DOCS_PROXY_PATH.test(routedPath(pathname));
}

/** The path OpenNext routes `pathname` as (a data URL as its page). */
function routedPath(pathname: string): string {
  const page = NEXT_DATA_PATH.exec(pathname)?.[1];

  if (page === undefined) return pathname;

  return page === "/index" ? "/" : page;
}

/**
 * `request` without its credential headers when it is bound for the docs
 * proxy; otherwise `request` itself.
 */
export function withoutDocsCredentials(request: Request): Request {
  if (!isDocsProxyPath(new URL(request.url).pathname)) return request;
  if (!STRIPPED_REQUEST_HEADERS.some((name) => request.headers.has(name))) {
    return request;
  }

  const headers = new Headers(request.headers);

  for (const name of STRIPPED_REQUEST_HEADERS) headers.delete(name);

  // A Request input keeps its method, body, redirect mode and (on Workers)
  // `cf`; only the headers are replaced.
  return new Request(request, { headers });
}

/**
 * `response` without the headers that act on the whole origin when it
 * answers a docs request; otherwise `response` itself.
 */
export function withoutDocsCookies(
  request: Request,
  response: Response,
): Response {
  if (!isDocsProxyPath(new URL(request.url).pathname)) return response;
  if (!STRIPPED_RESPONSE_HEADERS.some((name) => response.headers.has(name))) {
    return response;
  }

  const headers = new Headers(response.headers);

  for (const name of STRIPPED_RESPONSE_HEADERS) headers.delete(name);

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}
