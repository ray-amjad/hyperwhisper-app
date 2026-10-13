"use client";

import { useLocale } from "next-intl";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";

import { isEnglishOnlyPath, siteHref } from "./english-only";

/**
 * #1170: shared by the navbar and the footer. `nativeLinks` is true once an
 * English-only page has hydrated: its site links must then be native <a>s
 * with the hrefs siteHref gives, since a next-intl Link would prefix "en".
 */
export function useSiteHref() {
  const locale = useLocale();
  const pathname = usePathname();
  const englishOnly = isEnglishOnlyPath(pathname);
  const [cookieHeader, setCookieHeader] = useState<string | undefined>();

  useEffect(() => {
    setCookieHeader(englishOnly ? document.cookie : undefined);
  }, [englishOnly, pathname]);

  return {
    nativeLinks: englishOnly && cookieHeader !== undefined,
    siteHref: (path: string) => siteHref(pathname, locale, cookieHeader, path),
  };
}
