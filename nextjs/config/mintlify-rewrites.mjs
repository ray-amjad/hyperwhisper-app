// @ts-check

/**
 * The Mintlify docs, served under the site's own domain (moved here from
 * vercel.json when the site left Vercel for Cloudflare Workers).
 *
 * `beforeFiles`, so they run before the filesystem and the dynamic
 * `app/[locale]` route: otherwise `/docs` could match locale="docs". Next's
 * middleware (proxy.ts) runs BEFORE these rewrites, so every source here must
 * also be excluded by the proxy matcher, or next-intl would redirect it to
 * `/en/...` first. OpenNext proxies an external rewrite with fetch.
 *
 * `/docs.rsc` dates from Vercel, which renamed an RSC request to `<path>.rsc`
 * before its rewrites ran (#1233). Next on Workers does not, but the rule is
 * harmless and keeps any cached client prefetch working.
 */
export const MINTLIFY_ORIGIN = "https://rayamjadltd.mintlify.dev";

export const mintlifyRewrites = [
  {
    source: "/_mintlify/:path*",
    destination: `${MINTLIFY_ORIGIN}/_mintlify/:path*`,
  },
  {
    source: "/api/request",
    destination: `${MINTLIFY_ORIGIN}/_mintlify/api/request`,
  },
  { source: "/docs", destination: `${MINTLIFY_ORIGIN}/docs` },
  { source: "/docs.rsc", destination: `${MINTLIFY_ORIGIN}/docs.rsc` },
  { source: "/docs/:path*", destination: `${MINTLIFY_ORIGIN}/docs/:path*` },
  {
    source: "/mintlify-assets/:path+",
    destination: `${MINTLIFY_ORIGIN}/mintlify-assets/:path+`,
  },
];
