"use client";

import { useState } from "react";

import UserHeaderView from "@/components/user/UserHeaderView";
import { authClient } from "@/src/lib/auth-client";
import { createSignOutHandler } from "@/src/lib/sign-out";

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
 *
 * This is the STATEFUL half only. Every byte it renders lives in
 * `UserHeaderView`, which holds no hooks — see the note there for why the split
 * exists (#881 review round 2): a hook-free view can be called as a function in
 * a test, so the button's `onClick` and the busy and failed states are all
 * reachable, and none of them are reachable through `renderToStaticMarkup`.
 */
export default function UserHeader({ user, locale, isAdmin }: UserHeaderProps) {
  const [signingOut, setSigningOut] = useState(false);
  const [signOutError, setSignOutError] = useState<string | null>(null);

  // Built in the render body, not inline in `onClick`: that is what makes the
  // wiring visible to `tests/user-header-sign-out.test.ts`, which can render
  // this component but cannot click it. Every rule about when to navigate and
  // when to clear the busy flag lives in the factory — see `src/lib/sign-out.ts`.
  const handleSignOut = createSignOutHandler({
    signOut: () => authClient.signOut(),
    navigate: (destination) => {
      window.location.href = destination;
    },
    setBusy: setSigningOut,
    setError: setSignOutError,
    redirectTo: `/${locale}/user/sign-in`,
  });

  return (
    <UserHeaderView
      isAdmin={isAdmin}
      signOutError={signOutError}
      signingOut={signingOut}
      user={user}
      onSignOut={() => {
        void handleSignOut();
      }}
    />
  );
}
