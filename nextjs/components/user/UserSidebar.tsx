"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";

import { useUser } from "./UserContext";
import { FOCUS_RING } from "@/components/user/panel-styles";

interface UserSidebarProps {
  locale: string;
}

/** A sidebar link that was clicked, and the path that was showing then. */
export interface PendingNav {
  href: string;
  from: string;
}

/**
 * The href that carries the active pill (#915). `pathname` only changes once
 * a navigation commits, so a clicked link reads active while its navigation
 * is pending. The pending entry stops counting as soon as the path moves
 * away from where the click happened.
 */
export function activeNavHref(
  pathname: string,
  pending: PendingNav | null,
): string {
  return pending && pending.from === pathname ? pending.href : pathname;
}

/**
 * User Sidebar Navigation
 *
 * Provides navigation links for the user portal.
 * Shows admin-only links (Customers) only when user is an admin.
 * Uses emerald accent colors to match customer portal styling.
 */
export default function UserSidebar({ locale }: UserSidebarProps) {
  const pathname = usePathname();
  const { isAdmin } = useUser();
  const [pending, setPending] = useState<PendingNav | null>(null);
  // The path the phone menu was opened on, or null when it is closed.
  const [menuOpenOn, setMenuOpenOn] = useState<string | null>(null);

  // Drop a click once its navigation commits, so a later Back to the old
  // path cannot light the old click again.
  if (pending && pending.from !== pathname) setPending(null);
  const activeHref = activeNavHref(pathname, pending);
  // Close the phone menu on any navigation, not just a nav-item tap: the
  // logo, Back/Forward and in-page links all move the path while this
  // layout stays mounted.
  if (menuOpenOn !== null && menuOpenOn !== pathname) setMenuOpenOn(null);
  const menuOpen = menuOpenOn !== null;

  const navItems = [
    {
      name: "Dashboard",
      href: `/${locale}/user/dashboard`,
      icon: (
        <svg
          className="w-5 h-5"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6"
          />
        </svg>
      ),
      showFor: "all" as const,
    },
    {
      name: "Customers",
      href: `/${locale}/user/customers`,
      icon: (
        <svg
          className="w-5 h-5"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z"
          />
        </svg>
      ),
      showFor: "admin" as const,
    },
    {
      name: "Devices",
      href: `/${locale}/user/devices`,
      icon: (
        <svg
          className="w-5 h-5"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
          />
        </svg>
      ),
      showFor: "admin" as const,
    },
  ];

  // Filter items based on user role
  const visibleItems = navItems.filter(
    (item) => item.showFor === "all" || (item.showFor === "admin" && isAdmin)
  );

  const logo = (
    <Link
      href={`/${locale}/user/dashboard`}
      className={`flex items-center gap-3 rounded-lg ${FOCUS_RING}`}
      // A tap on the logo while already on the dashboard moves no path.
      onNavigate={() => setMenuOpenOn(null)}
    >
      {/* eslint-disable-next-line @next/next/no-img-element -- images are unoptimized on this site */}
      <img
        alt=""
        className="size-8 rounded-[8px] shadow-[0_0_0_1px_rgba(255,255,255,.1),0_6px_18px_rgba(0,0,0,.45)]"
        height={32}
        src="/icon/128.png"
        width={32}
      />
      <div>
        <h1 className="text-[15.5px] font-semibold tracking-[-0.01em] text-white">HyperWhisper</h1>
        <p className="text-[11px] uppercase tracking-[1.4px] text-[var(--dusk-dim)]">
          {isAdmin ? "Admin Dashboard" : "Dashboard"}
        </p>
      </div>
    </Link>
  );

  const navList = (
    <ul className="space-y-1">
      {visibleItems.map((item) => {
        const isActive = activeHref === item.href;
        return (
          <li key={item.name}>
            <Link
              // Colour alone does not tell a screen reader which page is
              // open (#1314). Read from the committed path, not the pending
              // click: a stalled navigation must not name a page not shown.
              aria-current={pathname === item.href ? "page" : undefined}
              href={item.href}
              className={`flex items-center gap-3 px-3.5 py-2.5 rounded-xl border text-[14.5px] transition-[background-color,color,border-color] duration-200 ease-[cubic-bezier(.2,0,0,1)] ${FOCUS_RING} motion-reduce:transition-none ${
                isActive
                  ? "bg-[rgba(196,181,253,.1)] text-white border-[rgba(196,181,253,.24)] [&_svg]:text-[var(--dusk-lav)]"
                  : "border-transparent text-[var(--dusk-mute)] hover:bg-white/[.05] hover:text-white"
              }`}
              // Fires only for an in-app navigation, never for a
              // modifier, middle or new-tab click.
              onNavigate={() => {
                setPending({ href: item.href, from: pathname });
                setMenuOpenOn(null);
              }}
            >
              {item.icon}
              <span className="font-medium">{item.name}</span>
            </Link>
          </li>
        );
      })}
    </ul>
  );

  return (
    <>
      {/* Phone bar (#1330): below md the sidebar folds behind a menu button,
          so the main column gets the full viewport width. */}
      <div className="md:hidden bg-[rgba(5,6,13,.6)] border-b border-[var(--dusk-line)] backdrop-blur-[16px]">
        <div className="flex items-center justify-between px-4 py-3">
          {logo}
          <button
            type="button"
            onClick={() => setMenuOpenOn(menuOpen ? null : pathname)}
            aria-expanded={menuOpen}
            aria-controls="user-sidebar-menu"
            aria-label={menuOpen ? "Close menu" : "Open menu"}
            className={`p-2 rounded-lg text-[var(--dusk-mute)] hover:bg-white/[.06] hover:text-white ${FOCUS_RING}`}
          >
            <svg
              className="w-6 h-6"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d={
                  menuOpen ? "M6 18L18 6M6 6l12 12" : "M4 6h16M4 12h16M4 18h16"
                }
              />
            </svg>
          </button>
        </div>
        {menuOpen && (
          <nav id="user-sidebar-menu" className="px-4 pb-4">
            {navList}
          </nav>
        )}
      </div>

      {/* Sticky beside the sticky header (review round 1), so the logo row
          and the header stay level on a long Customers list. */}
      <aside className="hidden md:block md:sticky md:top-0 md:h-screen md:self-start overflow-y-auto w-64 shrink-0 bg-[rgba(5,6,13,.45)] border-r border-[var(--dusk-line)]">
        {/* Logo */}
        <div className="px-5 h-14 flex items-center border-b border-[var(--dusk-line)]">{logo}</div>

        {/* Navigation */}
        <nav className="p-3">{navList}</nav>
      </aside>
    </>
  );
}
