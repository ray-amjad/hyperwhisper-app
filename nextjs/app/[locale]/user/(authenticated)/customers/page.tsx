import { Metadata } from "next";
import { redirect } from "next/navigation";

import { getPortalSession } from "@/src/lib/auth";
import CustomersClient from "./CustomersClient";

export const metadata: Metadata = {
  title: "Customer Management",
};

/**
 * Customers Page (Admin-Only)
 *
 * Server component that:
 * 1. Verifies authentication
 * 2. Verifies admin access (redirects non-admins to dashboard)
 * 3. Renders the client component
 *
 * Middleware also enforces admin access, but we double-check here
 * as defense-in-depth.
 */
export default async function CustomersPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const session = await getPortalSession();

  // Not authenticated - redirect to sign-in
  if (!session?.user) {
    redirect(`/${locale}/user/sign-in`);
  }

  // Not admin - redirect to dashboard
  if (session.user.role !== "admin") {
    redirect(`/${locale}/user/dashboard`);
  }

  return <CustomersClient />;
}
