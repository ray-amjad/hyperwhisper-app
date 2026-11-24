"use client";

import { DownloadCta, downloadPath } from "@/components/landing/DownloadCta";
import { BTN_LG, BTN_WHITE } from "@/components/landing/dusk";
import { GLASS } from "@/components/landing/dusk-pages";
import { useDownloadModal } from "@/contexts/DownloadModalContext";

/**
 * Crawlable download CTA at the end of every English blog post. Like every
 * other download CTA, a plain left-click opens the email-capture modal; the
 * href is the /en/download landing page (blog posts are English-only).
 */
export function BlogDownloadCta() {
  const { openModal } = useDownloadModal();

  return (
    <div className={`${GLASS} mt-12 p-6 text-center md:p-8`}>
      <p className="text-lg font-semibold text-white">Download HyperWhisper</p>
      <p className="mt-2 text-[15px] text-[var(--dusk-mute)]">
        Free dictation and speech-to-text for macOS, Windows, and Linux.
      </p>
      <div className="mt-5 flex justify-center">
        <DownloadCta
          className={`${BTN_WHITE} ${BTN_LG}`}
          cta="blog_post"
          href={`/en${downloadPath()}`}
          onOpen={openModal}
        >
          Download
        </DownloadCta>
      </div>
    </div>
  );
}
