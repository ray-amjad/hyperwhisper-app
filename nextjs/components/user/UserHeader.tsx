"use client";

import { Link as LocaleLink } from "@/src/i18n/navigation";
import { authClient } from "@/src/lib/auth-client";

interface UserHeaderProps {
  user: { email?: string | null };
  locale: string;
  isAdmin: boolean;
}

/**
 * User Header Component
 *
 * Displays the logo (when no sidebar), current user info, admin badge, and sign out button.
 * For admins with sidebar, the logo is hidden since it's in the sidebar.
 * For regular users, shows the logo on the left.
 */
export default function UserHeader({ user, locale, isAdmin }: UserHeaderProps) {
  async function handleSignOut() {
    await authClient.signOut();
    window.location.href = `/${locale}/user/sign-in`;
  }

  return (
    <header className="h-14 border-b border-white/10 bg-slate-900/50 backdrop-blur-sm">
      <div
        className={`h-full px-6 flex items-center justify-between ${
          isAdmin ? "" : "max-w-4xl mx-auto"
        }`}
      >
        {/* Left side - Logo (only shown when no sidebar, i.e., non-admin) */}
        {!isAdmin && (
          <LocaleLink href="/" className="text-lg font-semibold text-white hover:text-gray-200 transition-colors">
            HyperWhisper
          </LocaleLink>
        )}

        {/* Spacer for admin (logo is in sidebar) */}
        {isAdmin && <div />}

        {/* Right side - user info */}
        <div className="flex items-center gap-4">
          {/* Admin badge */}
          {isAdmin && (
            <span className="px-2 py-0.5 text-xs font-medium bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 rounded">
              Admin
            </span>
          )}

          <span className="text-sm text-gray-400 hidden sm:block">
            {user.email}
          </span>

          <button
            onClick={handleSignOut}
            className="px-3 py-1.5 text-sm text-gray-400 hover:text-white hover:bg-white/10 rounded-md transition-colors"
          >
            Sign Out
          </button>
        </div>
      </div>
    </header>
  );
}
