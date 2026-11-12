import type { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

const pageMetadata = localizedPageMetadata({
  path: "/purchase-success",
  title: "Purchase Successful",
  description: "Thank you for purchasing HyperWhisper",
});

/*
  Stripe's success_url lands here after a checkout. A direct visit still shows
  the confirmation copy, so the page stays out of search results. Next merges
  metadata per top-level key, so this `robots` replaces the [locale] layout's
  "index, follow" (googleBot included) rather than merging into it.
*/
export async function generateMetadata(
  props: Parameters<typeof pageMetadata>[0],
): Promise<Metadata> {
  return {
    ...(await pageMetadata(props)),
    robots: { index: false, follow: false },
  };
}

export default function PurchaseSuccessLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
