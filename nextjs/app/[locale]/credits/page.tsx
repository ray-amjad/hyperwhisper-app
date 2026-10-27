import type { Metadata } from "next";

import { getTranslations } from "next-intl/server";
import { redirect } from "next/navigation";

import { firstParam } from "./first-param";

import CreditsPurchase from "@/components/credits/CreditsPurchase";
import { localizedPageMetadata } from "@/lib/localized-page-metadata";

type Props = {
  params: Promise<{ locale: string }>;
};

const localizedMetadata = localizedPageMetadata({ path: "/credits" });

// The page's own title and description, from the strings the page renders as
// its heading (#707). Without them it fell back to the home page's title and
// description.
export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const metadata = await localizedMetadata({ params });
  const { locale } = await params;
  const t = await getTranslations({ locale, namespace: "buyCredits" });

  return {
    ...metadata,
    title: t("title"),
    description: t("subtitle"),
  };
}

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
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { locale } = await params;
  const { id, license_key } = await searchParams;
  const raw = firstParam(id) ?? firstParam(license_key) ?? "";

  if (raw.startsWith("HW-")) {
    redirect(
      `/${locale}/user/sign-in?licenseKey=${encodeURIComponent(raw)}&returnTo=/${locale}/user/dashboard`
    );
  }

  return (
    <main className="min-h-screen bg-black py-24 md:py-28">
      <CreditsPurchase locale={locale} />
    </main>
  );
}
