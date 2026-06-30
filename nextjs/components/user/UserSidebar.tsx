"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

import { useUser } from "./UserContext";

interface UserSidebarProps {
  locale: string;
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

  return (
    <aside className="w-64 bg-slate-900/50 border-r border-white/10 min-h-screen">
      {/* Logo */}
      <div className="p-6 border-b border-white/10">
        <Link
          href={`/${locale}/user/dashboard`}
          className="flex items-center gap-3"
        >
          <div className="w-10 h-10 rounded-lg bg-gradient-to-br from-emerald-500 to-teal-500 flex items-center justify-center">
            <span className="text-white font-bold text-lg">H</span>
          </div>
          <div>
            <h1 className="text-white font-semibold">HyperWhisper</h1>
            <p className="text-gray-500 text-xs">
              {isAdmin ? "Admin Dashboard" : "Dashboard"}
            </p>
          </div>
        </Link>
      </div>

      {/* Navigation */}
      <nav className="p-4">
        <ul className="space-y-2">
          {visibleItems.map((item) => {
            const isActive = pathname === item.href;
            return (
              <li key={item.name}>
                <Link
                  href={item.href}
                  className={`flex items-center gap-3 px-4 py-3 rounded-lg transition-all duration-200 ${
                    isActive
                      ? "bg-emerald-500/20 text-emerald-300 border border-emerald-500/30"
                      : "text-gray-400 hover:bg-white/5 hover:text-white"
                  }`}
                >
                  {item.icon}
                  <span className="font-medium">{item.name}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </aside>
  );
}
