import type { MetadataRoute } from "next";

import { docsSitemapEntries } from "@/lib/docs/sitemap";

// /docs/sitemap.xml: the URL Mintlify served the docs sitemap at, which
// robots.txt has named since #1884 and search consoles may hold. The same
// entries are in the main /sitemap.xml too.
export const dynamic = "force-static";

export default function sitemap(): MetadataRoute.Sitemap {
  return docsSitemapEntries();
}
