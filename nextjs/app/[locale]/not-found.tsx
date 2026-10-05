import type { Metadata } from "next";

import RootNotFound from "../not-found";

// A notFound() thrown under [locale] (blog, blog/[slug], latency,
// choosing-a-model) still resolves the [locale] layout's metadata, whose
// robots is "index, follow". Next then also injects its own
// <meta name="robots" content="noindex">, so the 404 carried both (#1133).
// This boundary's robots replaces the layout's, leaving only noindex.
// It renders inside the [locale] layout, so these 404s get the site chrome.
// Unmatched URLs (/en/zzz) still use app/not-found.tsx directly.
export const metadata: Metadata = {
  robots: { index: false },
};

export default RootNotFound;
