/**
 * Test harness for the two internal latency routes:
 * `app/api/internal/latency` (the edge transcription service writes one
 * anonymous row per provider attempt) and `app/api/internal/latency/prune`
 * (the daily Vercel cron deletes rows past the 1-year retention the public
 * privacy page promises).
 *
 * Both routes reach three collaborators at import time: the Drizzle client
 * (`src/db/index.ts`), the Upstash-backed rate limiter (`lib/rate-limit.ts`)
 * and, for the prune route, the validated server env (`src/env/server.mjs`).
 * All three are replaced here with `mock.module`, so the tests run the REAL
 * route modules — the secret gate, the body-size guard, the batch validation,
 * the bucketing, the coarse timestamp, the batched delete loop and the reply
 * shapes — with no database, no Redis and no network.
 *
 * Every secret below is a made-up string. The routes accept it only because
 * the fake env was told to hold it.
 *
 * The mocks are installed when this module is evaluated, and the `load*`
 * helpers are the only way the tests reach the routes. A test file that
 * imports a route path itself would bind to the real collaborators, so do not.
 */
import { mock } from "node:test";

import { PgDialect } from "drizzle-orm/pg-core";
import type { SQL } from "drizzle-orm";
import { NextRequest } from "next/server";

/** Everything the routes asked their collaborators to do, in call order. */
export const calls = {
  /** Rows handed to `db.insert(...).values(...)`, one entry per insert. */
  inserts: [] as Array<{ table: unknown; rows: Array<Record<string, unknown>> }>,
  /** Rendered `where` clause of each `db.delete(...)`, one entry per batch. */
  deletes: [] as Array<{ table: unknown; sql: string; params: unknown[] }>,
  /** Identifier each `latencyIngestRateLimiter.limit` call was keyed by. */
  rateLimit: [] as string[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** What the rate limiter answers. */
  rateLimitSuccess: true,
  /** Thrown by `insert().values()` when set — models a database fault. */
  insertError: null as unknown,
  /** Thrown by `delete().where()` when set — models a database fault. */
  deleteError: null as unknown,
  /**
   * `rowCount` each successive delete batch reports. When the list runs out,
   * the fake reports 0. `undefined` inside the list models a driver that
   * leaves `rowCount` off.
   */
  deleteRowCounts: [] as Array<number | undefined>,
};

/**
 * The fake validated env the prune route reads. The route reads it per
 * request, so a test may change it between calls.
 */
export const fakeEnv: Record<string, string | undefined> = {};

export function resetHarness(): void {
  calls.inserts.length = 0;
  calls.deletes.length = 0;
  calls.rateLimit.length = 0;

  behaviour.rateLimitSuccess = true;
  behaviour.insertError = null;
  behaviour.deleteError = null;
  behaviour.deleteRowCounts = [];

  for (const key of Object.keys(fakeEnv)) delete fakeEnv[key];
}

/**
 * The routes log on their fault branches. Swallow it so the run stays
 * readable, and keep the lines so a test can assert on what was logged.
 */
export const logLines: Array<{ level: string; args: unknown[] }> = [];

const realConsole = { error: console.error, warn: console.warn };

export function silenceRouteLogging(): void {
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      logLines.push({ level, args });
    };
  console.error = capture("error");
  console.warn = capture("warn");
}

export function restoreRouteLogging(): void {
  console.error = realConsole.error;
  console.warn = realConsole.warn;
}

function moduleUrl(relative: string): string {
  return new URL(relative, import.meta.url).href;
}

/**
 * `mock.module` is a real Node 22 API (behind `--experimental-test-module-mocks`,
 * which `npm test` passes) but this repo pins `@types/node@20`, which has no
 * declaration for it. Narrow the tracker to the one method used here rather
 * than bumping the types in a test-only change.
 */
interface ModuleMocker {
  module(
    specifier: string,
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

/**
 * Renders a Drizzle `where` clause the way Postgres would receive it, so the
 * tests can read the real cutoff and batch size the route put into it.
 */
const dialect = new PgDialect();

moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: {
    db: {
      insert: (table: unknown) => ({
        values: async (rows: Array<Record<string, unknown>>) => {
          calls.inserts.push({ table, rows });
          if (behaviour.insertError) throw behaviour.insertError;
          return { rowCount: rows.length };
        },
      }),
      delete: (table: unknown) => ({
        where: async (condition: SQL) => {
          const query = dialect.sqlToQuery(condition);
          calls.deletes.push({ table, sql: query.sql, params: query.params });
          if (behaviour.deleteError) throw behaviour.deleteError;
          const next = behaviour.deleteRowCounts.shift();
          return { rowCount: next };
        },
      }),
    },
  },
});

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    latencyIngestRateLimiter: {
      limit: async (identifier: string) => {
        calls.rateLimit.push(identifier);
        return { success: behaviour.rateLimitSuccess };
      },
    },
  },
});

moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: fakeEnv },
});

const BASE = "https://hyperwhisper.test";

/** Builds a POST the way the edge service sends a latency batch. */
export function postRequest(
  path: string,
  body: unknown,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest(`${BASE}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

/** Builds a GET the way the Vercel cron (or a manual run) sends one. */
export function getRequest(
  path: string,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest(`${BASE}${path}`, { method: "GET", headers });
}

/**
 * Loads a route module AFTER the mocks above are installed, so it binds to
 * them. Tests call these, never `import` a route path directly.
 */
export const loadLatencyIngestRoute = () =>
  import("@/app/api/internal/latency/route");
export const loadLatencyPruneRoute = () =>
  import("@/app/api/internal/latency/prune/route");
export const loadLatencySchema = () =>
  import("@/src/db/schema/stt-latency-samples");
export const loadKnownProviders = () => import("@/lib/latency/providers");
