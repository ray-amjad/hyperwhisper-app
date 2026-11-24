"use client";

import type { AnchorHTMLAttributes, MouseEvent, ReactNode } from "react";
import type { DownloadPlatform } from "@/contexts/DownloadModalContext";

import {
  captureDownloadClicked,
  type DownloadCtaLocation,
} from "@/src/lib/capture-seo-event";

/**
 * Left-click without modifier keys opens the email-capture modal. Crawlers,
 * open-in-new-tab, and no-JS visitors still follow `href` to /{locale}/download.
 */
export function handleDownloadCtaClick(
  event: MouseEvent<HTMLAnchorElement>,
  open: () => void,
) {
  if (
    event.button !== 0 ||
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey
  ) {
    return;
  }
  event.preventDefault();
  open();
}

export function downloadPath(platform?: DownloadPlatform | null): string {
  return platform ? `/download?platform=${platform}` : "/download";
}

type DownloadCtaProps = {
  href: string;
  onOpen: () => void;
  cta: DownloadCtaLocation;
  platform?: DownloadPlatform | null;
  children: ReactNode;
} & Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href" | "onClick">;

export function DownloadCta({
  href,
  onOpen,
  cta,
  platform,
  children,
  ...rest
}: DownloadCtaProps) {
  return (
    <a
      {...rest}
      href={href}
      onClick={(event) => {
        void captureDownloadClicked({ cta, platform });
        handleDownloadCtaClick(event, onOpen);
      }}
    >
      {children}
    </a>
  );
}
