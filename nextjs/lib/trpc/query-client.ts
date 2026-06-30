/**
 * Shared QueryClient Factory
 *
 * Creates a QueryClient with consistent defaults for use in both
 * client-side provider and server-side rendering.
 *
 * CONFIGURATION:
 * - staleTime: 5 minutes - data is considered fresh for this duration
 * - refetchOnWindowFocus: disabled - prevents unexpected refetches
 */
import { QueryClient } from "@tanstack/react-query";

/**
 * Creates a new QueryClient instance with shared defaults.
 *
 * IMPORTANT: Create a new instance per request on the server to avoid
 * sharing state between requests. On the client, use useState to
 * ensure the same instance is used across re-renders.
 *
 * @returns Configured QueryClient instance
 */
export function makeQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Data is fresh for 5 minutes
        staleTime: 5 * 60 * 1000,
        // Don't refetch when window regains focus
        refetchOnWindowFocus: false,
      },
    },
  });
}
