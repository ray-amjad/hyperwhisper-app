import { redirect } from "next/navigation";

export default async function CustomerRedirect({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  redirect(`/${locale}/user`);
}
