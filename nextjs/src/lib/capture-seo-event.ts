import { loadPostHog } from "@/src/lib/posthog-client";
import { isSupportedLocale } from "@/src/i18n/locales";

/**
 * Where a download CTA was clicked. Keep this list short and stable: these
 * names are PostHog breakdowns for organic → download.
 */
export type DownloadCtaLocation =
  | "home_hero"
  | "home_closing"
  | "home_pricing"
  | "header"
  | "footer"
  | "blog_post"
  | "download_page"
  | "not_found";

export type DownloadCtaPlatform = "mac" | "windows" | "linux";

function localeFromPathname(pathname: string): string {
  const segment = pathname.split("/")[1] ?? "";

  return isSupportedLocale(segment) ? segment : "en";
}

function pageContext(): { path: string; locale: string } {
  if (typeof window === "undefined") return { path: "", locale: "en" };

  const path = window.location.pathname;

  return { path, locale: localeFromPathname(path) };
}

/**
 * `download_clicked` on a CTA or installer control. Never throws: PostHog can
 * be unset, blocked, or stubbed, and a failed capture must not change the
 * click. No email, Account Key, or other personal data.
 *
 * `signup_completed` is deliberately not fired. First-time magic-link verify
 * is only known server-side (`verifyContextsThatCreatedAUser` in
 * `src/lib/auth.ts`). The one client-visible signal is Better Auth's
 * `newUserCallbackURL`, which this app does not send — sending it would
 * change the redirect for new users. `user.createdAt` vs request time is a
 * clock guess (same file). License-key sign-in is for an existing key.
 */
export function captureDownloadClicked({
  cta,
  platform,
}: {
  cta: DownloadCtaLocation;
  platform?: DownloadCtaPlatform | null;
}): Promise<void> {
  return capture("download_clicked", {
    cta,
    ...(platform ? { platform } : {}),
    ...pageContext(),
  });
}

async function capture(
  event: string,
  properties: Record<string, string>,
): Promise<void> {
  try {
    const posthog = await loadPostHog();

    posthog?.capture(event, properties);
  } catch {
    // An ad-blocker can stub `capture` with a thrower.
  }
}
