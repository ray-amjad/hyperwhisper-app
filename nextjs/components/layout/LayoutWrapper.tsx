"use client";

import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";

import { Navbar } from "@/components/navbar";
import FooterSection from "@/components/landing/FooterSection";
import { OpenSourceBanner } from "@/components/open-source-banner";

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

  // Open-source announcement banner: shown until the user dismisses it.
  // Defaults to visible so SSR and first client render match; hidden after
  // mount if a prior dismissal is stored.
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
    // +7px keeps a small gap under the header (113px + 7px = 120px at 100%).
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
  }, [isFullScreenRoute]);

  if (isFullScreenRoute) {
    // Full-screen layout: no navbar/footer
    return <div className="min-h-screen">{children}</div>;
  }

  const showBanner = !bannerDismissed;

  // Regular layout: with navbar and footer
  return (
    <div className="relative flex flex-col min-h-screen">
      {/*
        Sticky header holds the announcement banner + navbar. Using `sticky`
        (not `fixed`) keeps it in normal document flow, so the banner naturally
        pushes the navbar and page content down — no manual spacer / height math
        to keep in sync, which previously caused the header to be cut off.
      */}
      <div ref={headerRef} className="sticky top-0 z-50 w-full flex flex-col">
        {showBanner && <OpenSourceBanner onDismiss={dismissBanner} />}
        <Navbar />
      </div>
      <main className="container mx-auto max-w-7xl px-6 flex-grow">
        {children}
      </main>
      <FooterSection />
    </div>
  );
}
