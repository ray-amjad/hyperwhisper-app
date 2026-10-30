"use client";

import NextLink from "next/link";
import { usePathname } from "next/navigation";

import { BTN_GLASS, BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import { DuskPage, PageHero } from "@/components/landing/DuskPage";
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
    <DuskPage>
      <PageHero
        eyebrow="404"
        lead={t.description}
        sky="night"
        title={t.heading}
      >
        <div className="flex flex-wrap items-center justify-center gap-3">
          <NextLink className={`${BTN_WHITE} ${BTN_LG}`} href={`/${locale}`}>
            {t.home}
          </NextLink>
          <NextLink
            className={`${BTN_GLASS} ${BTN_LG}`}
            href={`/${locale}/download`}
          >
            {t.download}
          </NextLink>
        </div>
      </PageHero>
    </DuskPage>
  );
}
