/**
 * Shared Open Graph pieces for the root layout and for the English-only
 * `force-static` pages (#1312).
 *
 * Next.js merges metadata SHALLOWLY per top-level key: a page that returns
 * `openGraph: { url }` replaces the layout's whole `openGraph` object, so the
 * page must send the image list, site name, type and locale itself. Both the
 * layout and those pages read them from here, so the list lives in one place.
 */

import { toOpenGraphLocale } from "@/src/i18n/locales";

export const SITE_URL = "https://hyperwhisper.com";
export const SITE_NAME = "HyperWhisper";

export const OG_IMAGES = [1024, 512, 256, 128].map((size) => ({
  url: `${SITE_URL}/icon/${size}.png`,
  width: size,
  height: size,
  alt: "HyperWhisper Logo",
}));

export const TWITTER_CARD = "summary" as const;
export const TWITTER_CREATOR = "@theramjad";
export const TWITTER_IMAGES = [`${SITE_URL}/icon/256.png`];

/**
 * The [locale] layout's Open Graph object, without a `url`. A layout cannot
 * know the child path without reading the request (#843), so each page adds
 * its own `url` through `localizedPageMetadata`, which spreads this object.
 */
export function localeOpenGraph(
  locale: string,
  title: string,
  description: string,
) {
  return {
    type: "website" as const,
    locale: toOpenGraphLocale(locale),
    title,
    description,
    siteName: SITE_NAME,
    images: OG_IMAGES,
  };
}

type EnglishStaticPageMetadataInput = {
  /** Route path below the locale, starting with "/", e.g. "/blog". */
  path: string;
  title: string;
  description: string;
};

/**
 * Metadata for an English-only `force-static` page. The layout sets no
 * canonical or `og:url` (#843), so the page has to state its own. No hreflang
 * `languages`: these pages 404 in every other locale.
 */
export function englishStaticPageMetadata({
  path,
  title,
  description,
}: EnglishStaticPageMetadataInput) {
  const url = `${SITE_URL}/en${path}`;
  // The layout's "%s - HyperWhisper" template does not reach openGraph.title
  // or twitter.title (Next only templates them from a parent openGraph/twitter
  // title template), so spell out the same text the <title> shows.
  const fullTitle = `${title} - ${SITE_NAME}`;

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
      title: fullTitle,
      description,
      siteName: SITE_NAME,
      images: OG_IMAGES,
    },
    // The layout sets twitter.title, so Next does not fill it from og:title;
    // the page must send the whole twitter object (shallow merge again).
    twitter: {
      card: TWITTER_CARD,
      title: fullTitle,
      description,
      creator: TWITTER_CREATOR,
      images: TWITTER_IMAGES,
    },
  };
}
