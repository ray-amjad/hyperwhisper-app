import { redirect } from "next/navigation";

import { firstParam } from "../credits/first-param";

/**
 * /checkout — legacy purchase entry point (kept as a redirect).
 *
 * The standalone license checkout was retired: buying credits now mints the key
 * (the license IS the wallet). Native apps (macOS `LicenseManager.openPurchasePage`,
 * Windows `PurchaseUrl`) and the docs still link to `/checkout`, so this page
 * stays as a permanent redirect into the credits buy flow. A license key (if
 * present) is forwarded so existing users land on the top-up path rather than
 * minting a second key; a `code` (promo) param is preserved too. A repeated
 * parameter forwards its first value, not a comma-joined one (#2181).
 */
export default async function CheckoutPage({
  params,
  searchParams,
}: {
  params: Promise<{ locale: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { locale } = await params;
  const { id, license_key, code } = await searchParams;

  const query = new URLSearchParams();
  const key = firstParam(id) ?? firstParam(license_key);

  if (key) query.set("license_key", key);
  const promo = firstParam(code);

  if (promo) query.set("code", promo);
  const qs = query.toString();

  redirect(`/${locale}/credits${qs ? `?${qs}` : ""}`);
}
