import type { Metadata } from "next";

import { getLocale, getTranslations } from "next-intl/server";

import LocaleLayout from "./[locale]/layout";
import LocaleNotFound from "./[locale]/not-found";

// Every unmatched URL (/de/zzz) renders this file directly, inside the root
// layout only, so it never reaches the [locale] layout on its own. It wraps the
// [locale] 404 in that layout so the page gets the navbar, footer, dark theme
// and translations, and keeps its 404 status (#815).
//
// A notFound() thrown under [locale] renders [locale]/not-found.tsx inside the
// real [locale] layout instead, so the chrome is never doubled.
//
// The locale comes from the proxy's x-next-intl-locale header through
// src/i18n/request.ts, which falls back to the default locale when it is
// absent or unknown.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("notFound");

  // No [locale] layout above this file, so no "%s - HyperWhisper" template.
  // No robots either: there is no layout robots to override here, and Next
  // already injects noindex on a 404.
  return { title: `${t("title")} - HyperWhisper` };
}

export default async function NotFound() {
  const locale = await getLocale();

  return (
    <LocaleLayout params={Promise.resolve({ locale })}>
      <LocaleNotFound />
    </LocaleLayout>
  );
}
