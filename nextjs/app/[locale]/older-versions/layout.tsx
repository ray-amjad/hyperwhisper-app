import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Older Versions",
  description: "Download previous versions of HyperWhisper",
};

export default function DownloadsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
