import type { Metadata } from "next";

import { getLocale, getTranslations } from "next-intl/server";

import { isSupportedLocale } from "@/src/i18n/locales";
import { Link } from "@/src/i18n/navigation";

// The 404 content for every locale (#815). A notFound() thrown under [locale]
// renders it inside the [locale] layout; an unmatched URL renders it through
// app/not-found.tsx, which wraps it in the same layout.
//
// robots: the [locale] layout's robots is "index, follow", and Next also
// injects its own noindex on a 404, so without this the page carried both
// (#1133). This boundary's robots replaces the layout's, leaving only noindex.
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

export default async function LocaleNotFound() {
  const t = await getTranslations("notFound");

  return (
    <section className="flex flex-col items-center justify-center gap-6 px-6 py-32 text-center">
      <p className="text-6xl font-extrabold tracking-tight text-gray-500">
        404
      </p>
      <h1 className="text-3xl font-bold text-white">{t("heading")}</h1>
      <p className="max-w-md text-base text-gray-400">{t("description")}</p>
      <div className="flex flex-wrap items-center justify-center gap-4">
        <Link
          className="rounded-xl bg-gradient-to-r from-purple-600 to-blue-600 px-6 py-3 font-semibold text-white"
          href="/"
        >
          {t("home")}
        </Link>
        <Link
          className="rounded-xl border border-white/20 px-6 py-3 font-semibold text-white"
          href="/download"
        >
          {t("download")}
        </Link>
      </div>
    </section>
  );
}
