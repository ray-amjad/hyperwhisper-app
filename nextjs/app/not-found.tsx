import type { Metadata } from "next";

import { getTranslations } from "next-intl/server";

import LocaleLayout from "./[locale]/layout";
import LocaleNotFound from "./[locale]/not-found";

import { defaultLocale } from "@/src/i18n/locales";

// A URL that matches no route and has no real locale (/favicon.ico, /.env: the
// proxy skips dotted paths, and the [locale] layout 404s them, #1853) renders
// this file directly, inside the root layout only. It wraps the [locale] 404 in
// that layout so the page gets the navbar, footer, dark theme and translations,
// and keeps its 404 status (#815).
//
// An unmatched URL under a locale (/de/zzz) is caught by [locale]/[...rest] and
// renders [locale]/not-found.tsx inside the real [locale] layout instead.
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
  // robots: Next injects its own noindex here only when the status is already
  // 404 at render. next start does that; Vercel does not, and production
  // answered /de/zzz with no robots tag at all. So the tag is set on Vercel
  // only: anywhere else it would be a second, identical noindex (#815).
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
