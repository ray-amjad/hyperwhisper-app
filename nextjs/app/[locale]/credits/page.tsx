import { redirect } from "next/navigation";

import CreditsPurchase from "@/components/credits/CreditsPurchase";

/**
 * Credits Page
 *
 * Default (no params): the guest buy-credits flow — enter an email, pick an
 * amount, and check out. The webhook mints a license key (the wallet) and emails
 * it. See components/credits/CreditsPurchase.tsx.
 *
 * Native-app deep links still carry a key and route through sign-in so the user
 * lands on their dashboard to top up an existing wallet:
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

  return (
    <main className="min-h-screen bg-black px-6 py-24 md:py-28">
      <CreditsPurchase />
    </main>
  );
}
