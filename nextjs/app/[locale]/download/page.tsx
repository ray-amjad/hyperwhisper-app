"use client";

import {
  Suspense,
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import { useSearchParams } from "next/navigation";
import { AnimatePresence, m } from "framer-motion";
import { Download, Copy, Check, CheckCircle } from "lucide-react";
import { useTranslations } from "next-intl";

import { DuskPage, PageHero } from "@/components/landing/DuskPage";
import { BTN_LG, BTN_WHITE, WRAP } from "@/components/landing/dusk";
import {
  GLASS,
  LABEL,
  LINK_LAV,
  SECTION,
} from "@/components/landing/dusk-pages";
import Reveal from "@/components/landing/Reveal";
import { ICON_SWAP } from "@/lib/icon-swap";
import { Link } from "@/src/i18n/navigation";
import { isMobileDevice } from "@/src/lib/mobile-device";
import { isRecord } from "@/src/lib/type-guards";
import { reportClientError } from "@/lib/shared/report-client-error";

type Platform = "mac" | "windows" | "linux";

/**
 * Shape of /linux-latest.json, written by linux-release.yml on every published
 * Linux release. Linux updates through apt rather than Sparkle, so it has no
 * appcast; this file is the one thing the site needs from a release.
 */
type LinuxLatest = {
  version: string;
  releasedAt: string;
  deb: string;
  aptArchive: string | null;
  checksums: string;
  releasePage: string;
};

function isLinuxLatest(value: unknown): value is LinuxLatest {
  return (
    isRecord(value) &&
    typeof value.version === "string" &&
    typeof value.releasedAt === "string" &&
    typeof value.deb === "string" &&
    (value.aptArchive === null || typeof value.aptArchive === "string") &&
    typeof value.checksums === "string" &&
    typeof value.releasePage === "string"
  );
}

// Used only if /linux-latest.json cannot be read. It points at the releases
// list rather than a pinned version, so a stale build can never advertise a
// version number that has been superseded.
const LINUX_RELEASES_URL =
  "https://github.com/ray-amjad/hyperwhisper-app/releases?q=linux";

// Every release, for the "Release notes" link under the cards.
const RELEASES_URL = "https://github.com/ray-amjad/hyperwhisper-app/releases";

// Every download button.
const CTA = `${BTN_WHITE} ${BTN_LG} w-full sm:w-auto`;

/** One OS card: the whole card selects its platform (see PlatformCard). */
const CARD =
  "relative isolate rounded-[26px] border px-3 py-5 text-center backdrop-blur-[14px] transition-[background-color,border-color] duration-200 ease-out motion-reduce:transition-none sm:px-6 md:px-[30px] md:py-[34px]";
const CARD_ON =
  "border-[rgba(196,181,253,.35)] bg-[linear-gradient(180deg,rgba(129,90,220,.2),rgba(255,255,255,.03))]";
const CARD_OFF =
  "border-[var(--dusk-line)] bg-[var(--dusk-glass)] hover:border-[rgba(196,181,253,.28)] hover:bg-white/[.06]";

/** The terminal panel the Homebrew and Debian commands sit in. */
const TERMINAL =
  "overflow-hidden rounded-2xl border border-[var(--dusk-line)] bg-[#070814] text-start";

/** The two-column install card under the platform panel. */
const INSTALL = `${GLASS} grid items-center gap-6 p-6 text-start md:grid-cols-[.8fr_1.2fr] md:gap-10 md:p-9`;

// The three OS glyphs. Decorative: the card's button names the platform.
const OS_ICON_PATHS: Record<Platform, string> = {
  mac: "M16.4 12.6c0-2.6 2.1-3.8 2.2-3.9-1.2-1.8-3.1-2-3.7-2-1.6-.2-3.1.9-3.9.9-.8 0-2-.9-3.4-.9-1.7 0-3.3 1-4.2 2.6-1.8 3.1-.5 7.7 1.3 10.2.9 1.2 1.9 2.6 3.2 2.6 1.3-.1 1.8-.8 3.3-.8 1.6 0 2 .8 3.4.8 1.4 0 2.3-1.3 3.1-2.5 1-1.4 1.4-2.8 1.4-2.9 0 0-2.7-1-2.7-4.1zM13.9 5c.7-.9 1.2-2 1-3.2-1 0-2.3.7-3 1.6-.7.8-1.2 2-1.1 3.1 1.2.1 2.3-.6 3.1-1.5z",
  windows:
    "M3 5.5l7.5-1v7.3H3zm8.5-1.2L21 3v8.8h-9.5zM3 12.8h7.5v7.3L3 19zm8.5 0H21V21l-9.5-1.3z",
  linux:
    "M12 2c-2.2 0-3.5 1.9-3.5 4.4 0 1.5-.6 2.6-1.6 4-1.2 1.6-2.4 3.6-2.4 6 0 .7.1 1.3.4 1.8-.6.3-1.4.6-1.4 1.3 0 .9 1.6 1 3 1.6 1.1.5 2 .9 2.8.9.7 0 1.2-.4 1.5-.9h2.4c.3.5.8.9 1.5.9.8 0 1.7-.4 2.8-.9 1.4-.6 3-.7 3-1.6 0-.7-.8-1-1.4-1.3.3-.5.4-1.1.4-1.8 0-2.4-1.2-4.4-2.4-6-1-1.4-1.6-2.5-1.6-4C15.5 3.9 14.2 2 12 2z",
};

const OsIcon = ({ platform }: { platform: Platform }) => (
  <svg
    aria-hidden="true"
    className="size-6 md:size-7"
    fill="currentColor"
    viewBox="0 0 24 24"
  >
    <path d={OS_ICON_PATHS[platform]} />
  </svg>
);

/** The three dots of the terminal's title bar. */
const TerminalBar = () => (
  <div
    aria-hidden="true"
    className="flex items-center gap-[7px] border-b border-[var(--dusk-line)] px-3.5 py-[11px]"
  >
    <i className="size-2.5 rounded-full bg-[#2c2f46]" />
    <i className="size-2.5 rounded-full bg-[#2c2f46]" />
    <i className="size-2.5 rounded-full bg-[#2c2f46]" />
  </div>
);

/**
 * One OS card. The platform name is the card's one <button> (aria-pressed, as
 * the old tabs were); its ::after covers the card, so a tap anywhere on it
 * selects that platform, and the focus ring draws round the whole card.
 */
function PlatformCard({
  platform,
  label,
  requirement,
  selected,
  onSelect,
}: {
  platform: Platform;
  label: string;
  requirement: string;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <li className={`${CARD} ${selected ? CARD_ON : CARD_OFF}`}>
      <div
        className={`mx-auto mb-3 grid size-12 place-items-center rounded-[16px] border border-[var(--dusk-line)] transition-colors duration-200 motion-reduce:transition-none md:mb-[18px] md:size-[60px] md:rounded-[18px] ${
          selected
            ? "bg-white/[.12] text-white"
            : "bg-white/[.06] text-white/80"
        }`}
      >
        <OsIcon platform={platform} />
      </div>
      <h2 className="text-base font-semibold sm:text-lg md:text-[22px]">
        <button
          aria-pressed={selected}
          className="cursor-pointer rounded after:absolute after:inset-0 after:rounded-[26px] after:content-[''] focus-visible:outline-none focus-visible:after:outline-2 focus-visible:after:outline-offset-2 focus-visible:after:outline-[var(--dusk-lav)]"
          type="button"
          onClick={onSelect}
        >
          {label}
        </button>
      </h2>
      <p className="mt-1.5 hidden text-[14.5px] text-[var(--dusk-mute)] md:block">
        {requirement}
      </p>
    </li>
  );
}

// #843: the page is prerendered, and a useSearchParams() with no Suspense
// boundary above it fails `next build`. Only the platform choice reads the
// query, so only this null-rendering child sits in the boundary and the rest of
// the page stays in the static HTML.
function PlatformFromQuery({
  onPlatform,
}: {
  onPlatform: (platformParam: string | null) => void;
}) {
  const platformParam = useSearchParams().get("platform");

  useEffect(() => {
    onPlatform(platformParam);
  }, [platformParam, onPlatform]);

  return null;
}

export default function DownloadPage() {
  const t = useTranslations("downloadPage");
  const tNav = useTranslations("navbar");
  const tFooter = useTranslations("footer");
  const [selectedPlatform, setSelectedPlatform] = useState<Platform>("mac");
  const [downloadState, setDownloadState] = useState<
    Record<Platform, { countdown: number; started: boolean }>
  >({
    mac: { countdown: 5, started: false },
    windows: { countdown: 5, started: false },
    linux: { countdown: 5, started: false },
  });
  const [copied, setCopied] = useState(false);
  const [linuxLatest, setLinuxLatest] = useState<LinuxLatest | null>(null);
  // null until the mount effect has looked at the device. The countdown waits
  // for `false`, so a phone never gets a timer, not even for the first render.
  const [isMobile, setIsMobile] = useState<boolean | null>(null);

  const currentState = downloadState[selectedPlatform];

  // #767: a tab press tweens the panel below the tabs to its new height and
  // reveals the new content, instead of swapping both in one frame. Only a
  // press animates; the platform picked on load (query or OS) lands at once.
  // The height is read just before the swap; the layout effect pins the panel
  // at it, then sets the new one, and `.t-panel` tweens between the two.
  const panel = useRef<HTMLDivElement>(null);
  const panelHeightBeforeSwap = useRef<number | null>(null);
  // Ends the tween in flight, so a second press mid-tween starts from where the
  // panel is now instead of racing the first tween's observer and timer.
  const stopPanelTween = useRef<(() => void) | null>(null);
  const [panelEnter, setPanelEnter] = useState(false);

  const choosePlatform = (platform: Platform) => {
    if (platform === selectedPlatform) return;
    panelHeightBeforeSwap.current = panel.current?.offsetHeight ?? null;
    setPanelEnter(true);
    setSelectedPlatform(platform);
  };

  useLayoutEffect(() => {
    const box = panel.current;
    const from = panelHeightBeforeSwap.current;

    panelHeightBeforeSwap.current = null;
    if (!box || from === null) return;
    stopPanelTween.current?.();
    const to = box.offsetHeight;

    if (to === from) return;
    box.dataset.resizing = "";
    box.style.height = `${from}px`;
    void box.offsetHeight;
    box.style.height = `${to}px`;

    const unpin = () => {
      observer.disconnect();
      window.clearTimeout(timer);
      box.removeEventListener("transitionend", onEnd);
      box.style.height = "";
      delete box.dataset.resizing;
      stopPanelTween.current = null;
    };
    // The content's own transitions bubble here too; only the panel's counts.
    const onEnd = (event: TransitionEvent) => {
      if (event.target === box && event.propertyName === "height") unpin();
    };
    // A transition that never runs (reduced motion) fires no transitionend.
    let timer = window.setTimeout(unpin, 400);

    // Content can still change size mid-tween (the Linux version arrives from
    // /linux-latest.json); retarget the pin to it rather than snap at the end.
    const content = box.firstElementChild;
    const observer = new ResizeObserver(() => {
      if (!(content instanceof HTMLElement)) return;
      box.style.height = `${content.offsetHeight}px`;
      window.clearTimeout(timer);
      timer = window.setTimeout(unpin, 400);
    });

    if (content) observer.observe(content);
    box.addEventListener("transitionend", onEnd);
    stopPanelTween.current = unpin;
    // No cleanup: Strict Mode's dev-only re-run would unpin the panel before
    // the tween ran. The next press, or leaving the page, ends it instead.
  }, [selectedPlatform]);

  useEffect(() => () => stopPanelTween.current?.(), []);

  // A phone or tablet cannot run the desktop app, so it gets no auto-download
  // (#1099). This ignores ?platform= on purpose: a phone opening
  // /download?platform=mac must not download the DMG either.
  useEffect(() => {
    setIsMobile(
      isMobileDevice(
        navigator.userAgent ?? "",
        navigator.platform ?? "",
        navigator.maxTouchPoints ?? 0,
      ),
    );
  }, []);

  // Detect user's OS on mount and set platform accordingly
  // URL query param takes precedence over OS detection
  const applyPlatform = useCallback((platformParam: string | null) => {
    // Check URL param first
    if (platformParam === "windows") {
      setSelectedPlatform("windows");

      return;
    }
    if (platformParam === "mac") {
      setSelectedPlatform("mac");

      return;
    }
    if (platformParam === "linux") {
      setSelectedPlatform("linux");

      return;
    }

    // Fall back to OS detection
    const platform = navigator.platform?.toLowerCase() || "";
    const userAgent = navigator.userAgent?.toLowerCase() || "";

    if (platform.includes("win") || userAgent.includes("windows")) {
      setSelectedPlatform("windows");
    } else if (platform.includes("linux") || userAgent.includes("linux")) {
      setSelectedPlatform("linux");
    }
    // macOS is default, no change needed
  }, []);

  // Read the current Linux version once the user is on the Linux tab. A failure
  // is not an error state: the button falls back to the releases list, so the
  // page stays useful if the file is missing or the fetch is blocked.
  useEffect(() => {
    if (selectedPlatform !== "linux" || linuxLatest) return;

    let cancelled = false;

    const fetchLinuxLatest = async () => {
      try {
        const response = await fetch("/linux-latest.json");
        const data: unknown = response.ok ? await response.json() : null;

        if (!cancelled && isLinuxLatest(data)) setLinuxLatest(data);
      } catch (error) {
        // A fetch the cleanup abandoned is nobody's failure.
        if (!cancelled) void reportClientError("download.linux_latest", error);
        // Fall back to LINUX_RELEASES_URL below.
      }
    };

    void fetchLinuxLatest();

    return () => {
      cancelled = true;
    };
  }, [selectedPlatform, linuxLatest]);

  // Reset countdown when switching platforms
  useEffect(() => {
    setDownloadState((prev) => ({
      ...prev,
      [selectedPlatform]: {
        ...prev[selectedPlatform],
        countdown: 5,
        started: false,
      },
    }));
  }, [selectedPlatform]);

  // Countdown timer effect
  // DELAY REASON: Browsers block immediate auto-downloads as a security measure
  // to prevent drive-by downloads. A countdown gives visual feedback and signals
  // to the browser this is an intentional user-initiated action.
  // Skip countdown for platforms with explicit download choices.
  useEffect(() => {
    // Windows and Linux use explicit download buttons, no auto-download.
    if (selectedPlatform !== "mac") return;
    // Only a device known to be a desktop counts down; a phone or tablet, and
    // a render before detection, never does.
    if (isMobile !== false) return;

    const { countdown, started } = currentState;

    // Don't continue countdown if download already started manually
    if (started) return;

    if (countdown > 0) {
      const timerId = setTimeout(() => {
        setDownloadState((prev) => ({
          ...prev,
          [selectedPlatform]: {
            ...prev[selectedPlatform],
            countdown: prev[selectedPlatform].countdown - 1,
          },
        }));
      }, 1000);

      return () => clearTimeout(timerId);
    } else {
      // Countdown finished, trigger download
      triggerDownload(selectedPlatform);
    }
  }, [
    currentState.countdown,
    currentState.started,
    selectedPlatform,
    isMobile,
  ]);

  // No `started` guard here: the countdown effect already stops once `started`
  // is set, so it fires this once, and a click on "Download again" must re-run it.
  const triggerDownload = (platform: Platform) => {
    setDownloadState((prev) => ({
      ...prev,
      [platform]: { ...prev[platform], started: true, countdown: 0 },
    }));

    window.location.href = `/api/download?platform=${platform}`;
  };

  const handleManualDownload = () => {
    triggerDownload(selectedPlatform);
  };

  const handleCopyCommand = async () => {
    try {
      await navigator.clipboard.writeText(t("homebrewCommand"));
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (error) {
      console.error("Failed to copy:", error);
    }
  };

  const platforms: { id: Platform; label: string; requirement: string }[] = [
    { id: "mac", label: "macOS", requirement: t("requirementsMac") },
    { id: "windows", label: "Windows", requirement: t("requirementsWindows") },
    { id: "linux", label: "Linux", requirement: t("requirementsLinux") },
  ];
  const selectedRequirement =
    platforms.find((p) => p.id === selectedPlatform)?.requirement ?? "";

  return (
    <DuskPage>
      <Suspense fallback={null}>
        <PlatformFromQuery onPlatform={applyPlatform} />
      </Suspense>
      <PageHero
        eyebrow={tNav("download")}
        lead={t("heroLead")}
        title={t("title")}
      />

      <section className={SECTION}>
        <div className={WRAP}>
          {/* The platform switch: three OS cards, the chosen one lit. */}
          <ul className="relative z-[3] -mt-10 grid grid-cols-3 gap-2.5 sm:gap-4 md:gap-[18px]">
            {platforms.map((platform) => (
              <PlatformCard
                key={platform.id}
                label={platform.label}
                platform={platform.id}
                requirement={platform.requirement}
                selected={selectedPlatform === platform.id}
                onSelect={() => choosePlatform(platform.id)}
              />
            ))}
          </ul>

          {/* Everything the platform switch controls */}
          <div ref={panel} className="t-panel mt-[18px]">
            <div
              key={selectedPlatform}
              className="t-panel-content space-y-[18px]"
              data-enter={panelEnter ? "" : undefined}
            >
              <div className={`${GLASS} px-5 py-8 text-center sm:px-9`}>
                {/* The cards hide their requirement line on a phone. */}
                <p className="mb-3 text-sm text-[var(--dusk-dim)] md:hidden">
                  {selectedRequirement}
                </p>

                {/* Countdown or status message */}
                <div className="mb-6 text-center">
                  {isMobile ? (
                    <p className="text-lg text-[var(--dusk-mute)]">
                      {t("mobileNote")}
                    </p>
                  ) : selectedPlatform === "windows" ? (
                    <p className="text-lg text-[var(--dusk-mute)]">
                      {t("selectArchitecture")}
                    </p>
                  ) : selectedPlatform === "linux" ? (
                    <p className="text-lg text-[var(--dusk-mute)]">
                      {t("linuxSubtitle")}
                    </p>
                  ) : currentState.started ? (
                    <div className="flex items-center justify-center gap-2 text-lg text-[var(--dusk-mute)]">
                      <CheckCircle
                        aria-hidden="true"
                        className="size-5 text-emerald-400"
                      />
                      <span>{t("downloadStarted")}</span>
                    </div>
                  ) : isMobile === false ? (
                    <p className="text-lg tabular-nums text-[var(--dusk-mute)]">
                      {t("countdownText", { seconds: currentState.countdown })}
                    </p>
                  ) : (
                    // Device not detected yet: hold the line's height, promise nothing
                    <p
                      aria-hidden="true"
                      className="text-lg text-[var(--dusk-mute)]"
                    >
                      {" "}
                    </p>
                  )}
                </div>

                {/* Download Button(s) */}
                <div className="flex justify-center">
                  {selectedPlatform === "windows" ? (
                    <div className="flex w-full flex-col justify-center gap-3 sm:w-auto sm:flex-row">
                      {/* eslint-disable-next-line @next/next/no-html-link-for-pages -- an API redirect to the installer, not a page */}
                      <a
                        className={CTA}
                        href="/api/download?platform=windows&arch=x64"
                      >
                        <Download aria-hidden="true" />
                        {t("downloadX64")}
                      </a>
                      {/* eslint-disable-next-line @next/next/no-html-link-for-pages -- an API redirect to the installer, not a page */}
                      <a
                        className={CTA}
                        href="/api/download?platform=windows&arch=arm64"
                      >
                        <Download aria-hidden="true" />
                        {t("downloadArm64")}
                      </a>
                    </div>
                  ) : selectedPlatform === "linux" ? (
                    <div className="flex w-full flex-col items-center gap-3 sm:w-auto">
                      <a
                        className={CTA}
                        href={linuxLatest?.deb ?? LINUX_RELEASES_URL}
                        rel="noreferrer"
                        target="_blank"
                      >
                        <Download aria-hidden="true" />
                        {linuxLatest
                          ? t("downloadLinuxVersion", {
                              version: linuxLatest.version,
                            })
                          : t("downloadLinux")}
                      </a>
                      <a
                        className={LINK_LAV}
                        href={linuxLatest?.releasePage ?? LINUX_RELEASES_URL}
                        rel="noreferrer"
                        target="_blank"
                      >
                        {t("linuxReleaseNotes")}
                      </a>
                    </div>
                  ) : (
                    <button
                      className={CTA}
                      type="button"
                      onClick={handleManualDownload}
                    >
                      <Download aria-hidden="true" />
                      {currentState.started
                        ? t("downloadAgain")
                        : t("downloadNow")}
                    </button>
                  )}
                </div>
              </div>

              {/* Homebrew Section - only show for macOS */}
              {selectedPlatform === "mac" && (
                <div className={INSTALL}>
                  <div>
                    <p className={LABEL}>{t("orDivider")}</p>
                    <h2 className="mt-2 text-2xl font-semibold tracking-[-0.03em] md:text-[28px]">
                      {t("homebrewTitle")}
                    </h2>
                  </div>
                  <div className={TERMINAL}>
                    <TerminalBar />
                    <div className="flex flex-wrap items-center gap-3 px-4 py-3.5 sm:px-5">
                      <code
                        className="min-w-0 flex-1 font-mono text-[13.5px] leading-7 text-[#e4e4f5] [overflow-wrap:anywhere]"
                        dir="ltr"
                      >
                        <span
                          aria-hidden="true"
                          className="me-2 select-none text-[var(--dusk-lav)]"
                        >
                          $
                        </span>
                        {t("homebrewCommand")}
                      </code>
                      <button
                        className="inline-flex min-w-[80px] shrink-0 items-center justify-center rounded-full border border-white/[.16] bg-white/[.06] px-3 py-1.5 text-xs text-white/80 transition-colors duration-150 hover:bg-white/[.12] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)] motion-reduce:transition-none"
                        type="button"
                        onClick={handleCopyCommand}
                      >
                        <AnimatePresence initial={false} mode="popLayout">
                          <m.span
                            key={copied ? "copied" : "copy"}
                            className="inline-flex me-1"
                            {...ICON_SWAP}
                          >
                            {copied ? (
                              <Check className="size-4 text-emerald-300" />
                            ) : (
                              <Copy className="size-4" />
                            )}
                          </m.span>
                        </AnimatePresence>
                        {copied ? (
                          <span className="text-emerald-300">
                            {t("copied")}
                          </span>
                        ) : (
                          <span>{t("copy")}</span>
                        )}
                      </button>
                    </div>
                  </div>
                </div>
              )}

              {selectedPlatform === "linux" && (
                <>
                  <div className={INSTALL}>
                    <div>
                      <h2 className="text-2xl font-semibold tracking-[-0.03em] md:text-[28px]">
                        {t("linuxInstallTitle")}
                      </h2>
                      <p className="mt-2 leading-[1.55] text-[var(--dusk-mute)]">
                        {t("linuxTargets")}
                        {!linuxLatest && ` ${t("linuxReplaceVersion")}`}
                      </p>
                      <p className="mt-3 text-sm leading-[1.55] text-[var(--dusk-mute)]">
                        {t("linuxInputGroupNote")}
                      </p>
                    </div>
                    <div className={TERMINAL}>
                      <TerminalBar />
                      <div
                        aria-label={t("linuxInstallCommandLabel")}
                        className="overflow-x-auto px-4 py-4 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-[var(--dusk-lav)] sm:px-5"
                        role="region"
                        // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- scrollable region needs a tab stop (WCAG 2.1.1, axe scrollable-region-focusable)
                        tabIndex={0}
                      >
                        <code
                          className="block whitespace-pre font-mono text-[13.5px] leading-8 text-[#e4e4f5]"
                          dir="ltr"
                        >
                          {[
                            `sudo apt install ./hyperwhisper_${linuxLatest?.version ?? "VERSION"}_amd64.deb`,
                            'sudo usermod -aG hyperwhisper-input "$USER"',
                          ].map((line) => (
                            <span key={line} className="block">
                              <span
                                aria-hidden="true"
                                className="me-2 select-none text-[var(--dusk-lav)]"
                              >
                                $
                              </span>
                              {line}
                            </span>
                          ))}
                        </code>
                      </div>
                    </div>
                  </div>

                  <div className={`${GLASS} p-6 text-start md:p-9`}>
                    <h2 className="text-xl font-semibold tracking-[-0.02em]">
                      {t("aptTitle")}
                    </h2>
                    <p className="mt-2 max-w-[760px] leading-[1.55] text-[var(--dusk-mute)]">
                      {t("aptBody")}
                    </p>
                    <a
                      className={`${LINK_LAV} mt-4 inline-block`}
                      href="https://www.hyperwhisper.com/docs/linux-installation"
                      rel="noreferrer"
                      target="_blank"
                    >
                      {t("linuxGuideCta")}
                    </a>
                  </div>
                </>
              )}
            </div>
          </div>

          <Reveal className="mt-[34px] flex flex-wrap items-center justify-center gap-x-3.5 gap-y-2 text-[var(--dusk-dim)]">
            <Link className={LINK_LAV} href="/older-versions">
              {tFooter("links.olderVersions")}
            </Link>
            <span aria-hidden="true">·</span>
            <a
              className={LINK_LAV}
              href={RELEASES_URL}
              rel="noreferrer"
              target="_blank"
            >
              {t("releaseNotes")}
            </a>
          </Reveal>
        </div>
      </section>
    </DuskPage>
  );
}
