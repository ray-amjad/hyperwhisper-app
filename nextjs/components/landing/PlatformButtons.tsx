"use client";

import type { CSSProperties } from "react";

import { useTranslations } from "next-intl";

import { BTN_GLASS, BTN_LG, BTN_WHITE } from "./dusk";

import {
  useDownloadModal,
  type DownloadPlatform,
} from "@/contexts/DownloadModalContext";

function AppleIcon() {
  return (
    <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
      <path d="M16.4 12.6c0-2.6 2.1-3.8 2.2-3.9-1.2-1.8-3.1-2-3.7-2-1.6-.2-3.1.9-3.9.9-.8 0-2-.9-3.4-.9-1.7 0-3.3 1-4.2 2.6-1.8 3.1-.5 7.7 1.3 10.2.9 1.2 1.9 2.6 3.2 2.6 1.3-.1 1.8-.8 3.3-.8 1.6 0 2 .8 3.4.8 1.4 0 2.3-1.3 3.1-2.5 1-1.4 1.4-2.8 1.4-2.9 0 0-2.7-1-2.7-4.1zM13.9 5c.7-.9 1.2-2 1-3.2-1 0-2.3.7-3 1.6-.7.8-1.2 2-1.1 3.1 1.2.1 2.3-.6 3.1-1.5z" />
    </svg>
  );
}

function WindowsIcon() {
  return (
    <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
      <path d="M3 5.5l7.5-1v7.3H3zm8.5-1.2L21 3v8.8h-9.5zM3 12.8h7.5v7.3L3 19zm8.5 0H21V21l-9.5-1.3z" />
    </svg>
  );
}

function LinuxIcon() {
  return (
    <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
      <path d="M12 2c-2.2 0-3.5 1.9-3.5 4.4 0 1.5-.6 2.6-1.6 4-1.2 1.6-2.4 3.6-2.4 6 0 .7.1 1.3.4 1.8-.6.3-1.4.6-1.4 1.3 0 .9 1.6 1 3 1.6 1.1.5 2 .9 2.8.9.7 0 1.2-.4 1.5-.9h2.4c.3.5.8.9 1.5.9.8 0 1.7-.4 2.8-.9 1.4-.6 3-.7 3-1.6 0-.7-.8-1-1.4-1.3.3-.5.4-1.1.4-1.8 0-2.4-1.2-4.4-2.4-6-1-1.4-1.6-2.5-1.6-4C15.5 3.9 14.2 2 12 2z" />
    </svg>
  );
}

/**
 * "Download for Mac", "Windows", "Linux". Each opens the download modal (the
 * optional email capture) for its platform; the modal then goes to
 * /download?platform=…, which takes the file URL from the appcast.
 */
export default function PlatformButtons({
  className,
  style,
}: {
  className?: string;
  style?: CSSProperties;
}) {
  const t = useTranslations("hero");
  const tNav = useTranslations("navbar");
  const { openModalFor } = useDownloadModal();
  const open = (platform: DownloadPlatform) => () => openModalFor(platform);

  return (
    <div
      className={`flex flex-wrap items-center justify-center gap-3 ${className ?? ""}`}
      style={style}
    >
      <button
        className={`${BTN_WHITE} ${BTN_LG}`}
        type="button"
        onClick={open("mac")}
      >
        <AppleIcon />
        {t("downloadMac")}
      </button>
      {/* OS names are never translated (see tests/locale-linux-platform-copy).
          The hidden, translated "Download" makes each accessible name say
          what the button does ("Download Windows"). The visible OS name stays
          inside that accessible name (WCAG 2.5.3, label in name). */}
      <button
        className={`${BTN_GLASS} ${BTN_LG}`}
        type="button"
        onClick={open("windows")}
      >
        <WindowsIcon />
        <span className="sr-only">{tNav("download")} </span>
        Windows
      </button>
      <button
        className={`${BTN_GLASS} ${BTN_LG}`}
        type="button"
        onClick={open("linux")}
      >
        <LinuxIcon />
        <span className="sr-only">{tNav("download")} </span>
        Linux
      </button>
    </div>
  );
}
