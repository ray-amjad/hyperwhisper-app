import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Checkout",
  description: "Complete your HyperWhisper purchase",
};

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
