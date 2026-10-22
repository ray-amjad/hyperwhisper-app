import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export const generateMetadata = localizedPageMetadata({
  path: "/download",
  title: "Download",
  description: "Download HyperWhisper for macOS, Windows, and Linux",
});

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
