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

const OWN_HOSTS = new Set([
  new URL(SITE_URL).host,
  new URL(APEX_SITE_URL).host,
]);

/**
 * True for an absolute http(s) URL on another host. Compares the parsed host,
 * not a string prefix, so `https://www.hyperwhisper.com.example.com` is off-site.
 */
export function isOffSiteHref(url: string): boolean {
  if (!url.startsWith("http://") && !url.startsWith("https://")) return false;

  try {
    return !OWN_HOSTS.has(new URL(url).host);
  } catch {
    return true;
  }
}
