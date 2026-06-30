import { redirect } from "next/navigation";

export default async function CustomerSignInRedirect({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const { locale } = await params;
  redirect(`/${locale}/user/sign-in`);
}
