/**
 * Test harness for the authenticated customer portal seam:
 * `server/api/trpc.ts` (the tRPC context and the two authorization
 * middlewares) and `server/api/routers/customer.ts` (the credit arithmetic
 * the dashboard shows and the Stripe billing-portal link).
 *
 * Both files are money and entitlement code. `trpc.ts` decides who may call a
 * procedure at all; `customer.ts` turns pooled credit grants into the balance
 * and the minutes a customer reads.
 *
 * Three collaborators are replaced here with `mock.module`: Better Auth
 * (`src/lib/auth.ts`), the database layer (`src/lib/db-layer.ts`) and the
 * Stripe client (`lib/clients/stripe.ts`). The tests then run the REAL
 * middleware chain and the REAL router — no session server, no database and
 * no Stripe account.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for: the
 * source keeps enforcing entitlement server-side, and no bypass and no test
 * license key ever reaches it. Every identifier in the fixtures is a made-up
 * string, and the harness answers for it only because the fake database was
 * told to.
 *
 * The mocks are installed when this module is evaluated, and the `load*`
 * helpers are the only way the tests reach the source. A test file that
 * imports `server/api/trpc.ts` itself would bind to the real collaborators,
 * so do not.
 */
import { mock } from "node:test";

import { callTRPCProcedure, type AnyRouter } from "@trpc/server";

import type { AccountKeyRow } from "@/src/lib/db-layer";

/** A paid credit grant as `getPaidCreditGrantsForUsers` returns it. */
export interface GrantRow {
  id: string;
  userId: string;
  createdAt: Date;
  expiresAt: Date | null;
  originalAmount: number;
  remainingAmount: number;
  status: string;
}

export interface PortalSessionCall {
  customer: string;
  return_url: string;
}

/** Everything the source asked its collaborators to do, in call order. */
export const calls = {
  getSession: [] as Headers[],
  getAccountKeysByEmail: [] as string[],
  getCreditBalancesForUsers: [] as string[][],
  getPaidCreditGrantsForUsers: [] as string[][],
  portalSessionCreate: [] as PortalSessionCall[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** Session Better Auth reports for the request headers. */
  session: null as { user: Record<string, unknown> } | null,
  /** Account Keys the fake database holds for the queried email. */
  accountKeys: [] as AccountKeyRow[],
  /** Pooled balance per user id. Absent ids read as 0, as the real map does. */
  balances: new Map<string, number>(),
  /** Paid credit grants the fake database holds. */
  grants: [] as GrantRow[],
  /** URL `stripe.billingPortal.sessions.create` hands back. */
  portalUrl: "https://billing.stripe.test/session/bps_1",
};

export function accountKeyRow(
  overrides: Partial<AccountKeyRow> = {},
): AccountKeyRow {
  return {
    id: "key_row_1",
    key: "HW-TEST-0000-0001",
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

export function grantRow(overrides: Partial<GrantRow> = {}): GrantRow {
  return {
    id: "grant_1",
    userId: "user_1",
    createdAt: new Date("2026-01-01T00:00:00Z"),
    expiresAt: new Date("2027-01-01T00:00:00Z"),
    originalAmount: 5_000,
    remainingAmount: 4_200,
    status: "active",
    ...overrides,
  };
}

export function resetHarness(): void {
  calls.getSession.length = 0;
  calls.getAccountKeysByEmail.length = 0;
  calls.getCreditBalancesForUsers.length = 0;
  calls.getPaidCreditGrantsForUsers.length = 0;
  calls.portalSessionCreate.length = 0;

  behaviour.session = null;
  behaviour.accountKeys = [];
  behaviour.balances = new Map();
  behaviour.grants = [];
  behaviour.portalUrl = "https://billing.stripe.test/session/bps_1";
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

moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: {
      api: {
        getSession: async (opts: { headers: Headers }) => {
          calls.getSession.push(opts.headers);
          return behaviour.session;
        },
      },
    },
    getPortalSession: async () => null,
  },
});

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    getAccountKeysByEmail: async (email: string): Promise<AccountKeyRow[]> => {
      calls.getAccountKeysByEmail.push(email);
      return behaviour.accountKeys;
    },
    getCreditBalancesForUsers: async (
      userIds: string[],
    ): Promise<Map<string, number>> => {
      calls.getCreditBalancesForUsers.push([...userIds]);
      // The real reader seeds every requested id with 0 before it sums the
      // grants, so an account with no active grant is present and reads 0.
      const map = new Map<string, number>();
      for (const id of userIds) map.set(id, behaviour.balances.get(id) ?? 0);
      return map;
    },
    getPaidCreditGrantsForUsers: async (
      userIds: string[],
    ): Promise<GrantRow[]> => {
      calls.getPaidCreditGrantsForUsers.push([...userIds]);
      const wanted = new Set(userIds);
      return behaviour.grants.filter((g) => wanted.has(g.userId));
    },
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      billingPortal: {
        sessions: {
          create: async (params: PortalSessionCall) => {
            calls.portalSessionCreate.push(params);
            return { url: behaviour.portalUrl };
          },
        },
      },
    },
  },
});

/**
 * Loads the tRPC configuration AFTER the mocks above are installed, so it
 * binds to them. Tests call these, never `import` the source paths directly.
 */
export const loadTRPC = () => import("@/server/api/trpc");
export const loadCustomerRouter = () => import("@/server/api/routers/customer");

/**
 * Invokes one procedure of the customer router through tRPC itself, so the
 * real `protectedProcedure` middleware chain runs first. This is how the HTTP
 * handler reaches a procedure; the harness only supplies the context.
 */
export async function callCustomer(
  path: string,
  ctx: unknown,
  type: "query" | "mutation" = "query",
  input: unknown = undefined,
): Promise<unknown> {
  const { customerRouter } = await loadCustomerRouter();
  return callTRPCProcedure({
    router: customerRouter as unknown as AnyRouter,
    path,
    getRawInput: async () => input,
    ctx,
    type,
    signal: undefined,
    batchIndex: 0,
  });
}

/** Builds request headers the way the App Router handler passes them in. */
export function requestHeaders(
  entries: Record<string, string> = {},
): Headers {
  return new Headers({ cookie: "hw-session=opaque", ...entries });
}

/** The single billing-portal session Stripe was asked for. Fails loudly otherwise. */
export function onlyPortalSession(): PortalSessionCall {
  if (calls.portalSessionCreate.length !== 1) {
    throw new Error(
      `expected exactly one billing portal session, got ${calls.portalSessionCreate.length}`,
    );
  }
  return calls.portalSessionCreate[0];
}
