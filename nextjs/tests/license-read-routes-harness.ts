/**
 * Test harness for the four routes that READ (and, in one case, mint) a
 * customer's Account Keys and their pooled credit balance:
 *
 * - `app/api/internal/grant-license`      — mints a key, or hands back the granted one
 * - `app/api/internal/granted-emails`     — bulk read of every granted email
 * - `app/api/internal/licenses-for-email` — read-only key + credit list for one email
 * - `app/api/customer/profile`            — the same list for the signed-in customer
 *
 * Each route reaches two collaborators at import time: the database layer
 * (`src/lib/db-layer.ts`) and, for the profile route, Better Auth
 * (`src/lib/auth.ts`). Both are replaced here with `mock.module`, so the tests
 * run the REAL route modules — the `x-internal-secret` gate, the session gate,
 * the granted/revoked filter, the pooled-credit arithmetic and the reply
 * shapes — with no database, no Redis and no network.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for: the
 * source keeps enforcing entitlement server-side, and no test license key and
 * no bypass ever reaches it. Every key and email in the fixtures is a made-up
 * string, and the harness answers for it only because the fake database was
 * told to.
 *
 * The mocks are installed when this module is evaluated, and the `load*`
 * helpers are the only way the tests reach the routes. A test file that
 * imports a route path itself would bind to the real collaborators, so do not.
 */
import { mock } from "node:test";

import { NextRequest } from "next/server";

import type { AccountKeyRow } from "@/src/lib/db-layer";

/** Everything the routes asked their collaborators to do, in call order. */
export const calls = {
  getAccountKeysByEmail: [] as string[],
  provisionAccountKeyForEmail: [] as string[],
  getCreditBalancesForUsers: [] as string[][],
  getGrantedEmails: 0,
  getSession: 0,
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** Rows the fake database holds for the email under test, newest first. */
  keysByEmail: [] as AccountKeyRow[],
  /** Distinct granted emails the fake database holds. */
  grantedEmails: [] as string[],
  /** Pooled credit balance per user id. A missing id models "no grants". */
  balances: new Map<string, number>(),
  /** The row `provisionAccountKeyForEmail` mints. */
  minted: null as AccountKeyRow | null,
  /** Thrown by `getAccountKeysByEmail` when set — models a database fault. */
  lookupError: null as unknown,
  /** Thrown by `getGrantedEmails` when set. */
  grantedEmailsError: null as unknown,
  /** Thrown by `provisionAccountKeyForEmail` when set. */
  mintError: null as unknown,
  /** The session the profile route sees. `null` models a signed-out caller. */
  session: null as { user: { id: string; email?: string | null } } | null,
};

export function accountKeyRow(
  overrides: Partial<AccountKeyRow> = {},
): AccountKeyRow {
  return {
    id: "key_row_1",
    key: "HW-AAAA-0000-0001",
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

export function resetHarness(): void {
  calls.getAccountKeysByEmail.length = 0;
  calls.provisionAccountKeyForEmail.length = 0;
  calls.getCreditBalancesForUsers.length = 0;
  calls.getGrantedEmails = 0;
  calls.getSession = 0;

  behaviour.keysByEmail = [];
  behaviour.grantedEmails = [];
  behaviour.balances = new Map();
  behaviour.minted = null;
  behaviour.lookupError = null;
  behaviour.grantedEmailsError = null;
  behaviour.mintError = null;
  behaviour.session = null;
}

/**
 * The routes log on their fault branches. Swallow it so the run stays
 * readable, and keep the lines so a failing test can still be diagnosed.
 */
export const logLines: string[] = [];

const realConsole = { error: console.error };

export function silenceRouteLogging(): void {
  console.error = (...args: unknown[]): void => {
    logLines.push(args.map((a) => String(a)).join(" "));
  };
}

export function restoreRouteLogging(): void {
  console.error = realConsole.error;
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

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    getAccountKeysByEmail: async (email: string): Promise<AccountKeyRow[]> => {
      calls.getAccountKeysByEmail.push(email);
      if (behaviour.lookupError) throw behaviour.lookupError;
      return behaviour.keysByEmail;
    },
    provisionAccountKeyForEmail: async (
      email: string,
    ): Promise<AccountKeyRow> => {
      calls.provisionAccountKeyForEmail.push(email);
      if (behaviour.mintError) throw behaviour.mintError;
      return (
        behaviour.minted ?? accountKeyRow({ key: "HW-MINT-0000-0001", email })
      );
    },
    getCreditBalancesForUsers: async (
      userIds: string[],
    ): Promise<Map<string, number>> => {
      calls.getCreditBalancesForUsers.push([...userIds]);
      const map = new Map<string, number>();
      for (const id of userIds) {
        const balance = behaviour.balances.get(id);
        if (balance !== undefined) map.set(id, balance);
      }
      return map;
    },
    getGrantedEmails: async (): Promise<string[]> => {
      calls.getGrantedEmails += 1;
      if (behaviour.grantedEmailsError) throw behaviour.grantedEmailsError;
      return behaviour.grantedEmails;
    },
  },
});

moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: {
      api: {
        getSession: async (): Promise<typeof behaviour.session> => {
          calls.getSession += 1;
          return behaviour.session;
        },
      },
    },
  },
});

/** The secret the tests install in `HYPERWHISPER_INTERNAL_SECRET`. */
export const INTERNAL_SECRET = "harness-internal-secret";

/** Builds a POST request the way the ACS admin backfill sends one. */
export function internalPost(
  path: string,
  body: unknown,
  headers: Record<string, string> = { "x-internal-secret": INTERNAL_SECRET },
): NextRequest {
  return new NextRequest(`https://hyperwhisper.test${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

/** Builds the signed-in customer's GET, cookie and all. */
export function customerGet(cookie = "better-auth.session_token=made-up"): NextRequest {
  return new NextRequest("https://hyperwhisper.test/api/customer/profile", {
    method: "GET",
    headers: { cookie },
  });
}

/**
 * Loads a route module AFTER the mocks above are installed, so it binds to
 * them. Tests call these, never `import` a route path directly.
 */
export const loadGrantLicenseRoute = () =>
  import("@/app/api/internal/grant-license/route");
export const loadGrantedEmailsRoute = () =>
  import("@/app/api/internal/granted-emails/route");
export const loadLicensesForEmailRoute = () =>
  import("@/app/api/internal/licenses-for-email/route");
export const loadCustomerProfileRoute = () =>
  import("@/app/api/customer/profile/route");
