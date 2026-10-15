"use client";

import NextLink from "next/link";
import { usePathname } from "next/navigation";

import { isSupportedLocale } from "@/src/i18n/locales";

export type NotFoundCopy = {
  title: string;
  heading: string;
  description: string;
  home: string;
  download: string;
};

type Props = {
  copyByLocale: Record<string, NotFoundCopy>;
  fallbackLocale: string;
};

// The locale is read from the URL, not from the request (#815). The English-only
// pages (blog, latency, choosing-a-model) are force-static, so a notFound()
// thrown there renders without the proxy's locale header and next-intl falls
// back to English. Every path is /<locale>/..., so the first segment is the
// locale the visitor asked for.
export default function NotFoundContent({
  copyByLocale,
  fallbackLocale,
}: Props) {
  const segment = usePathname()?.split("/")[1] ?? "";
  const locale = isSupportedLocale(segment) ? segment : fallbackLocale;
  const t = copyByLocale[locale] ?? copyByLocale[fallbackLocale];

  return (
    <section className="flex flex-col items-center justify-center gap-6 px-6 py-32 text-center">
      <p className="text-6xl font-extrabold tracking-tight text-gray-500">
        404
      </p>
      <h1 className="text-3xl font-bold text-white">{t.heading}</h1>
      <p className="max-w-md text-base text-gray-400">{t.description}</p>
      <div className="flex flex-wrap items-center justify-center gap-4">
        <NextLink
          className="rounded-xl bg-gradient-to-r from-purple-600 to-blue-600 px-6 py-3 font-semibold text-white"
          href={`/${locale}`}
        >
          {t.home}
        </NextLink>
        <NextLink
          className="rounded-xl border border-white/20 px-6 py-3 font-semibold text-white"
          href={`/${locale}/download`}
        >
          {t.download}
        </NextLink>
      </div>
    </section>
  );
}
