/**
 * Shared Open Graph pieces for the root layout and for the English-only
 * `force-static` pages (#1312).
 *
 * Next.js merges metadata SHALLOWLY per top-level key: a page that returns
 * `openGraph: { url }` replaces the layout's whole `openGraph` object, so the
 * page must send the image list, site name, type and locale itself. Both the
 * layout and those pages read them from here, so the list lives in one place.
 */

export const SITE_URL = "https://hyperwhisper.com";
export const SITE_NAME = "HyperWhisper";

export const OG_IMAGES = [1024, 512, 256, 128].map((size) => ({
  url: `${SITE_URL}/icon/${size}.png`,
  width: size,
  height: size,
  alt: "HyperWhisper Logo",
}));

type EnglishStaticPageMetadataInput = {
  /** Route path below the locale, starting with "/", e.g. "/blog". */
  path: string;
  title: string;
  description: string;
};

/**
 * Metadata for an English-only `force-static` page. Under `force-static` the
 * layout's `headers()` is empty, so its canonical and `og:url` fall back to
 * the home page; the page has to state its own. No hreflang `languages`: these
 * pages 404 in every other locale.
 */
export function englishStaticPageMetadata({
  path,
  title,
  description,
}: EnglishStaticPageMetadataInput) {
  const url = `${SITE_URL}/en${path}`;

  return {
    title,
    description,
    alternates: {
      canonical: url,
    },
    openGraph: {
      type: "website" as const,
      locale: "en_US",
      url,
      // The layout's "%s - HyperWhisper" template does not reach
      // openGraph.title (Next only templates it from a parent openGraph.title
      // template), so spell out the same text the <title> shows.
      title: `${title} - ${SITE_NAME}`,
      description,
      siteName: SITE_NAME,
      images: OG_IMAGES,
    },
  };
}
