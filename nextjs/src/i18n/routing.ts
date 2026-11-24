import { defineRouting } from "next-intl/routing";

import { defaultLocale, locales } from "./locales";

export const routing = defineRouting({
  locales,
  defaultLocale,
  // Always use locale prefix in URL
  localePrefix: "always",
  // Locale detection (Accept-Language + NEXT_LOCALE cookie) makes `/` redirect
  // to a different prefix per visitor, so next-intl's 307 must stay. A 308
  // would be cached by browsers and CDNs and pin every visitor to one locale.
  // hreflang x-default already names the 200 URL /en, not this redirect.
});
