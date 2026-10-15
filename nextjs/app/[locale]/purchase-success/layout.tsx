import { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;

  return {
    title: "Purchase Successful",
    description: "Thank you for purchasing HyperWhisper",
    ...(await localizedPageMetadata(locale, "/purchase-success")),
  };
}

export default function PurchaseSuccessLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
