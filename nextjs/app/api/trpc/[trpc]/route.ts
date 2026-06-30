/**
 * tRPC HTTP Handler for Next.js App Router
 *
 * This catch-all route handles all tRPC requests at /api/trpc/*
 *
 * KEY DIFFERENCE FROM PAGES ROUTER:
 * - Uses fetchRequestHandler from @trpc/server/adapters/fetch
 * - NOT createNextApiHandler which is for Pages Router
 *
 * REQUEST FLOW:
 * 1. Request comes in to /api/trpc/[procedure.name]
 * 2. fetchRequestHandler parses the procedure path and input
 * 3. createContext creates TRPCContext with Better Auth
 * 4. Router matches procedure and executes with context
 * 5. Response serialized with superjson and returned
 */
import { fetchRequestHandler } from "@trpc/server/adapters/fetch";
import { getHTTPStatusCodeFromError } from "@trpc/server/http";
import { type NextRequest } from "next/server";

import { appRouter } from "@/server/api/root";
import { createTRPCContext } from "@/server/api/trpc";

/**
 * Wraps createTRPCContext to handle incoming HTTP requests.
 *
 * @param req - Next.js request object
 * @returns Promise resolving to TRPCContext
 */
const createContext = async (req: NextRequest) => {
  return createTRPCContext({
    headers: req.headers,
  });
};

/**
 * Main request handler for tRPC.
 *
 * @param req - Next.js App Router request
 * @returns HTTP response with tRPC result
 */
const handler = (req: NextRequest) =>
  fetchRequestHandler({
    endpoint: "/api/trpc",
    req,
    router: appRouter,
    createContext: () => createContext(req),
    // Log server-side errors so production failures (DB outages, unexpected
    // exceptions, Stripe/Polar errors inside mutations) are visible to
    // operators and surfaced to observability sinks.
    //
    // Expected client/auth errors (invalid input, rate limits, unauthenticated
    // requests) map to 4xx status codes and are normal traffic — promoting them
    // to console.error in production would flood observability with user noise
    // and bury real 5xx incidents. So in production we only emit console.error
    // for unexpected 5xx failures; in development we log everything (plus the
    // full stack) for local debugging.
    onError: ({ path, error, type }) => {
      const isDev = process.env.NODE_ENV === "development";
      const httpStatus = getHTTPStatusCodeFromError(error);
      const isServerError = httpStatus >= 500;

      if (isServerError || isDev) {
        const message = `tRPC failed on ${type} ${path ?? "<no-path>"}: ${error.code} - ${error.message}`;
        if (isServerError) {
          console.error(message);
        } else {
          // Expected 4xx in development — keep visible but not as an error.
          console.debug(message);
        }
      }

      if (isDev && error.stack) {
        console.error(error.stack);
      }
    },
  });

// Export handlers for both GET and POST methods
// tRPC uses GET for queries, POST for mutations
export { handler as GET, handler as POST };
