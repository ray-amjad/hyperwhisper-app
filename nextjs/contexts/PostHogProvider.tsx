"use client";

import { Suspense, type ReactNode, useEffect, useRef } from "react";
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

export function PostHogClientProvider({
  children,
}: PostHogClientProviderProps) {
  return (
    <Suspense fallback={null}>
      <PostHogClientProviderInner>{children}</PostHogClientProviderInner>
    </Suspense>
  );
}
