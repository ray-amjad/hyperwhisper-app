import { redirect } from "next/navigation";
import { headers } from "next/headers";

import { auth } from "@/src/lib/auth";
import { UserProvider } from "@/components/user/UserContext";
import UserSidebar from "@/components/user/UserSidebar";
import UserHeader from "@/components/user/UserHeader";

/**
 * User Portal Layout
 *
 * Unified layout for all authenticated users (customers and admins).
 *
 * Features:
 * - Server-side authentication check
 * - Admin detection via email allowlist
 * - Conditional sidebar (only for admins)
 * - Header with user info and sign-out
 * - UserProvider context for child components
 *
 * Layout Variations:
 * - Admins: Full-width with sidebar on left
 * - Regular users: Centered content with max-width (like customer portal)
 */
export default async function UserLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const session = await auth.api.getSession({ headers: await headers() });

  // Double-check auth (middleware should handle this, but be safe)
  if (!session?.user) {
    redirect(`/${locale}/user/sign-in`);
  }

  const user = session.user;

  const isAdmin = user.role === "admin";

  return (
    <UserProvider email={user.email || ""} isAdmin={isAdmin}>
      <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-800 to-slate-900">
        <div className="flex">
          {/* Sidebar - Only visible for admins */}
          {isAdmin && <UserSidebar locale={locale} />}

          {/* Main Content */}
          <div className="flex-1 flex flex-col min-h-screen">
            <UserHeader user={user} locale={locale} isAdmin={isAdmin} />
            <main
              className={`flex-1 ${
                isAdmin ? "p-6" : "w-full max-w-4xl mx-auto px-6 py-8"
              }`}
            >
              {children}
            </main>
          </div>
        </div>
      </div>
    </UserProvider>
  );
}
