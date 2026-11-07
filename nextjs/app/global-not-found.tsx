import type { Metadata } from "next";

import { getLocale, getTranslations } from "next-intl/server";

import LocaleLayout from "./[locale]/layout";
import LocaleNotFound from "./[locale]/not-found";

// Every unmatched URL (/de/zzz) renders this file, on its own route, with no
// layout above it (experimental.globalNotFound in next.config.mjs). It wraps the
// [locale] 404 in the [locale] layout, which renders the <html>/<body> shell, so
// the page gets the navbar, footer, dark theme and translations, and keeps its
// 404 status (#815).
//
// It used to be app/not-found.tsx. Next renders that boundary on every page, so
// its getLocale() (a headers() call) made every route dynamic (#843). This file
// renders only for an unmatched URL, so reading the request here is free.
//
// The locale comes from the proxy's x-next-intl-locale header through
// src/i18n/request.ts, which falls back to the default locale when it is
// absent or unknown.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("notFound");

  // No [locale] layout metadata above this file, so no "%s - HyperWhisper"
  // template. No robots here: Next injects its own noindex when the status is
  // already 404 at render. next start does that, and so does OpenNext on
  // Cloudflare Workers (checked with wrangler dev, 2026-10-29: /de/zzz got two
  // identical noindex tags while this file added one too). Vercel did not,
  // which is why #815 once set it here on Vercel only.
  return {
    title: `${t("title")} - HyperWhisper`,
  };
}

export default async function GlobalNotFound() {
  const locale = await getLocale();

  return (
    <LocaleLayout params={Promise.resolve({ locale })}>
      <LocaleNotFound locale={locale} />
    </LocaleLayout>
  );
}
