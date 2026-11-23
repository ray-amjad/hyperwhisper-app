"use client";

import { Link } from "@/src/i18n/navigation";
import { BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import { GLASS } from "@/components/landing/dusk-pages";
import { captureDownloadClicked } from "@/src/lib/capture-seo-event";

/** Crawlable download CTA at the end of every English blog post. */
export function BlogDownloadCta() {
  return (
    <div className={`${GLASS} mt-12 p-6 text-center md:p-8`}>
      <p className="text-lg font-semibold text-white">Download HyperWhisper</p>
      <p className="mt-2 text-[15px] text-[var(--dusk-mute)]">
        Free dictation and speech-to-text for macOS, Windows, and Linux.
      </p>
      <div className="mt-5 flex justify-center">
        <Link
          className={`${BTN_WHITE} ${BTN_LG}`}
          href="/download"
          onClick={() => {
            void captureDownloadClicked({ cta: "blog_post" });
          }}
        >
          Download
        </Link>
      </div>
    </div>
  );
}
