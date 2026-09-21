/**
 * Test harness for the license-key sign-in endpoint
 * (`src/lib/auth-license-key-plugin.ts`).
 *
 * That endpoint is the web half of the entitlement gate: a customer pastes a
 * key and gets a 90-day browser session. It reaches four collaborators —
 * the account-key lookup (`src/lib/db-layer.ts`), the `user` table through
 * Drizzle (`src/db`), better-auth's session adapter, and better-auth's cookie
 * writer. All four are replaced here, so the tests run the REAL endpoint: the
 * real zod body schema, the real `requireHeaders` gate, the real status codes,
 * the real re-read after `createSession`, and the real redirect sanitiser.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for. The
 * source keeps enforcing entitlement server-side, and no test license key and
 * no bypass ever reaches it. Every key string below is made up, and the
 * endpoint answers for it only because the fake database was told to.
 *
 * The mocks are installed when this module is evaluated, and `loadPlugin()` is
 * the only way the tests reach the endpoint. A test file that imports the
 * plugin path itself would bind to the real collaborators, so do not.
 */
import { mock } from "node:test";

import type { AccountKeyRow } from "@/src/lib/db-layer";

export interface SessionRow {
  token: string;
  userId: string;
}

export interface UserRow {
  id: string;
  name: string;
  email: string;
  emailVerified: boolean;
  image: string | null;
  createdAt: Date;
  updatedAt: Date;
  role: string | null;
}

export interface SetSessionCookieCall {
  sessionToken: string;
  userId: string;
}

/** Everything the endpoint asked its collaborators to do, in call order. */
export const calls = {
  findAccountByKey: [] as string[],
  selectUserById: [] as string[],
  createSession: [] as string[],
  deleteSession: [] as string[],
  setSessionCookie: [] as SetSessionCookieCall[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** Account-key rows the fake database holds, keyed by the key string. */
  rows: new Map<string, AccountKeyRow>(),
  /** `user` rows the fake database holds, keyed by `user.id`. */
  users: new Map<string, UserRow>(),
  /**
   * Runs before each `findAccountByKey` answer, with the 1-based call number.
   * A test uses it to change the stored row BETWEEN the first lookup and the
   * re-read, which is how the revocation race is driven.
   */
  beforeLookup: null as ((callNumber: number) => void) | null,
  /** `null` makes the session adapter fail to create a session. */
  createSessionResult: "ok" as "ok" | "null",
};

export function accountKeyRow(
  overrides: Partial<AccountKeyRow> = {},
): AccountKeyRow {
  return {
    id: "key_row_1",
    key: "HW-AAAA-BBBB-CCCC-DDDD",
    email: "buyer@example.com",
    userId: "user_1",
    status: "granted",
    polarLicenseKeyId: null,
    polarCustomerId: null,
    stripeCustomerId: "cus_1",
    stripeSessionId: "cs_1",
    createdAt: new Date("2026-01-01T00:00:00Z"),
    ...overrides,
  };
}

export function userRow(overrides: Partial<UserRow> = {}): UserRow {
  return {
    id: "user_1",
    name: "buyer",
    email: "buyer@example.com",
    emailVerified: true,
    image: null,
    createdAt: new Date("2026-01-01T00:00:00Z"),
    updatedAt: new Date("2026-01-01T00:00:00Z"),
    role: "user",
    ...overrides,
  };
}

/** Puts an account-key row in the fake database under its own `key`. */
export function storeRow(row: AccountKeyRow): AccountKeyRow {
  behaviour.rows.set(row.key, row);
  return row;
}

/** Puts a `user` row in the fake database under its own `id`. */
export function storeUser(row: UserRow): UserRow {
  behaviour.users.set(row.id, row);
  return row;
}

export function resetHarness(): void {
  calls.findAccountByKey.length = 0;
  calls.selectUserById.length = 0;
  calls.createSession.length = 0;
  calls.deleteSession.length = 0;
  calls.setSessionCookie.length = 0;

  behaviour.rows = new Map();
  behaviour.users = new Map();
  behaviour.beforeLookup = null;
  behaviour.createSessionResult = "ok";
}

/**
 * The endpoint logs one audit line on the success path. Swallow it so the run
 * stays readable, and keep the lines so the tests can assert on them — the
 * audit line is the only record of who signed in with which key.
 */
export const logLines: string[] = [];

const realConsole = { log: console.log };

export function silenceEndpointLogging(): void {
  console.log = (...args: unknown[]): void => {
    logLines.push(args.map((a) => String(a)).join(" "));
  };
}

export function restoreEndpointLogging(): void {
  console.log = realConsole.log;
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
    options: {
      namedExports?: Record<string, unknown>;
      defaultExport?: unknown;
    },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    findAccountByKey: async (key: string): Promise<AccountKeyRow | null> => {
      calls.findAccountByKey.push(key);
      behaviour.beforeLookup?.(calls.findAccountByKey.length);
      return behaviour.rows.get(key) ?? null;
    },
  },
});

/**
 * A stand-in for the Drizzle client. The endpoint builds exactly one query:
 * `db.select().from(user).where(eq(user.id, id)).limit(1)`. The fake records
 * the id the real `eq(...)` condition was built with, and answers from
 * `behaviour.users`, so a test can make the account key point at a user row
 * that is not there.
 */
function fakeDb() {
  return {
    select() {
      return {
        from() {
          return {
            where(condition: unknown) {
              const id = readEqValue(condition);
              calls.selectUserById.push(id);
              return {
                limit(n: number) {
                  const row = behaviour.users.get(id);
                  const rows = row ? [row] : [];
                  return Promise.resolve(rows.slice(0, n));
                },
              };
            },
          };
        },
      };
    },
  };
}

/**
 * Pulls the compared value out of a Drizzle `eq(column, value)` condition.
 * The condition is a real `SQL` object, so its `queryChunks` hold the column
 * and a `Param` whose `value` is what the endpoint looked up. Reading it keeps
 * the assertion on the id the endpoint actually queried for, rather than on
 * the fact that some query ran.
 */
function readEqValue(condition: unknown): string {
  const chunks =
    (condition as { queryChunks?: unknown[] } | undefined)?.queryChunks ?? [];

  for (const chunk of chunks) {
    const value = (chunk as { value?: unknown } | undefined)?.value;
    if (typeof value === "string") {
      return value;
    }
  }

  throw new Error(
    "could not read the compared value out of the Drizzle condition",
  );
}

moduleMock.module(moduleUrl("../src/db/index.ts"), {
  namedExports: { db: fakeDb() },
});

moduleMock.module("better-auth/cookies", {
  namedExports: {
    setSessionCookie: async (
      _ctx: unknown,
      payload: { session: SessionRow; user: { id: string } },
    ): Promise<void> => {
      calls.setSessionCookie.push({
        sessionToken: payload.session.token,
        userId: payload.user.id,
      });
    },
  },
});

/**
 * better-auth's session adapter, handed to the endpoint as `ctx.context`.
 * `createSession` mints a token; `deleteSession` records the revocation
 * rollback, which is the endpoint's race guard.
 */
function internalAdapter() {
  return {
    createSession: async (userId: string): Promise<SessionRow | null> => {
      calls.createSession.push(userId);
      if (behaviour.createSessionResult === "null") {
        return null;
      }
      return { token: `session_token_for_${userId}`, userId };
    },
    deleteSession: async (token: string): Promise<void> => {
      calls.deleteSession.push(token);
    },
  };
}

/** Loads the plugin AFTER the mocks above are installed, so it binds to them. */
export const loadPlugin = () => import("@/src/lib/auth-license-key-plugin");

export interface SignInOptions {
  /** Omit to send no `headers` at all, which the endpoint must reject. */
  headers?: Record<string, string> | null;
  callbackURL?: string;
  /** Replaces the whole body, for the malformed-input tests. */
  rawBody?: unknown;
}

export interface SignInResult {
  status: number;
  body: Record<string, unknown>;
}

/**
 * Calls the real endpoint the way better-auth's router does, and reads the
 * status off the `Response` — `asResponse` is what makes `ctx.json(body,
 * { status })` produce a real HTTP status instead of a bare object.
 */
export async function signInWithLicenseKey(
  licenseKey: string,
  options: SignInOptions = {},
): Promise<SignInResult> {
  const { licenseKeyPlugin } = await loadPlugin();
  const endpoint = licenseKeyPlugin().endpoints.signInLicenseKey as unknown as (
    input: Record<string, unknown>,
  ) => Promise<Response>;

  const body =
    "rawBody" in options
      ? options.rawBody
      : {
          licenseKey,
          ...(options.callbackURL === undefined
            ? {}
            : { callbackURL: options.callbackURL }),
        };

  const input: Record<string, unknown> = {
    body,
    context: { internalAdapter: internalAdapter() },
    asResponse: true,
  };

  if (options.headers !== null) {
    input.headers = new Headers(
      options.headers ?? { "user-agent": "HyperWhisper/1.0" },
    );
  }

  const response = await endpoint(input);

  return {
    status: response.status,
    body: (await response.json()) as Record<string, unknown>,
  };
}

/** The rate-limit rule the plugin ships, for the tests that assert on it. */
export async function loadRateLimitRules() {
  const { licenseKeyPlugin } = await loadPlugin();
  return licenseKeyPlugin().rateLimit;
}
