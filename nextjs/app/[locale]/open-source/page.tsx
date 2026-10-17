import type { Metadata } from "next";

import { getTranslations, setRequestLocale } from "next-intl/server";

import OpenSourceContent from "@/components/open-source-content";

type Props = {
  params: Promise<{ locale: string }>;
};

// The locale comes from the segment params, as in app/[locale]/layout.tsx: the
// [locale] layout has already 404'd a segment that is not a locale (#1853).
export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "openSource" });

  return {
    title: t("metaTitle"),
    description: t("metaDescription"),
  };
}

export default async function OpenSourcePage({ params }: Props) {
  const { locale } = await params;

  setRequestLocale(locale);

  return <OpenSourceContent />;
}
