import { redirect } from "next/navigation";

// An account route: it stays private, no-store like the portal it forwards to
// (#843). Without this the redirect prerenders and the edge caches it.
export const dynamic = "force-dynamic";

export default async function CustomerRedirect({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  redirect(`/${locale}/user`);
}
