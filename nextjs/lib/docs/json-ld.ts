import { ORGANIZATION_ID, organizationJsonLd } from "@/lib/home-json-ld";
import { SITE_NAME, SITE_URL } from "@/lib/site-metadata";

export const WEBSITE_ID = `${SITE_URL}/#website`;

export type DocsCrumb = { name: string; url: string };

/**
 * A docs page's JSON-LD: the Organization and WebSite (as the Mintlify pages
 * carried), the page itself as a TechArticle, and its breadcrumb trail.
 */
export function docsPageJsonLd({
  title,
  description,
  canonical,
  crumbs,
}: {
  title: string;
  description: string;
  canonical: string;
  crumbs: DocsCrumb[];
}) {
  return {
    "@context": "https://schema.org",
    "@graph": [
      organizationJsonLd(),
      {
        "@type": "WebSite",
        "@id": WEBSITE_ID,
        name: SITE_NAME,
        url: SITE_URL,
        publisher: { "@id": ORGANIZATION_ID },
      },
      {
        "@type": "TechArticle",
        "@id": `${canonical}#article`,
        headline: title,
        description,
        url: canonical,
        mainEntityOfPage: canonical,
        inLanguage: "en",
        isPartOf: { "@id": WEBSITE_ID },
        publisher: { "@id": ORGANIZATION_ID },
      },
      {
        "@type": "BreadcrumbList",
        "@id": `${canonical}#breadcrumb`,
        itemListElement: crumbs.map((item, i) => ({
          "@type": "ListItem",
          position: i + 1,
          name: item.name,
          item: item.url,
        })),
      },
    ],
  };
}
