"use client";

import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { AnimatePresence, m, useReducedMotion } from "framer-motion";
import { useTranslations } from "next-intl";

import { Navbar } from "@/components/navbar";
import FooterSection from "@/components/landing/FooterSection";
import { OpenSourceBanner } from "@/components/open-source-banner";
import { isSupportedLocale } from "@/src/i18n/locales";

/** `/en`, `/ja`, … (and a trailing slash): the homepage of a locale. */
function isHomePath(pathname: string): boolean {
  const parts = pathname.split("/").filter(Boolean);

  return parts.length === 1 && isSupportedLocale(parts[0]);
}

/**
 * Client-side layout wrapper that conditionally renders navbar/footer
 * based on the current pathname.
 *
 * Full-screen routes (no navbar/footer):
 * - /user/*
 */
export default function LayoutWrapper({
  children,
}: {
  children: React.ReactNode;
}) {
  const pathname = usePathname();
  // MotionConfig's reducedMotion="user" skips transforms only, not the height
  // collapse below, so Reduce Motion is read here (#701: one frame, no ease).
  const reduceMotion = useReducedMotion();
  const t = useTranslations("navbar");

  // Open-source announcement banner: shown until the user dismisses it.
  // Defaults to visible so SSR and first client render match; hidden after
  // mount if a prior dismissal is stored. Until then a returning visitor never
  // sees it: HtmlDocument's blocking script marks <html> before first paint and
  // globals.css hides the banner from that mark (#849).
  const [bannerDismissed, setBannerDismissed] = useState(false);

  useEffect(() => {
    if (localStorage.getItem("os-banner-dismissed") === "1") {
      setBannerDismissed(true);
    }
  }, []);

  const dismissBanner = () => {
    setBannerDismissed(true);
    localStorage.setItem("os-banner-dismissed", "1");
  };

  // Check if this is a full-screen route (user portal)
  const isFullScreenRoute = pathname.includes("/user");
  // The homepage lays a transparent navbar over its full-height hero, with no
  // banner (the hero's own pill links to /open-source) and a full-bleed main.
  const isHome = isHomePath(pathname);

  // Publishes the sticky header's live height as `--site-header-offset`, which
  // globals.css uses as `scroll-padding-top` so focused controls and #anchor
  // targets land below the header. Measured rather than hard-coded because the
  // header grows with text size (the navbar wraps) and shrinks when the banner
  // is dismissed. Full-screen routes have no header, so the offset is 0.
  const headerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const root = document.documentElement;
    const header = headerRef.current;

    if (!header) {
      root.style.setProperty("--site-header-offset", "0px");

      return () => root.style.removeProperty("--site-header-offset");
    }
    // +7px keeps a small gap under the header (121px + 7px = 128px at 100%).
    const observer = new ResizeObserver(() => {
      root.style.setProperty(
        "--site-header-offset",
        `${header.getBoundingClientRect().height + 7}px`,
      );
    });

    observer.observe(header);

    return () => {
      observer.disconnect();
      root.style.removeProperty("--site-header-offset");
    };
  }, [isFullScreenRoute, isHome]);

  if (isFullScreenRoute) {
    // Full-screen layout: no navbar/footer
    return <div className="min-h-screen">{children}</div>;
  }

  const showBanner = !bannerDismissed && !isHome;

  // Regular layout: with navbar and footer
  return (
    <div className="group/site relative flex flex-col min-h-screen">
      {/*
        The first Tab stop: skips the banner and the navbar's dozen links. It
        is visually hidden until focused, then shows as a dusk pill above the
        header. It targets the main landmark, which takes focus (tabIndex -1), so the next
        Tab continues from the page's content.
      */}
      <a
        className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-[60] focus:rounded-full focus:border focus:border-[var(--dusk-line)] focus:bg-[rgba(5,6,13,.92)] focus:px-5 focus:py-2.5 focus:text-sm focus:font-medium focus:text-white focus:shadow-[0_8px_30px_rgba(0,0,0,.45)] focus:backdrop-blur-[16px] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
        href="#main-content"
      >
        {t("skipToContent")}
      </a>
      {/*
        Sticky header holds the announcement banner + navbar. Using `sticky`
        (not `fixed`) keeps it in normal document flow, so the banner naturally
        pushes the navbar and page content down — no manual spacer / height math
        to keep in sync, which previously caused the header to be cut off.
        The homepage is one exception: its header is `fixed` over the hero,
        which is full height on purpose, and has no banner.
        A page that opens on a Dusk sky band (PageHero marks itself
        `data-dusk-hero`) is the other: the page declares it, and a CSS
        `:has()` on the `group/site` wrapper lifts the header out of flow so
        the sky runs up behind it. PageHero pads itself by the header's height
        (`--site-header-h`, globals.css), so this is right in the server HTML,
        with or without JS. A page without one (an error page) keeps `sticky`.
      */}
      <div
        ref={headerRef}
        className={`${isHome ? "fixed inset-x-0" : "sticky group-has-[[data-dusk-hero]]/site:fixed group-has-[[data-dusk-hero]]/site:inset-x-0"} top-0 z-50 w-full flex flex-col`}
      >
        {/*
          The dismiss collapses the banner's height over 300ms instead of
          unmounting it in one frame, so the page eases up rather than stepping
          48px (#849). initial={false}: no enter animation on first render.
        */}
        <AnimatePresence initial={false}>
          {showBanner && (
            <m.div
              key="open-source-banner"
              className="overflow-hidden"
              data-os-banner-root=""
              exit={{ opacity: 0, height: 0 }}
              transition={
                reduceMotion
                  ? { duration: 0 }
                  : { type: "spring", duration: 0.3, bounce: 0 }
              }
            >
              <OpenSourceBanner onDismiss={dismissBanner} />
            </m.div>
          )}
        </AnimatePresence>
        <Navbar overlay={isHome} />
      </div>
      <main
        className={
          isHome
            ? "flex-grow outline-none"
            : // A Dusk sub-page (components/landing/DuskPage.tsx) lays its own
              // full-bleed sky band and centres its sections itself, so the
              // container lets go of its width and gutter while one is inside.
              // Keyed on the page's own mark, not the path, so a 404 or an
              // error page under the same prefix keeps the gutter.
              "container mx-auto max-w-7xl px-6 flex-grow outline-none has-[[data-dusk-page]]:max-w-none has-[[data-dusk-page]]:px-0"
        }
        id="main-content"
        tabIndex={-1}
      >
        {children}
      </main>
      <FooterSection />
    </div>
  );
}
