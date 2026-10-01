"use client";

import { useEffect } from "react";

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
      <body>
        <h2>Something went wrong.</h2>
        {/* retry re-fetches the failed server render; reset would replay it */}
        <button onClick={() => retry()}>Try again</button>
      </body>
    </html>
  );
}
