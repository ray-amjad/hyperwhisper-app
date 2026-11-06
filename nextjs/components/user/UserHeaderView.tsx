"use client";

import { Link as LocaleLink } from "@/src/i18n/navigation";
import { BTN_SECONDARY_XS, CHIP, CHIP_LAV, FOCUS_RING } from "@/components/user/panel-styles";

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
    <header className="sticky top-0 z-30 h-14 border-b border-[var(--dusk-line)] bg-[rgba(5,6,13,.72)] backdrop-blur-[16px]">
      <div
        className={`h-full px-4 sm:px-6 flex items-center justify-between gap-4 ${
          isAdmin ? "" : "max-w-4xl mx-auto"
        }`}
      >
        {/* Left side - Logo (only shown when no sidebar, i.e., non-admin) */}
        {!isAdmin && (
          <LocaleLink
            className={`shrink-0 whitespace-nowrap flex items-center gap-2.5 rounded-lg text-[17px] font-semibold text-white transition-opacity hover:opacity-85 ${FOCUS_RING}`}
            href="/"
          >
            {/* Decorative: the link is named by the word beside it. */}
            {/* eslint-disable-next-line @next/next/no-img-element -- images are unoptimized on this site */}
            <img
              alt=""
              className="size-[26px] rounded-[7px]"
              height={26}
              src="/icon/128.png"
              width={26}
            />
            HyperWhisper
          </LocaleLink>
        )}

        {/* Spacer for admin (logo is in sidebar) */}
        {isAdmin && <div />}

        {/* Right side - user info. `min-w-0` lets it shrink below its content so
            a long email truncates instead of pushing into the logo (#1658). */}
        <div className="flex min-w-0 items-center gap-4">
          {/* Admin badge */}
          {isAdmin && (
            <span className={`shrink-0 ${CHIP} ${CHIP_LAV}`}>
              Admin
            </span>
          )}

          <span
            className="min-w-0 truncate text-sm text-[var(--dusk-mute)] hidden sm:block"
            title={user.email ?? undefined}
          >
            {user.email}
          </span>

          {/* #2286: the message fades in (@starting-style) instead of
              arriving in one frame. Not a .t-error-reveal height reveal: this
              is a row in a fixed h-14 header, and a width reveal would wrap
              the text mid-tween. */}
          {signOutError && (
            <span
              className="text-rose-300 text-sm transition-opacity duration-[280ms] ease-[var(--motion-ease-smooth-out)] starting:opacity-0 motion-reduce:transition-none"
              role="alert"
            >
              {signOutError}
            </span>
          )}

          <button
            className={`shrink-0 whitespace-nowrap ${BTN_SECONDARY_XS}`}
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
