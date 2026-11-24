import { getCloudflareContext } from "@opennextjs/cloudflare";
import { drizzle } from "drizzle-orm/node-postgres";
import { Pool } from "pg";

import * as schema from "./schema";

import { env } from "@/src/env/server.mjs";

export const DATABASE_URL_NOT_SET = "PLANETSCALE_DATABASE_URL is not set";

/**
 * Stands in for the pg pool when no connection string is available (a build
 * with no database, #682, or a test with an empty env). Importing `db` must
 * not throw, because pages import it transitively and the build would fail;
 * every query instead fails with a clear message. Drizzle wraps a failed query
 * in its own "Failed query" error, with this one as its `cause`.
 */
const unconfiguredPool = {
  query: () => {
    throw new Error(DATABASE_URL_NOT_SET);
  },
  connect: () => {
    throw new Error(DATABASE_URL_NOT_SET);
  },
} as unknown as Pool;

function makeDb(connectionString: string | undefined) {
  if (!connectionString) {
    return drizzle(unconfiguredPool, { schema });
  }

  // max 5: a Worker invocation may hold at most 6 outbound connections, and
  // Hyperdrive does the real pooling in front of PlanetScale.
  return drizzle({
    client: new Pool({ connectionString, max: 5 }),
    schema,
  });
}

type Db = ReturnType<typeof makeDb>;

/** Minimal shape of the Cloudflare request context this module reads. */
type RequestContext = {
  env: { HYPERDRIVE?: { connectionString?: string } };
};

/**
 * The OpenNext worker runs every request inside its own AsyncLocalStorage
 * store ({ env, ctx, cf }), a fresh object per request. Keying on it gives
 * each request its own Pool, which Workers require: a socket opened during
 * one request cannot be used by another ("Cannot perform I/O on behalf of a
 * different request"). The store survives into `after()` work, so a task
 * scheduled there reuses the same Pool. The Pool is not ended explicitly;
 * workerd closes its sockets when the invocation finishes.
 */
const perRequest = new WeakMap<object, Db>();

/** Node: `next dev`, `next build`, tests and scripts. One Pool per process. */
let processDb: Db | undefined;

function requestContext(): RequestContext | undefined {
  try {
    // Sync form: returns the request store on Workers, throws when there is
    // none (node tests, scripts, a `next build` prerender).
    return getCloudflareContext() as unknown as RequestContext;
  } catch {
    return undefined;
  }
}

function currentDb(): Db {
  const store = requestContext();

  if (store) {
    let db = perRequest.get(store);

    if (!db) {
      // Hyperdrive in production; the plain URL is a fallback for a Worker
      // run without the binding (e.g. `wrangler dev` with no local string).
      db = makeDb(
        store.env.HYPERDRIVE?.connectionString || env.PLANETSCALE_DATABASE_URL,
      );
      perRequest.set(store, db);
    }

    return db;
  }

  return (processDb ??= makeDb(env.PLANETSCALE_DATABASE_URL));
}

/**
 * The Drizzle client. A lazy proxy, so every existing `db.select(...)`,
 * `db.transaction(...)` and the Better Auth adapter (which receives `db` at
 * module load) resolve the right per-request instance at call time.
 */
export const db: Db = new Proxy({} as Db, {
  get(_target, property) {
    const instance = currentDb();
    const value: unknown = Reflect.get(instance, property, instance);

    return typeof value === "function" ? value.bind(instance) : value;
  },
  has(_target, property) {
    return Reflect.has(currentDb(), property);
  },
});
