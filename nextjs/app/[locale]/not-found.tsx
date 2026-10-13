import type { Metadata } from "next";

import { getLocale, getTranslations } from "next-intl/server";

import NotFoundContent, {
  type NotFoundCopy,
} from "@/components/layout/NotFoundContent";
import { isSupportedLocale, locales } from "@/src/i18n/locales";
import { loadMessages } from "@/src/i18n/messages";

// The 404 content for every locale (#815). A notFound() under [locale] renders
// it inside the [locale] layout; an unmatched URL renders it through
// app/not-found.tsx, which wraps it in the same layout.
//
// robots: the [locale] layout's robots is "index, follow", and Next also
// injects its own noindex on a 404, so without this the page carried both
// (#1133). This boundary's robots replaces the layout's, leaving only noindex.
// The pages that throw notFound() also throw it from their generateMetadata,
// or the browser keeps the page's metadata instead of this.
//
// The locale comes from the segment params when Next passes them (a notFound()
// under [locale]): there the request locale is not set yet when metadata
// resolves, and the title came out in English on /de/blog. app/not-found.tsx
// calls this with no params, and the request locale is right there.
export async function generateMetadata(props?: {
  params?: Promise<{ locale?: string }>;
}): Promise<Metadata> {
  const paramLocale = (await props?.params)?.locale;
  const locale =
    paramLocale && isSupportedLocale(paramLocale)
      ? paramLocale
      : await getLocale();
  const t = await getTranslations({ locale, namespace: "notFound" });

  return {
    title: t("title"),
    robots: { index: false },
  };
}

// This component gets no params, and on the force-static English-only pages
// the request locale is the default, not the URL's. So every locale's copy goes
// to the client, which picks the URL's (five short strings per locale).
async function loadNotFoundCopy(): Promise<Record<string, NotFoundCopy>> {
  const entries = await Promise.all(
    locales.map(async (locale) => {
      const messages = await loadMessages(locale);

      return [locale, messages.notFound as NotFoundCopy] as const;
    }),
  );

  return Object.fromEntries(entries);
}

export default async function LocaleNotFound() {
  const [copyByLocale, fallbackLocale] = await Promise.all([
    loadNotFoundCopy(),
    getLocale(),
  ]);

  return (
    <NotFoundContent
      copyByLocale={copyByLocale}
      fallbackLocale={fallbackLocale}
    />
  );
}
