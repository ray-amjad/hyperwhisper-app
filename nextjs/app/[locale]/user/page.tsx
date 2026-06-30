import { redirect } from "next/navigation";

/**
 * User Portal Root Page
 *
 * Redirects to dashboard. Middleware handles unauthenticated users.
 */
export default async function UserPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  redirect(`/${locale}/user/dashboard`);
}
