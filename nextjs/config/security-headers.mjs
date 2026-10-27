// @ts-check

/**
 * Response headers sent on every route of the site (#842).
 *
 * Without them, any third-party page could put the sign-in page or the
 * credit purchase page in an iframe and overlay its own controls
 * (clickjacking). `frame-ancestors 'none'` and `X-Frame-Options: DENY` refuse
 * every framer, our own origin included: nothing on the site frames one of
 * its own pages, and the desktop apps open the site in the system browser.
 *
 * Deliberately NOT a full CSP. The AgentStack embed, the Bunny video player,
 * PostHog and Stripe all load third-party scripts, so a `script-src` /
 * `default-src` policy needs its own Report-Only run before it is enforced.
 *
 * These headers DO reach `/docs`, though it and `/mintlify-assets` are Vercel
 * rewrites to Mintlify (vercel.json): production sends them on the rewritten
 * responses, and our `Content-Security-Policy` replaces Mintlify's own
 * (#1343). The docs were checked with them on 2026-10-06: pages render,
 * Ctrl+K search works and the Loom videos play. So a fuller CSP here would
 * apply to the Mintlify docs too, and must be checked there as well.
 */
export const securityHeaders = [
  { key: "Content-Security-Policy", value: "frame-ancestors 'none'" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  {
    key: "Permissions-Policy",
    value: "camera=(), microphone=(), geolocation=()",
  },
];

/** The `headers()` entries for next.config.mjs: every path gets the list. */
export const securityHeaderRoutes = [
  { source: "/:path*", headers: securityHeaders },
];
