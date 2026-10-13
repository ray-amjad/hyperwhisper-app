import { isSupportedLocale } from "./locales";

// #1135: pages whose page.tsx calls notFound() for every locale but "en".
// Every locale links to them with a raw /en/... href, so a visit there says
// nothing about the visitor's language.
export const ENGLISH_ONLY_REGEX = /^\/en\/(latency|choosing-a-model|blog)(\/|$)/;

export function isEnglishOnlyPath(pathname: string): boolean {
  return ENGLISH_ONLY_REGEX.test(pathname);
}

function readLocaleCookie(cookieHeader: string): string | undefined {
  for (const part of cookieHeader.split(";")) {
    const [name, ...rest] = part.trim().split("=");
    if (name === "NEXT_LOCALE") {
      try {
        return decodeURIComponent(rest.join("="));
      } catch {
        return undefined;
      }
    }
  }
  return undefined;
}

/**
 * #1170: the locale the navbar's home and section links point at.
 *
 * On an English-only page `useLocale()` is "en" whatever the visitor's saved
 * language, so `/en#features` would send a German visitor to an ordinary page
 * that writes NEXT_LOCALE=en. There the links follow NEXT_LOCALE instead. Read
 * in the browser only (document.cookie), so these pages stay static.
 */
export function navbarHomeLocale(
  pathname: string,
  pageLocale: string,
  cookieHeader: string,
): string {
  if (!isEnglishOnlyPath(pathname)) return pageLocale;
  const saved = readLocaleCookie(cookieHeader);
  return saved && isSupportedLocale(saved) ? saved : pageLocale;
}
