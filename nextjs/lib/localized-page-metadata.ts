import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";

import { localeOpenGraph, SITE_URL } from "@/lib/site-metadata";
import {
  buildAlternateLanguageMap,
  defaultLocale,
  isSupportedLocale,
} from "@/src/i18n/locales";

/**
 * The per-path metadata of a page that renders in every locale: its canonical,
 * the hreflang map with x-default, and `og:url` (#843).
 *
 * The [locale] layout used to build these from the proxy's `x-pathname`
 * header, and that `headers()` call made every route dynamic. A page knows its
 * own path statically, so it states them here instead.
 *
 * Next merges metadata SHALLOWLY per top-level key, so `openGraph` carries the
 * layout's whole object plus the `url`, or the page would drop the og images.
 *
 * `path` is the route below the locale: "" for the home page, else "/download".
 */
export async function localizedPageMetadata(locale: string, path: string) {
  // Same guard as the [locale] layout: no canonical built from a non-locale (#1853).
  if (!isSupportedLocale(locale)) notFound();

  const t = await getTranslations({ locale, namespace: "metadata" });
  const url = `${SITE_URL}/${locale}${path}`;

  return {
    alternates: {
      canonical: url,
      languages: {
        ...buildAlternateLanguageMap(SITE_URL, path),
        "x-default": `${SITE_URL}/${defaultLocale}${path}`,
      },
    },
    openGraph: {
      ...localeOpenGraph(locale, t("title"), t("description")),
      url,
    },
  };
}
