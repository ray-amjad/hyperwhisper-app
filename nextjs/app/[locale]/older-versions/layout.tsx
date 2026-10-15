import { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;

  return {
    title: "Older Versions",
    description: "Download previous versions of HyperWhisper",
    ...(await localizedPageMetadata(locale, "/older-versions")),
  };
}

export default function DownloadsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
