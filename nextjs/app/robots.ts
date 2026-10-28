import { MetadataRoute } from "next";
import { SITE_URL } from "@/lib/site-metadata";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: "*",
      allow: "/",
    },
    // Crawlers read robots.txt only at the host root, so the docs sitemap
    // (served by Mintlify through the /docs rewrite in nextjs/vercel.json) has to be
    // named here; its own /docs/robots.txt is never read (#1884).
    sitemap: [`${SITE_URL}/sitemap.xml`, `${SITE_URL}/docs/sitemap.xml`],
  };
}
