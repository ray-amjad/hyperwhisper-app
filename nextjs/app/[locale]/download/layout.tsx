import { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;

  return {
    title: "Download",
    description: "Download HyperWhisper for macOS, Windows, and Linux",
    ...(await localizedPageMetadata(locale, "/download")),
  };
}

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
