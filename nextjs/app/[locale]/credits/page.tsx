import { redirect } from "next/navigation";

/**
 * Credits Page Redirect
 *
 * The credits functionality has been moved to the customer dashboard.
 * This page redirects to sign-in, passing the license key if present
 * so the sign-in page can auto-authenticate the user.
 *
 * Supported query params from native apps:
 *   - ?license_key=HW-XXXX-XXXX-XXXX-XXXX (macOS, Windows)
 *   - ?id=HW-XXXX-XXXX-XXXX-XXXX          (legacy macOS — kept for older app versions)
 */
export default async function CreditsPage({
  params,
  searchParams,
}: {
  params: Promise<{ locale: string }>;
  searchParams: Promise<{ id?: string; license_key?: string }>;
}) {
  const { locale } = await params;
  const { id, license_key } = await searchParams;
  const raw = id ?? license_key ?? "";

  if (raw.startsWith("HW-")) {
    redirect(
      `/${locale}/user/sign-in?licenseKey=${encodeURIComponent(raw)}&returnTo=/${locale}/user/dashboard`
    );
  }
  redirect(`/${locale}/user/sign-in`);
}
