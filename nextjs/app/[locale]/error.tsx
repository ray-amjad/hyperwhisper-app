"use client";

import { useEffect } from "react";
import { Button } from "@heroui/button";

import { BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import { DuskPage, delay } from "@/components/landing/DuskPage";

const TEXT_WRAP = "[overflow-wrap:anywhere] [text-wrap:balance]";

export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    // Log the error to an error reporting service
    /* eslint-disable no-console */
    console.error(error, {
      digest: error.digest,
      path: window.location.pathname,
    });
  }, [error]);

  return (
    <DuskPage className="flex items-start justify-center bg-[radial-gradient(900px_420px_at_50%_-10%,rgba(129,90,220,.18),transparent_65%)]">
      <div className="flex flex-col items-center gap-6 px-6 py-24 text-center md:py-32">
        <h2
          className={`dusk-enter text-3xl font-semibold tracking-[-0.03em] text-white md:text-4xl ${TEXT_WRAP}`}
          style={delay(0)}
        >
          Something went wrong!
        </h2>
        <div className="dusk-enter" style={delay(80)}>
          <Button
            className={`${BTN_WHITE} ${BTN_LG} h-auto min-w-0`}
            onPress={
              // Attempt to recover by trying to re-render the segment
              () => reset()
            }
          >
            Try again
          </Button>
        </div>
      </div>
    </DuskPage>
  );
}
