/**
 * tRPC Client for React Components
 *
 * Provides typed React hooks for calling tRPC procedures.
 * Uses React Query under the hood for caching and state management.
 *
 * USAGE IN COMPONENTS:
 * ```tsx
 * import { api } from "@/lib/trpc/client";
 *
 * // Query example
 * const { data, isLoading } = api.admin.stats.get.useQuery();
 *
 * // Mutation example
 * const mutation = api.license.validate.useMutation();
 * mutation.mutate({ license_key: "..." });
 * ```
 */
"use client";

import { createTRPCReact } from "@trpc/react-query";

import type { AppRouter } from "@/server/api/root";

/**
 * Typed tRPC React hooks.
 * Provides useQuery, useMutation, useInfiniteQuery, etc.
 */
export const api = createTRPCReact<AppRouter>();
