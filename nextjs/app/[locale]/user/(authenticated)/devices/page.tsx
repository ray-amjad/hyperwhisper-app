import { Metadata } from "next";
import { redirect } from "next/navigation";
import { headers } from "next/headers";

import { auth } from "@/src/lib/auth";
import DevicesClient from "./DevicesClient";

export const metadata: Metadata = {
  title: "Device Activations",
};

/**
 * Devices Page (Admin-Only)
 *
 * Server component that:
 * 1. Verifies authentication
 * 2. Verifies admin access (redirects non-admins to dashboard)
 * 3. Renders the client component
 */
export default async function DevicesPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const session = await auth.api.getSession({ headers: await headers() });

  if (!session?.user) {
    redirect(`/${locale}/user/sign-in`);
  }

  if (session.user.role !== "admin") {
    redirect(`/${locale}/user/dashboard`);
  }

  return <DevicesClient />;
}
