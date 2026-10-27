import type { Metadata } from "next";

import { getTranslations, setRequestLocale } from "next-intl/server";

import OpenSourceContent from "@/components/open-source-content";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

type Props = {
  params: Promise<{ locale: string }>;
};

// The canonical, hreflang map and og:url for this path (#843). The title and
// description stay translated per locale (#1059).
const localizedMetadata = localizedPageMetadata({ path: "/open-source" });

// The locale comes from the segment params, as in app/[locale]/layout.tsx: the
// [locale] layout has already 404'd a segment that is not a locale (#1853).
export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const metadata = await localizedMetadata({ params });
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "openSource" });

  return {
    ...metadata,
    // metaTitle already ends in "| HyperWhisper" in all 40 catalogues, so it
    // skips the layout's "%s - HyperWhisper" template, which would brand it
    // twice (#707).
    title: { absolute: t("metaTitle") },
    description: t("metaDescription"),
  };
}

export default async function OpenSourcePage({ params }: Props) {
  const { locale } = await params;

  setRequestLocale(locale);

  return <OpenSourceContent />;
}
