import { isSupportedLocale } from "./locales";

// #1135: pages whose page.tsx calls notFound() for every locale but "en".
// Every locale links to them with a raw /en/... href, so a visit there says
// nothing about the visitor's language.
export const ENGLISH_ONLY_REGEX =
  /^\/en\/(latency|choosing-a-model|blog)(\/|$)/;

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
 * #1170: the href for a site link (`/`, `/#features`, `/support`) in the
 * shared navbar and footer.
 *
 * On an English-only page `useLocale()` is "en" for every visitor, and
 * `/en/...` is an ordinary page that writes NEXT_LOCALE=en. So there, once
 * the browser passes `document.cookie` in (never on the server, so these
 * pages stay static and their server HTML keeps `/en`), the link follows a
 * saved NEXT_LOCALE. With none (or an unsupported one) it keeps the page
 * locale, so a visitor with no saved choice still gets `/en...`.
 *
 * `cookieHeader` is undefined before hydration.
 */
export function siteHref(
  pathname: string,
  pageLocale: string,
  cookieHeader: string | undefined,
  path: string,
): string {
  let locale = pageLocale;

  if (isEnglishOnlyPath(pathname) && cookieHeader !== undefined) {
    const saved = readLocaleCookie(cookieHeader);

    if (saved && isSupportedLocale(saved)) locale = saved;
  }

  if (path === "/") return `/${locale}`;
  if (path.startsWith("/#")) return `/${locale}${path.slice(1)}`;

  return `/${locale}${path}`;
}
