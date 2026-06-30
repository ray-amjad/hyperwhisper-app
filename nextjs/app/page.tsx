import { redirect } from "@/src/i18n/navigation";

// This page redirects to the default locale using next-intl's locale-aware redirect
export default function RootPage() {
  redirect({ href: "/", locale: "en" });
}
