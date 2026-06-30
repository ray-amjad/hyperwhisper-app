import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Purchase Successful",
  description: "Thank you for purchasing HyperWhisper",
};

export default function PurchaseSuccessLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
