import { MetadataRoute } from "next";
import { SITE_URL } from "@/lib/site-metadata";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: "*",
      allow: "/",
    },
    // The docs pages are in /sitemap.xml. /docs/sitemap.xml (app/docs/sitemap.ts)
    // lists them again at the URL the Mintlify docs sitemap had, which has been
    // named here since #1884, so a search console that holds it keeps working.
    sitemap: [`${SITE_URL}/sitemap.xml`, `${SITE_URL}/docs/sitemap.xml`],
  };
}
