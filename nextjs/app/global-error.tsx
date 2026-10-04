"use client";

import "@/styles/globals.css";

import clsx from "clsx";
import { useEffect } from "react";

import { fontSans } from "@/config/fonts";

// Catches what app/[locale]/error.tsx cannot: a throw in a layout, such as
// getMessages() in app/[locale]/layout.tsx. Unlike not-found.tsx, this file
// REPLACES the root layout when it fires, so its <html> and <body> are the
// document's real ones. The i18n provider is gone here, so the text stays English.
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
    <html lang="en">
      <head>
        <title>Something went wrong | HyperWhisper</title>
      </head>
      {/* font-sans reads --font-sans, which fontSans.variable defines. The locale
          layout sets it on a wrapper div this file replaces, so set it here. */}
      <body
        className={clsx(
          "min-h-screen bg-black text-white font-sans antialiased",
          fontSans.variable,
        )}
      >
        <div className="flex flex-col items-center gap-4 py-24 text-center">
          <h2 className="text-2xl font-semibold">Something went wrong.</h2>
          {/* A plain button: the HeroUI provider lives in the layout that threw.
              retry re-fetches the failed server render; reset would replay it */}
          <button
            className="rounded-lg bg-purple-600 px-4 py-2 font-medium text-white cursor-pointer transition-[background-color,transform] duration-150 hover:bg-purple-500 active:scale-[0.96]"
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
