"use client";

import { Link as LocaleLink } from "@/src/i18n/navigation";

export interface UserHeaderViewProps {
  user: { email?: string | null };
  isAdmin: boolean;
  /** Disarms the button and swaps its label while a sign-out is in flight. */
  signingOut: boolean;
  /** The message from a refused sign-out, or `null` when there is none. */
  signOutError: string | null;
  onSignOut: () => void;
}

/**
 * The presentational half of `UserHeader` (#881 review round 2).
 *
 * It owns every byte the header renders and it holds NO state and NO hooks, so
 * a test can call it as a plain function and walk the element tree it returns.
 * That is the only way the button's `onClick` is reachable in this repo:
 * `renderToStaticMarkup` is the whole rendering surface here and React does not
 * serialise event handlers, so a markup assertion can never see the wiring.
 * Round 1 shipped exactly that gap — deleting `onClick` killed no test.
 *
 * Being hook-free also makes the busy and failed states reachable. They are
 * props here, not `useState` values an initial render never reaches, so a test
 * can render this in those states and assert POSITIVELY on the markup.
 *
 * The stateful half — the two flags and the `createSignOutHandler` call — stays
 * in `UserHeader.tsx`.
 *
 * Note it takes no `locale`: the locale only ever reached the redirect target,
 * which the handler owns, and the logo link resolves its own locale.
 */
export default function UserHeaderView({
  user,
  isAdmin,
  signingOut,
  signOutError,
  onSignOut,
}: UserHeaderViewProps) {
  return (
    <header className="h-14 border-b border-white/10 bg-slate-900/50 backdrop-blur-sm">
      <div
        className={`h-full px-6 flex items-center justify-between ${
          isAdmin ? "" : "max-w-4xl mx-auto"
        }`}
      >
        {/* Left side - Logo (only shown when no sidebar, i.e., non-admin) */}
        {!isAdmin && (
          <LocaleLink
            className="text-lg font-semibold text-white hover:text-gray-200 transition-colors"
            href="/"
          >
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

          {signOutError && (
            <span className="text-red-300 text-sm" role="alert">
              {signOutError}
            </span>
          )}

          <button
            className="px-3 py-1.5 text-sm text-gray-400 hover:text-white hover:bg-white/10 rounded-md transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            disabled={signingOut}
            onClick={onSignOut}
          >
            {signingOut ? "Signing Out..." : "Sign Out"}
          </button>
        </div>
      </div>
    </header>
  );
}
