// @ts-check

import { localeCodes } from "../src/i18n/locale-codes.mjs";

/**
 * Permanent redirects for 3 URL families that blog posts link to and that do
 * not exist (#1377). The posts come from Outrank through the add-blog-post
 * webhook, and its AI writer guessed these paths, so they are mapped here
 * rather than in the stored post HTML: a re-published or future post would
 * guess the same URLs again.
 *
 * `:locale` is restricted to the site's locale codes, so `/docs` itself and
 * every other top-level path never match (no redirect loop, no catch-all).
 */

/** Escapes regex metacharacters; the codes today are letters and `-` only. */
const escapeRegex = (/** @type {string} */ value) =>
  value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** `:locale(en|ja|…|hi)`, built from the one locale list. */
export const localeParam = `:locale(${localeCodes.map(escapeRegex).join("|")})`;

/** The `redirects()` entries for next.config.mjs. */
export const blogLinkRedirects = [
  // The privacy policy page is /legal/privacy-policy.
  {
    source: `/${localeParam}/legal/privacy`,
    destination: "/:locale/legal/privacy-policy",
    permanent: true,
  },
  // The docs are the Mintlify rewrite at bare /docs (vercel.json), which has
  // no locale prefix. `:path*` also matches zero segments, so /en/docs lands
  // on /docs and /en/docs/x on /docs/x.
  {
    source: `/${localeParam}/docs/:path*`,
    destination: "/docs/:path*",
    permanent: true,
  },
  // Pricing is the #cloud section of the home page; the footer's "Pricing"
  // link already goes there.
  {
    source: `/${localeParam}/pricing`,
    destination: "/:locale#cloud",
    permanent: true,
  },
];
