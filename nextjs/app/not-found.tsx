import type { Metadata } from "next";

import Link from "next/link";

// The last-resort 404, for a request that never reaches the [locale] segment.
// Every /<locale>/<path> 404 renders app/[locale]/not-found.tsx instead (#815).
export const metadata: Metadata = {
  title: "Page not found - HyperWhisper",
  robots: { index: false },
};

export default function NotFound() {
  return (
    <>
      {/*
        No <html> or <body> here. Next renders this file INSIDE the root layout
        (app/layout.tsx), which already supplies the document's only <html> and
        <body>. A nested <html> made React 19 compare its props with the real
        document and log a hydration mismatch on every 404 (#1224).

        dir is set on the content because the root <html> carries the locale's
        dir, so on an RTL locale the document is dir="rtl". This text is English
        and reads left-to-right under every locale, so it states its own
        direction.

        The root layout loads no stylesheet, so the dark background is inline
        and covers the viewport (the body keeps its default margin).
      */}
      <main
        dir="ltr"
        style={{
          position: "fixed",
          inset: 0,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: "center",
          gap: "1rem",
          background: "#000",
          color: "#fff",
          fontFamily: "system-ui, sans-serif",
        }}
      >
        <h1>404 - Not Found</h1>
        <Link href="/" style={{ color: "#a78bfa" }}>
          Go to the HyperWhisper home page
        </Link>
      </main>
    </>
  );
}
