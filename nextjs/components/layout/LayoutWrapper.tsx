"use client";

import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";

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

  if (isFullScreenRoute) {
    // Full-screen layout: no navbar/footer
    return <div className="min-h-screen">{children}</div>;
  }

  const showBanner = !bannerDismissed;

  // Regular layout: with navbar and footer
  return (
    <div className="relative flex flex-col min-h-screen">
      {/* Sticky header container for announcement banner + navbar */}
      <div className="fixed top-0 z-50 w-full flex flex-col">
        {showBanner && <OpenSourceBanner onDismiss={dismissBanner} />}
        <Navbar />
      </div>
      {/* Spacer for fixed header height (navbar 64px + 48px banner when shown) */}
      <div className={showBanner ? "h-[112px]" : "h-[64px]"} />
      <main className="container mx-auto max-w-7xl px-6 flex-grow">
        {children}
      </main>
      <FooterSection />
    </div>
  );
}
