/**
 * tRPC Server Configuration for App Router
 *
 * This file sets up:
 * 1. Context creation with Better Auth
 * 2. Three procedure types: public, protected, admin
 * 3. superjson transformer for date/Map/Set serialization
 *
 * ARCHITECTURE NOTES:
 * - Uses App Router pattern with fetchRequestHandler (not Pages Router createNextApiHandler)
 * - Context receives Headers object and checks Better Auth session
 * - Three-tier auth: publicProcedure (anyone), protectedProcedure (logged in), adminProcedure (role === "admin")
 */
import { initTRPC, TRPCError } from "@trpc/server";
import superjson from "superjson";

import type { User as BetterAuthUser } from "better-auth/types";

import { auth } from "@/src/lib/auth";

/**
 * Context passed to every tRPC procedure.
 *
 * @property user - The authenticated Better Auth user, or null if not logged in
 * @property isAdmin - Whether the user is in the admin email whitelist
 * @property headers - Request headers (for rate limiting, IP extraction, etc.)
 */
export interface TRPCContext {
  user: BetterAuthUser | null;
  isAdmin: boolean;
  headers: Headers;
}

/**
 * Creates tRPC context from incoming request headers.
 *
 * FLOW:
 * 1. Gets session from Better Auth using request headers
 * 2. Checks admin status via email whitelist
 * 3. Returns context object for use in all procedures
 *
 * @param opts - Object containing request headers (from App Router)
 * @returns Promise resolving to TRPCContext
 */
export async function createTRPCContext(opts: {
  headers: Headers;
}): Promise<TRPCContext> {
  const session = await auth.api.getSession({ headers: opts.headers });
  const user = session?.user ?? null;
  const isAdmin = user?.role === "admin";

  return { user, isAdmin, headers: opts.headers };
}

/**
 * Initialize tRPC with context type and superjson transformer.
 *
 * superjson enables serialization of:
 * - Date objects
 * - Map/Set
 * - BigInt
 * - undefined (preserved, not converted to null)
 */
const t = initTRPC.context<TRPCContext>().create({
  transformer: superjson,
  errorFormatter({ shape }) {
    return shape;
  },
});

/**
 * Middleware: Ensure user is authenticated.
 *
 * Throws UNAUTHORIZED if no user in context.
 * Narrows ctx.user type from User | null to User.
 */
const isAuthed = t.middleware(({ ctx, next }) => {
  if (!ctx.user) {
    throw new TRPCError({
      code: "UNAUTHORIZED",
      message: "You must be signed in to access this resource",
    });
  }
  return next({
    ctx: {
      ...ctx,
      user: ctx.user, // Now guaranteed non-null
    },
  });
});

/**
 * Middleware: Ensure user is an admin.
 *
 * CHECKS:
 * 1. User must be authenticated
 * 2. User must have role === "admin" in the database
 *
 * Throws UNAUTHORIZED if not logged in, FORBIDDEN if not admin.
 */
const isAdmin = t.middleware(({ ctx, next }) => {
  if (!ctx.user) {
    throw new TRPCError({
      code: "UNAUTHORIZED",
      message: "You must be signed in",
    });
  }
  if (!ctx.isAdmin) {
    throw new TRPCError({
      code: "FORBIDDEN",
      message: "Admin access required",
    });
  }
  return next({
    ctx: {
      ...ctx,
      user: ctx.user,
    },
  });
});

// ============================================================================
// EXPORTS
// ============================================================================

/**
 * Creates a new tRPC router.
 * Use this to define procedure groups (e.g., licenseRouter, adminRouter).
 */
export const createTRPCRouter = t.router;

/**
 * Public procedure - no authentication required.
 * Use for: license validation, checkout URLs, public downloads
 */
export const publicProcedure = t.procedure;

/**
 * Protected procedure - requires authenticated user.
 * Use for: user-specific operations that any logged-in user can perform
 */
export const protectedProcedure = t.procedure.use(isAuthed);

/**
 * Admin procedure - requires admin email.
 * Use for: dashboard stats, customer management, sync operations
 */
export const adminProcedure = t.procedure.use(isAdmin);
