import { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;

  return {
    title: "Support",
    description: "Get help with HyperWhisper",
    ...(await localizedPageMetadata(locale, "/support")),
  };
}

export default function SupportLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
