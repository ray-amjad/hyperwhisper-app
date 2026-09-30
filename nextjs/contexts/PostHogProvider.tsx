"use client";

import { type ReactNode, useEffect, useRef } from "react";
import posthog from "posthog-js";
import { PostHogProvider as PostHogReactProvider } from "posthog-js/react";
import { env } from "@env/client.mjs";

interface PostHogClientProviderProps {
  children: ReactNode;
}

const DEFAULT_POSTHOG_HOST = "https://us.i.posthog.com";

function PostHogClientProviderInner({ children }: PostHogClientProviderProps) {
  const apiKey = env.NEXT_PUBLIC_POSTHOG_KEY;
  const apiHost = env.NEXT_PUBLIC_POSTHOG_HOST ?? DEFAULT_POSTHOG_HOST;

  const hasInitialisedRef = useRef(false);

  useEffect(() => {
    if (!apiKey) {
      // Skip initialisation when the PostHog key is not configured (e.g. local dev).
      return;
    }

    if (hasInitialisedRef.current && posthog.config.api_host === apiHost) {
      return;
    }

    posthog.init(apiKey, {
      api_host: apiHost,
      person_profiles: "always",
    });

    hasInitialisedRef.current = true;
  }, [apiKey, apiHost]);

  if (!apiKey) {
    return <>{children}</>;
  }

  return (
    <PostHogReactProvider client={posthog}>{children}</PostHogReactProvider>
  );
}

// No Suspense here. This provider wraps every page, and a boundary at this
// level streams each page into a hidden chunk that only a script reveals, so
// with JavaScript off every route painted blank (#1089). The useSearchParams
// callers below it (download, sign-in) need no boundary today, because
// app/layout.tsx and app/[locale]/layout.tsx read headers(), which renders
// every route dynamically. A route that becomes static and calls
// useSearchParams must add its own boundary, or `next build` fails.
export function PostHogClientProvider({
  children,
}: PostHogClientProviderProps) {
  return <PostHogClientProviderInner>{children}</PostHogClientProviderInner>;
}
