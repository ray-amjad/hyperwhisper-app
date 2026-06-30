import { redirect } from "next/navigation";

export default async function CustomerDashboardRedirect({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  redirect(`/${locale}/user/dashboard`);
}
