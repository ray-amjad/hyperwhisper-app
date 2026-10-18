import type { PostHog } from "posthog-js";

import { env } from "@env/client.mjs";
import {
  hasAccountKeyParam,
  redactAccountKeyFromEvent,
  withoutAccountKeyParams,
} from "@/src/lib/account-key-redaction";

const DEFAULT_POSTHOG_HOST = "https://us.i.posthog.com";

let client: Promise<PostHog | null> | undefined;

/**
 * Takes the Account Key out of the address bar, on every page, before PostHog
 * reads it (#1901). `init` stores the page URL as `$initial_current_url` and
 * sends it in the `/flags/` request body, which `before_send` never sees. A
 * URL with no key is left alone: rewriting it would re-encode its query.
 */
export function stripAccountKeyFromAddressBar(): void {
  if (typeof window === "undefined") return;

  const { href } = window.location;

  if (!hasAccountKeyParam(href)) return;

  window.history.replaceState(
    window.history.state,
    "",
    withoutAccountKeyParams(href),
  );
}

/**
 * Loads `posthog-js` in its own chunk and initialises it. The import and the
 * init share one try/catch, so a chunk that fails to load (e.g. an ad-blocker)
 * and an `init` that throws both settle as `null` rather than as a rejection
 * that would stay cached for the rest of the page.
 */
async function importAndInit(apiKey: string): Promise<PostHog | null> {
  try {
    const { default: posthog } = await import("posthog-js");

    stripAccountKeyFromAddressBar();
    posthog.init(apiKey, {
      api_host: env.NEXT_PUBLIC_POSTHOG_HOST ?? DEFAULT_POSTHOG_HOST,
      person_profiles: "always",
      // Unhandled errors and rejections on every page reach error tracking
      // (#739). `before_send` below redacts them like any other event.
      capture_exceptions: true,
      // The Account Key can be in the page URL (#1897): never send it.
      before_send: redactAccountKeyFromEvent,
    });

    return posthog;
  } catch {
    return null;
  }
}

/**
 * The one way into PostHog (#918). `posthog-js` is loaded on first call, in
 * its own chunk, so no route pays for it in its initial bundle; and it is
 * initialised before the promise resolves, so a caller can never capture on
 * an un-initialised instance (posthog-js drops those silently). The promise is
 * memoised, so the page loads and inits PostHog once. `null` when the key is
 * unset, the chunk fails to load, or `init` throws. It never rejects. Client
 * only.
 */
export function loadPostHog(): Promise<PostHog | null> {
  const apiKey = env.NEXT_PUBLIC_POSTHOG_KEY;

  if (!apiKey) return Promise.resolve(null);

  client ??= importAndInit(apiKey);

  return client;
}
