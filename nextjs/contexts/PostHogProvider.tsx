"use client";

import { type ReactNode, useEffect } from "react";

import { loadPostHog } from "@/src/lib/posthog-client";

interface PostHogClientProviderProps {
  children: ReactNode;
}

// No static PostHog import and no posthog-js/react provider: either one puts
// the whole PostHog core in every route's initial bundle (#918). The core is
// loaded and initialised after hydration, in its own chunk.
function PostHogClientProviderInner({ children }: PostHogClientProviderProps) {
  useEffect(() => {
    void loadPostHog();
  }, []);

  return <>{children}</>;
}

// No Suspense here. This provider wraps every page, and a boundary at this
// level streams each page into a hidden chunk that only a script reveals, so
// with JavaScript off every route painted blank (#1089). The 2 useSearchParams
// callers with no boundary of their own (download, sign-in) render
// dynamically, because the layouts read headers() and neither page sets
// `dynamic = "force-static"`. A static route (blog, latency and
// choosing-a-model are) or shared chrome that calls useSearchParams must add
// its own Suspense boundary, or `next build` fails.
export function PostHogClientProvider({
  children,
}: PostHogClientProviderProps) {
  return <PostHogClientProviderInner>{children}</PostHogClientProviderInner>;
}
