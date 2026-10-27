import { Metadata } from "next";
import { redirect } from "next/navigation";

import { getPortalSession } from "@/src/lib/auth";
import { canRotateAccountKeys } from "@/src/lib/session-sign-in-method";
import UserDashboardClient from "./UserDashboardClient";

export const metadata: Metadata = {
  title: "My Account",
};

/**
 * User Dashboard Page
 *
 * Server component wrapper that:
 * 1. Verifies authentication (backup to middleware)
 * 2. Determines admin status
 * 3. Passes user info to client component
 *
 * The client component handles all the data fetching and UI.
 */
export default async function UserDashboardPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const session = await getPortalSession();

  if (!session?.user) {
    redirect(`/${locale}/user/sign-in`);
  }

  const user = session.user;
  const isAdmin = user.role === "admin";

  return (
    <UserDashboardClient
      // Only a session proven by the inbox may rotate an Account Key.
      canRotateKeys={canRotateAccountKeys(session.session.signInMethod)}
      user={{
        email: user.email || "",
        id: user.id,
      }}
      isAdmin={isAdmin}
    />
  );
}
