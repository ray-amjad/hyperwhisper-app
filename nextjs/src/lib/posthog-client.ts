import type { PostHog } from "posthog-js";

import { env } from "@env/client.mjs";

const DEFAULT_POSTHOG_HOST = "https://us.i.posthog.com";

let client: Promise<PostHog | null> | undefined;

/**
 * The one way into PostHog (#918). `posthog-js` is loaded on first call, in
 * its own chunk, so no route pays for it in its initial bundle; and it is
 * initialised in the same promise, so a caller can never capture on an
 * un-initialised instance (posthog-js drops those silently). `null` when the
 * key is unset or the chunk fails to load (e.g. an ad-blocker). Client only.
 */
export function loadPostHog(): Promise<PostHog | null> {
  const apiKey = env.NEXT_PUBLIC_POSTHOG_KEY;

  if (!apiKey) return Promise.resolve(null);

  client ??= import("posthog-js").then(
    ({ default: posthog }) => {
      posthog.init(apiKey, {
        api_host: env.NEXT_PUBLIC_POSTHOG_HOST ?? DEFAULT_POSTHOG_HOST,
        person_profiles: "always",
      });

      return posthog;
    },
    () => null,
  );

  return client;
}
