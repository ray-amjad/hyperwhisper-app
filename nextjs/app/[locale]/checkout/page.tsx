import { redirect } from "next/navigation";

/**
 * /checkout — legacy purchase entry point (kept as a redirect).
 *
 * The standalone license checkout was retired: buying credits now mints the key
 * (the license IS the wallet). Native apps (macOS `LicenseManager.openPurchasePage`,
 * Windows `PurchaseUrl`) and the docs still link to `/checkout`, so this page
 * stays as a permanent redirect into the credits buy flow. A license key (if
 * present) is forwarded so existing users land on the top-up path rather than
 * minting a second key; a `code` (promo) param is preserved too.
 */
export default async function CheckoutPage({
  params,
  searchParams,
}: {
  params: Promise<{ locale: string }>;
  searchParams: Promise<{ id?: string; license_key?: string; code?: string }>;
}) {
  const { locale } = await params;
  const { id, license_key, code } = await searchParams;

  const query = new URLSearchParams();
  const key = id ?? license_key;
  if (key) query.set("license_key", key);
  if (code) query.set("code", code);
  const qs = query.toString();

  redirect(`/${locale}/credits${qs ? `?${qs}` : ""}`);
}
