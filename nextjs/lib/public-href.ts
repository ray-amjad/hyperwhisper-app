import { SITE_URL } from "@/lib/site-metadata";

/** The apex host. It 301s to www; never emit it as a crawlable site URL. */
export const APEX_SITE_URL = "https://hyperwhisper.com";

/**
 * Turn a markdown-link href from the catalogues into the URL a crawler should
 * follow: locale-prefix internal legal paths, and www for the apex host.
 */
export function rewritePublicHref(url: string, locale: string): string {
  if (
    url.startsWith("/legal/") ||
    url === "/download" ||
    url.startsWith("/download?")
  ) {
    return `/${locale}${url}`;
  }
  if (url === APEX_SITE_URL || url === `${APEX_SITE_URL}/`) return SITE_URL;
  if (url.startsWith(`${APEX_SITE_URL}/`)) {
    return `${SITE_URL}${url.slice(APEX_SITE_URL.length)}`;
  }

  return url;
}

export function isOffSiteHref(url: string): boolean {
  if (!url.startsWith("http://") && !url.startsWith("https://")) return false;

  return !url.startsWith(SITE_URL) && !url.startsWith(APEX_SITE_URL);
}
