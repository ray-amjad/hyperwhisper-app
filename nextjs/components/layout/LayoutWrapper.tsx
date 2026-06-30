"use client";

import { usePathname } from "next/navigation";

import { Navbar } from "@/components/navbar";
import FooterSection from "@/components/landing/FooterSection";
import { SaleBanner } from "@/components/sale-banner";

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

  // Check if this is a full-screen route (user portal)
  const isFullScreenRoute = pathname.includes("/user");

  if (isFullScreenRoute) {
    // Full-screen layout: no navbar/footer
    return <div className="min-h-screen">{children}</div>;
  }

  // Regular layout: with navbar and footer
  return (
    <div className="relative flex flex-col min-h-screen">
      {/* Sticky header container for sale banner + navbar */}
      <div className="fixed top-0 z-50 w-full flex flex-col">
        {/* Sale banner disabled - uncomment to re-enable */}
        {/* <SaleBanner /> */}
        <Navbar />
      </div>
      {/* Spacer to account for fixed header height (navbar ~64px) */}
      <div className="h-[64px]" />
      <main className="container mx-auto max-w-7xl px-6 flex-grow">
        {children}
      </main>
      <FooterSection />
    </div>
  );
}
