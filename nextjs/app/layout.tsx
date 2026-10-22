import { ReactNode } from "react";

type Props = {
  children: ReactNode;
};

// Root layout required by Next.js. It only passes children through: the
// <html>/<body> shell is rendered by app/[locale]/layout.tsx, where the locale
// arrives as a param (next-intl's documented App Router layout). Reading it here
// with headers() made every route dynamic and uncacheable (#843).
// app/not-found.tsx wraps its page in the [locale] layout for the same shell.
export default function RootLayout({ children }: Props) {
  return children;
}
