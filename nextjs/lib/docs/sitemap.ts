import type { MetadataRoute } from "next";

import { DOCS_ENTRIES } from "./nav";

import { SITE_URL } from "@/lib/site-metadata";

/** One entry per docs page and endpoint page, English only, no alternates. */
export function docsSitemapEntries(): MetadataRoute.Sitemap {
  return DOCS_ENTRIES.map((entry) => ({
    url: `${SITE_URL}${entry.href}`,
    changeFrequency: "weekly",
    priority: entry.slug === "index" ? 0.8 : 0.6,
  }));
}
