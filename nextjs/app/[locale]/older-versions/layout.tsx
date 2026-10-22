import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "/older-versions",
  title: "Older Versions",
  description: "Download previous versions of HyperWhisper",
});

export default function DownloadsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
