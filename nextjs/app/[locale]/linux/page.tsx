import type { Metadata } from "next";

import { getTranslations, setRequestLocale } from "next-intl/server";

import LinuxPageContent from "@/components/linux/linux-page-content";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

type Props = {
  params: Promise<{ locale: string }>;
};

// The canonical, hreflang map and og:url for this path (#843). The title and
// description stay translated per locale.
const localizedMetadata = localizedPageMetadata({ path: "/linux" });

// The locale comes from the segment params, as in app/[locale]/layout.tsx: the
// [locale] layout has already 404'd a segment that is not a locale (#1853).
export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const metadata = await localizedMetadata({ params });
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "linuxPage" });

  return {
    ...metadata,
    // metaTitle already names HyperWhisper in every catalogue, so it skips the
    // layout's "%s - HyperWhisper" template, which would brand it twice (#707).
    title: { absolute: t("metaTitle") },
    description: t("metaDescription"),
  };
}

export default async function LinuxPage({ params }: Props) {
  const { locale } = await params;

  setRequestLocale(locale);

  return <LinuxPageContent />;
}
