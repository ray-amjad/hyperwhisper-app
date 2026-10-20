import { drizzle } from "drizzle-orm/node-postgres";
import type { Pool } from "pg";
import * as schema from "./schema";
import { env } from "@/src/env/server.mjs";

export const DATABASE_URL_NOT_SET = "PLANETSCALE_DATABASE_URL is not set";

/**
 * Stands in for the pg pool when PLANETSCALE_DATABASE_URL is unset (Vercel
 * Preview has no database, #682). Importing `db` must not throw, because pages
 * import it transitively and a Preview build would fail; every query instead
 * fails with a clear message. Drizzle wraps a failed query in its own
 * "Failed query" error, with this one as its `cause`.
 */
const unconfiguredPool = {
  query: () => {
    throw new Error(DATABASE_URL_NOT_SET);
  },
  connect: () => {
    throw new Error(DATABASE_URL_NOT_SET);
  },
} as unknown as Pool;

export const db = env.PLANETSCALE_DATABASE_URL
  ? drizzle(env.PLANETSCALE_DATABASE_URL, { schema })
  : drizzle(unconfiguredPool, { schema });
