import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "/support",
  title: "Support",
  description: "Get help with HyperWhisper",
});

export default function SupportLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
