import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Download",
  description: "Download HyperWhisper for macOS and Windows",
};

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
