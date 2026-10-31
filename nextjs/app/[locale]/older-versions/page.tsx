"use client";

import { Download, Calendar, HardDrive, Monitor, Sparkles } from "lucide-react";
import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";

import { DuskPage, PageHero } from "@/components/landing/DuskPage";
import { BTN_GLASS, BTN_WHITE, WRAP } from "@/components/landing/dusk";
import {
  SECTION,
  SEG,
  SEG_ITEM,
  SEG_OFF,
  SEG_ON,
} from "@/components/landing/dusk-pages";
import { intlLocale } from "@/lib/format-date";
import { reportClientError } from "@/lib/shared/report-client-error";
import { useRevealAfterLoad } from "@/lib/use-reveal-after-load";

type Platform = "mac" | "windows";

/** A version card; the newest one gets the lit variant. */
const CARD =
  "h-full rounded-[22px] border p-6 transition-[border-color,background-color] duration-150 motion-reduce:transition-none";
const CARD_PLAIN =
  "border-[var(--dusk-line)] bg-[var(--dusk-glass)] hover:border-[rgba(196,181,253,.28)]";
const CARD_LATEST =
  "border-[rgba(196,181,253,.35)] bg-[linear-gradient(180deg,rgba(129,90,220,.2),rgba(255,255,255,.03))]";

// `formatDate` runs in render, once per row: report its failure once per tab,
// not once per row per render (#739).
let formatDateFailureReported = false;

function reportFormatDateFailure(
  error: unknown,
  context?: Record<string, string>,
): void {
  if (formatDateFailureReported) return;
  formatDateFailureReported = true;
  void reportClientError("older_versions.format_date", error, context);
}

/**
 * Interface representing a single download variant (one per architecture)
 */
interface DownloadVariant {
  arch: string; // e.g. "x64", "ARM64", or "" for macOS
  downloadUrl: string;
  fileSize: number;
}

/**
 * Interface representing a version with potentially multiple download variants
 * On macOS there's one variant per version; on Windows there are two (x64 + ARM64)
 */
interface Version {
  version: string;
  pubDate: string;
  buildNumber: string;
  minimumSystemVersion: string;
  downloads: DownloadVariant[];
}

/**
 * Downloads Page Component
 *
 * This page fetches and displays all available HyperWhisper versions from the appcast.xml feed.
 * Users can browse and download older versions of the application.
 *
 * Features:
 * - Parses Sparkle appcast.xml format
 * - Displays versions in reverse chronological order (newest first)
 * - Shows formatted release dates and file sizes
 * - Provides direct download links for each version
 * - The Dusk sub-page design: the sky band, then a grid of glass cards
 */
export default function DownloadsPage() {
  const t = useTranslations("olderVersions");
  const locale = useLocale();
  const [versions, setVersions] = useState<Version[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedPlatform, setSelectedPlatform] = useState<Platform>("mac");
  // The platform `versions` was fetched for; a switch keeps the old list on
  // screen until the new feed lands, so its labels must not follow the toggle.
  const [listPlatform, setListPlatform] = useState<Platform>("mac");
  // Flips true two frames after loading ends, so the content paints at its
  // hidden/dimmed opacity first and the CSS transition has a start value.
  const revealed = useRevealAfterLoad(loading);

  /**
   * Fetches and parses the appcast.xml file on component mount
   * The XML is parsed using the browser's DOMParser API
   * Extracts version information from Sparkle-formatted XML entries
   */
  useEffect(() => {
    // Guard against out-of-order resolution when the platform changes while a
    // fetch is still in flight. Without this, a slow request for the previously
    // selected platform can resolve last and overwrite the current platform's
    // version list (e.g. macOS tab showing Windows downloads).
    let ignore = false;
    const controller = new AbortController();

    async function fetchVersions() {
      try {
        setLoading(true);
        setError(null);
        // Fetch the appcast.xml file from the public directory
        const feedPath =
          selectedPlatform === "windows"
            ? "/appcast-windows.xml"
            : "/appcast.xml";
        const response = await fetch(feedPath, { signal: controller.signal });

        if (!response.ok) {
          throw new Error(`Failed to fetch ${feedPath}`);
        }

        const xmlText = await response.text();

        // Parse XML using browser's DOMParser
        const parser = new DOMParser();
        const xmlDoc = parser.parseFromString(xmlText, "text/xml");

        // Extract all <item> elements and group by version number
        const items = xmlDoc.querySelectorAll("item");
        const versionMap = new Map<string, Version>();

        items.forEach((item) => {
          // Extract data from Sparkle namespace elements
          const title = item.querySelector("title")?.textContent || "";
          const pubDate = item.querySelector("pubDate")?.textContent || "";
          const buildNumber =
            item.querySelector("sparkle\\:version, version")?.textContent || "";
          const shortVersionString =
            item.querySelector(
              "sparkle\\:shortVersionString, shortVersionString",
            )?.textContent || "";
          const minimumSystemVersion =
            item.querySelector(
              "sparkle\\:minimumSystemVersion, minimumSystemVersion",
            )?.textContent || "";
          const os = item.querySelector("sparkle\\:os, os")?.textContent || "";
          const enclosure = item.querySelector("enclosure");
          const downloadUrl = enclosure?.getAttribute("url") || "";
          const fileSize = parseInt(enclosure?.getAttribute("length") || "0");

          if (!buildNumber || !downloadUrl) return;

          // Determine architecture label from sparkle:os (e.g. "windows-x64" -> "x64")
          let arch = "";

          if (os.includes("arm64")) arch = "ARM64";
          else if (os.includes("x64")) arch = "x64";

          const variant: DownloadVariant = { arch, downloadUrl, fileSize };

          const displayVersion = shortVersionString || title || buildNumber;

          const existing = versionMap.get(buildNumber);

          if (existing) {
            existing.downloads.push(variant);
          } else {
            versionMap.set(buildNumber, {
              version: displayVersion,
              pubDate,
              buildNumber,
              minimumSystemVersion,
              downloads: [variant],
            });
          }
        });

        if (ignore) return;
        setVersions(Array.from(versionMap.values()));
        setListPlatform(selectedPlatform);
        setLoading(false);
      } catch (err) {
        // Ignore aborts triggered by cleanup / a newer platform selection.
        if (
          ignore ||
          (err instanceof DOMException && err.name === "AbortError")
        ) {
          return;
        }
        void reportClientError("older_versions.fetch", err, {
          platform: selectedPlatform,
        });
        // "" = no message to show; the render puts the translated fallback in.
        setError(err instanceof Error ? err.message : "");
        setLoading(false);
      }
    }

    void fetchVersions();

    return () => {
      ignore = true;
      controller.abort();
    };
  }, [selectedPlatform]);

  /**
   * Formats a date string from RFC 2822 format to a readable format
   * Example: "Sun, 12 Oct 2025 11:52:35 +0900" -> "October 12, 2025" on /en;
   * every other locale gets its own date format
   */
  const formatDate = (dateString: string) => {
    try {
      const date = new Date(dateString);

      // An unparseable `pubDate` does not throw: it renders "Invalid Date".
      if (Number.isNaN(date.getTime())) {
        reportFormatDateFailure(new Error("Unparseable appcast pubDate"), {
          pub_date: dateString,
        });
      }

      return date.toLocaleDateString(intlLocale(locale), {
        year: "numeric",
        month: "long",
        day: "numeric",
      });
    } catch (error) {
      reportFormatDateFailure(error);

      return dateString;
    }
  };

  /**
   * Formats file size from bytes to human-readable format
   * Example: 16926355 -> "16.14 MB"
   */
  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return t("unknownSize");
    const mb = bytes / (1024 * 1024);

    return `${mb.toFixed(2)} MB`;
  };

  /**
   * Determines if a version is the latest (first in the list)
   */
  const isLatestVersion = (index: number) => index === 0;

  const platformLabel = listPlatform === "mac" ? "macOS" : "Windows";
  const downloadLabel =
    listPlatform === "mac" ? t("downloadDmg") : t("downloadExe");

  return (
    <DuskPage>
      <PageHero lead={t("subtitle")} title={t("title")}>
        <div className={SEG}>
          <button
            aria-pressed={selectedPlatform === "mac"}
            className={`${SEG_ITEM} ${selectedPlatform === "mac" ? SEG_ON : SEG_OFF}`}
            type="button"
            onClick={() => setSelectedPlatform("mac")}
          >
            macOS
          </button>
          <button
            aria-pressed={selectedPlatform === "windows"}
            className={`${SEG_ITEM} ${selectedPlatform === "windows" ? SEG_ON : SEG_OFF}`}
            type="button"
            onClick={() => setSelectedPlatform("windows")}
          >
            Windows
          </button>
        </div>
      </PageHero>

      <section className={SECTION}>
        <div className={`${WRAP} max-w-[960px]`}>
          {/* Error State */}
          {error !== null && (
            <div className="rounded-[22px] border border-red-400/30 bg-red-500/[.08] p-8 text-center">
              <p className="text-lg text-red-300" role="alert">
                {t("errorLoading", { error: error || t("unknownError") })}
              </p>
            </div>
          )}

          {/* The skeleton is aria-hidden, so this always-mounted live region
              announces the first load and every platform-switch refetch. */}
          <p className="sr-only" role="status">
            {loading ? t("loading") : ""}
          </p>

          {/* Versions List: skeleton and content share one slot and cross-fade.
              A platform switch keeps the previous list mounted, dimmed. */}
          {error === null && (
            <div className="t-skel grid">
              <div
                aria-hidden
                className={`t-skel-skeleton [grid-area:1/1] pointer-events-none grid grid-cols-1 md:grid-cols-2 gap-4 transition-[opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
                  loading && versions.length === 0
                    ? "opacity-100 blur-[0px]"
                    : "opacity-0 blur-[2px]"
                }`}
              >
                {Array.from({ length: 6 }).map((_, i) => (
                  <div key={i} className={`${CARD} ${CARD_PLAIN} space-y-4`}>
                    {["w-32", "w-40", "w-24", "w-28"].map((w) => (
                      <div
                        key={w}
                        className={`h-4 bg-white/10 rounded ${w} animate-pulse motion-reduce:animate-none`}
                      />
                    ))}
                    <div className="h-12 bg-white/10 rounded-full animate-pulse motion-reduce:animate-none" />
                  </div>
                ))}
              </div>
              <div
                aria-busy={loading}
                className={`t-skel-content [grid-area:1/1] grid grid-cols-1 md:grid-cols-2 gap-4 content-start transition-[opacity,filter] duration-[400ms] ease-in-out motion-reduce:transition-none ${
                  !loading && revealed
                    ? "opacity-100 blur-[0px]"
                    : versions.length > 0
                      ? "opacity-40 blur-[2px]"
                      : "opacity-0 blur-[2px]"
                }`}
                inert={loading}
              >
                {versions.map((version, index) => (
                  <div
                    key={version.buildNumber}
                    className={`${CARD} ${isLatestVersion(index) ? CARD_LATEST : CARD_PLAIN}`}
                  >
                    {/* Version Header */}
                    <div className="mb-4 flex items-start gap-3">
                      <div
                        className={`grid size-[38px] shrink-0 place-items-center rounded-[11px] ${
                          isLatestVersion(index)
                            ? "bg-white text-[#0b0b14]"
                            : "bg-[rgba(196,181,253,.12)] text-[var(--dusk-lav)]"
                        }`}
                      >
                        {isLatestVersion(index) ? (
                          <Sparkles
                            aria-hidden="true"
                            className="size-[18px]"
                          />
                        ) : (
                          <Download
                            aria-hidden="true"
                            className="size-[18px]"
                          />
                        )}
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className="mb-1 flex flex-wrap items-center gap-2">
                          <h3 className="text-xl font-semibold tracking-[-0.02em] text-white">
                            v{version.version}
                          </h3>
                          {isLatestVersion(index) && (
                            <span className="rounded-full bg-white px-2.5 py-0.5 text-xs font-semibold text-[#111]">
                              {t("latest")}
                            </span>
                          )}
                        </div>
                        <p className="text-sm text-[var(--dusk-dim)]">
                          {t("build", { number: version.buildNumber })}
                        </p>
                      </div>
                    </div>

                    {/* Version Details */}
                    <div className="mb-5 space-y-2.5 text-[var(--dusk-mute)]">
                      <div className="flex items-center gap-2">
                        <Calendar
                          aria-hidden="true"
                          className="size-4 text-[var(--dusk-lav)]"
                          strokeWidth={1.5}
                        />
                        <span className="text-sm">
                          {formatDate(version.pubDate)}
                        </span>
                      </div>
                      {/* One size per card only when there is one file; a
                          multi-arch card puts each file's size on its own
                          button (#2086). */}
                      {version.downloads.length === 1 && (
                        <div className="flex items-center gap-2">
                          <HardDrive
                            aria-hidden="true"
                            className="size-4 text-[var(--dusk-lav)]"
                            strokeWidth={1.5}
                          />
                          <span className="text-sm">
                            {formatFileSize(
                              version.downloads[0]?.fileSize ?? 0,
                            )}
                          </span>
                        </div>
                      )}
                      <div className="flex items-center gap-2">
                        <Monitor
                          aria-hidden="true"
                          className="size-4 text-[var(--dusk-lav)]"
                          strokeWidth={1.5}
                        />
                        <span className="text-sm">
                          {platformLabel} {version.minimumSystemVersion}+
                        </span>
                      </div>
                    </div>

                    {/* Download Button(s) */}
                    <div
                      className={`flex gap-2 ${version.downloads.length > 1 ? "flex-col sm:flex-row" : ""}`}
                    >
                      {version.downloads.map((dl) => (
                        <a
                          key={dl.arch || "default"}
                          className={`${isLatestVersion(index) ? BTN_WHITE : BTN_GLASS} min-h-12 flex-1 px-5 py-2.5 text-[15px]`}
                          href={dl.downloadUrl}
                        >
                          <Download aria-hidden="true" />
                          {version.downloads.length > 1 ? (
                            // Two buttons share a ~350px row: the size goes on
                            // a second line, so the label, size and icon fit.
                            // The space keeps the link's text and accessible
                            // name "ARM64 172.02 MB", not "ARM64172.02 MB".
                            <span className="flex flex-col items-start leading-tight">
                              <span>{dl.arch}</span>{" "}
                              <span className="text-xs font-normal opacity-80">
                                {formatFileSize(dl.fileSize)}
                              </span>
                            </span>
                          ) : dl.arch ? (
                            `${dl.arch}`
                          ) : (
                            downloadLabel
                          )}
                        </a>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </section>
    </DuskPage>
  );
}
