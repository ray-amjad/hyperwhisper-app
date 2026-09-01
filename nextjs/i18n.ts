import { notFound } from "next/navigation";
import { getRequestConfig } from "next-intl/server";
import { defaultLocale, isSupportedLocale, locales } from "./src/i18n/locales";

export type Locale = (typeof locales)[number];
export { defaultLocale, locales };

export default getRequestConfig(async ({ locale }) => {
  // Validate that the incoming `locale` parameter is valid. `isSupportedLocale`
  // is a type guard, so `locale` is a `Locale` below rather than a cast.
  if (!locale || !isSupportedLocale(locale)) notFound();

  return {
    locale,
    messages: (await import(`./messages/${locale}.json`)).default,
  };
});
