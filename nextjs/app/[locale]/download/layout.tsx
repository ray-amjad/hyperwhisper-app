import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Download",
  description: "Download HyperWhisper for macOS, Windows, and Linux",
};

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
