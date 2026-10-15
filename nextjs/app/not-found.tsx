import type { Metadata } from "next";

import { getTranslations } from "next-intl/server";

import LocaleLayout from "./[locale]/layout";
import LocaleNotFound from "./[locale]/not-found";

import { defaultLocale } from "@/src/i18n/locales";

// The boundary for a notFound() thrown by the [locale] layout itself: a first
// segment that is not a locale (/favicon.ico, /.env: the proxy skips dotted
// paths, #1853). It wraps the [locale] 404 in that layout so the page gets the
// navbar, footer, dark theme and translations (#815).
//
// An unmatched URL (/de/zzz) renders app/global-not-found.tsx instead, and a
// notFound() thrown under a real locale renders [locale]/not-found.tsx inside
// the real [locale] layout.
//
// The locale is the default, never the request's: Next renders this boundary
// on every page, and getLocale() is a headers() call that made every route
// dynamic (#843). The paths that reach it skip the proxy, so they never carried
// a locale header anyway.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations({
    locale: defaultLocale,
    namespace: "notFound",
  });

  // No [locale] layout above this file, so no "%s - HyperWhisper" template.
  // robots: Vercel only, as in app/global-not-found.tsx (#815).
  return {
    title: `${t("title")} - HyperWhisper`,
    ...(process.env.VERCEL ? { robots: { index: false } } : {}),
  };
}

export default function NotFound() {
  return (
    <LocaleLayout params={Promise.resolve({ locale: defaultLocale })}>
      <LocaleNotFound locale={defaultLocale} />
    </LocaleLayout>
  );
}
