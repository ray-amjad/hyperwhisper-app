import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Support",
  description: "Get help with HyperWhisper",
};

export default function SupportLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
