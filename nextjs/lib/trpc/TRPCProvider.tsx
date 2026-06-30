/**
 * tRPC React Provider
 *
 * Wraps the app with tRPC and React Query providers.
 * Must be used in a Client Component (has "use client" directive).
 *
 * PROVIDER HIERARCHY:
 * TRPCProvider
 *   └── api.Provider (tRPC client)
 *         └── QueryClientProvider (React Query)
 *               └── children
 *
 * CONFIGURATION:
 * - httpLink: Sends individual HTTP requests per query
 * - loggerLink: Logs requests in development mode
 * - superjson: Enables serialization of Dates, Maps, Sets, etc.
 */
"use client";

import { QueryClientProvider } from "@tanstack/react-query";
import { httpLink, loggerLink } from "@trpc/client";
import { useState } from "react";
import superjson from "superjson";

import { api } from "./client";
import { makeQueryClient } from "./query-client";

/**
 * Gets the base URL for tRPC requests.
 *
 * ENVIRONMENTS:
 * - Browser: Empty string (relative URL /api/trpc)
 * - Vercel SSR: Uses NEXT_PUBLIC_VERCEL_URL
 * - Local dev: Uses localhost with PORT env var
 */
function getBaseUrl(): string {
  if (typeof window !== "undefined") {
    // Browser: use relative URL
    return "";
  }
  if (process.env.NEXT_PUBLIC_VERCEL_URL) {
    // Vercel deployment
    return `https://${process.env.NEXT_PUBLIC_VERCEL_URL}`;
  }
  // Local development
  return `http://localhost:${process.env.PORT ?? 3000}`;
}

/**
 * Props for TRPCProvider component.
 */
interface TRPCProviderProps {
  children: React.ReactNode;
}

/**
 * TRPCProvider wraps the application with tRPC and React Query.
 *
 * USAGE:
 * Wrap your root layout or providers:
 * ```tsx
 * <TRPCProvider>
 *   <YourApp />
 * </TRPCProvider>
 * ```
 */
export function TRPCProvider({ children }: TRPCProviderProps) {
  // useState ensures same instance across re-renders
  const [queryClient] = useState(() => makeQueryClient());

  const [trpcClient] = useState(() =>
    api.createClient({
      // superjson handles Date, Map, Set, undefined serialization
      transformer: superjson,
      links: [
        // Log requests in development
        loggerLink({
          enabled: (opts) =>
            process.env.NODE_ENV === "development" ||
            (opts.direction === "down" && opts.result instanceof Error),
        }),
        // Send individual HTTP requests per query
        httpLink({
          url: `${getBaseUrl()}/api/trpc`,
        }),
      ],
    })
  );

  return (
    <api.Provider client={trpcClient} queryClient={queryClient}>
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    </api.Provider>
  );
}
