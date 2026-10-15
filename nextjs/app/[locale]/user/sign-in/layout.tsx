import { Metadata } from "next";

import { localizedPageMetadata } from "@/lib/localized-page-metadata";

export async function generateMetadata({
  params,
}: {
  params: Promise<{ locale: string }>;
}): Promise<Metadata> {
  const { locale } = await params;

  return {
    title: "Sign In",
    ...(await localizedPageMetadata(locale, "/user/sign-in")),
  };
}

export default function SignInLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return children;
}
