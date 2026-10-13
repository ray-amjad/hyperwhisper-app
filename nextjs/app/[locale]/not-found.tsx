import type { Metadata } from "next";

import { getTranslations } from "next-intl/server";

import { Link } from "@/src/i18n/navigation";

// Renders inside the [locale] layout, so every localized 404 gets the navbar,
// footer, dark theme and translations (#815). It serves both a notFound()
// thrown under [locale] and an unmatched /<locale>/<path>, which
// [...rest]/page.tsx routes here.
//
// robots: the [locale] layout's robots is "index, follow", and Next also
// injects its own noindex on a 404, so without this the page carried both
// (#1133). This boundary's robots replaces the layout's, leaving only noindex.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("notFound");

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
