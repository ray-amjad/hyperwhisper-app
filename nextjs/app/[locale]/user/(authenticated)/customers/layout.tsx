import { redirect } from "next/navigation";

import { getPortalSession } from "@/src/lib/auth";

/**
 * Admin gate for this route, ahead of its `loading.tsx` (#915).
 *
 * Next.js renders a segment's layout OUTSIDE that segment's loading boundary,
 * so this redirect lands before anything streams: a non-admin's direct load
 * answers 307 to the dashboard and never sees the admin skeleton. The page
 * repeats the check as defence in depth. `getPortalSession` is wrapped in
 * React `cache()`, so this adds no second session read per request.
 */
export default async function AdminOnlyLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  const session = await getPortalSession();

  if (!session?.user) {
    redirect(`/${locale}/user/sign-in`);
  }

  if (session.user.role !== "admin") {
    redirect(`/${locale}/user/dashboard`);
  }

  return children;
}
