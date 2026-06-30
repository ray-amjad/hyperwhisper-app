/**
 * Root tRPC Router
 *
 * Combines all sub-routers into the main appRouter.
 * Export the AppRouter type for client-side type inference.
 *
 * ROUTER STRUCTURE:
 * - download: getLatestUrl, recordDownload (public)
 * - customer: credits (protected - requires auth)
 * - admin: stats, customers, sync (admin only)
 *
 * NOTE: License validation/activation uses REST endpoints at /api/license/*
 * because the macOS and Windows apps call them directly (can't use tRPC from native code).
 * Credit purchases use the REST endpoint at /api/checkout/credits (redirect flow).
 */
import { createTRPCRouter } from "./trpc";
import { downloadRouter } from "./routers/download";
import { customerRouter } from "./routers/customer";
import { adminRouter } from "./routers/admin";

/**
 * Main application router.
 * All sub-routers are combined here and exposed at /api/trpc/*
 */
export const appRouter = createTRPCRouter({
  download: downloadRouter,
  customer: customerRouter,
  admin: adminRouter,
});

/**
 * Export type definition for client-side type inference.
 * This enables full end-to-end type safety from server to client.
 *
 * Usage in client:
 * ```ts
 * import type { AppRouter } from "@/server/api/root";
 * const api = createTRPCReact<AppRouter>();
 * ```
 */
export type AppRouter = typeof appRouter;
