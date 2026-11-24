"use client";

import "@/styles/globals.css";

import clsx from "clsx";
import { useEffect } from "react";

import { fontSans } from "@/config/fonts";

// Catches what app/[locale]/error.tsx cannot: a throw in a layout, such as
// getMessages() in app/[locale]/layout.tsx. Unlike not-found.tsx, this file
// REPLACES the root layout when it fires, so its <html> and <body> are the
// document's real ones. The i18n provider is gone here, so the text stays English.
// next-themes is gone too, so "dark" is set by hand to keep HeroUI's dark tokens (#1151).
export default function GlobalError({
  error,
  retry,
}: {
  error: Error & { digest?: string };
  retry: () => void;
}) {
  useEffect(() => {
    /* eslint-disable no-console */
    console.error(
      "[global-error]",
      {
        digest: error.digest,
        message: error.message,
        path: window.location.pathname,
      },
      error,
    );
  }, [error]);

  return (
    <html className="dark" lang="en">
      <head>
        <title>Something went wrong | HyperWhisper</title>
      </head>
      {/* font-sans reads --font-sans, which fontSans.variable defines. The locale
          layout sets it on a wrapper div this file replaces, so set it here. */}
      <body
        className={clsx(
          "min-h-screen bg-[#05060d] text-[#f5f5ff] font-sans antialiased",
          fontSans.variable,
        )}
      >
        {/* The Dusk look, with its colours written out: this file must not
            depend on a component from the layout that threw. */}
        <div className="flex min-h-screen flex-col items-center justify-center gap-6 bg-[radial-gradient(900px_420px_at_50%_-10%,rgba(129,90,220,.2),transparent_65%),linear-gradient(180deg,#0a0f2a,#05060d_420px)] px-6 py-24 text-center">
          <h2 className="text-3xl font-semibold tracking-[-0.03em] text-white [text-wrap:balance] md:text-4xl">
            Something went wrong.
          </h2>
          {/* A plain button: the HeroUI provider lives in the layout that threw.
              retry re-fetches the failed server render; reset would replay it */}
          <button
            className="inline-flex cursor-pointer items-center justify-center rounded-full bg-white px-[22px] py-[13px] text-[15px] font-semibold text-[#0b0b14] transition-[background-color,scale] duration-150 ease-out hover:bg-[#ececf5] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#c4b5fd] active:scale-[.96] motion-reduce:transition-none"
            type="button"
            onClick={() => retry()}
          >
            Try again
          </button>
        </div>
      </body>
    </html>
  );
}
