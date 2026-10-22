import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "/purchase-success",
  title: "Purchase Successful",
  description: "Thank you for purchasing HyperWhisper",
});

export default function PurchaseSuccessLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
