/**
 * Server-side tRPC Caller
 *
 * Allows calling tRPC procedures directly from Server Components
 * and Server Actions without HTTP overhead.
 *
 * BENEFITS:
 * - No network round-trip for server-to-server calls
 * - Full type safety maintained
 * - Access to same context (Better Auth, etc.)
 *
 * USAGE IN SERVER COMPONENT:
 * ```tsx
 * import { createServerCaller } from "@/lib/trpc/server";
 *
 * export default async function AdminPage() {
 *   const api = await createServerCaller();
 *   const stats = await api.admin.stats.get();
 *   return <Dashboard stats={stats} />;
 * }
 * ```
 */
import { headers } from "next/headers";

import { appRouter } from "@/server/api/root";
import { createTRPCContext } from "@/server/api/trpc";

/**
 * Creates a server-side tRPC caller.
 *
 * FLOW:
 * 1. Gets request headers from Next.js
 * 2. Creates TRPCContext (same as HTTP handler uses)
 * 3. Returns caller bound to appRouter with context
 *
 * @returns Promise resolving to typed tRPC caller
 */
export async function createServerCaller() {
  const headersObj = await headers();
  const ctx = await createTRPCContext({ headers: headersObj });

  // Create caller - enables direct procedure calls without HTTP
  return appRouter.createCaller(ctx);
}
