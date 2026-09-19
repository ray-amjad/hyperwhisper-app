/**
 * Test harness for the two credit REST endpoints:
 * `app/api/checkout/credits` (money in — it creates the Stripe Checkout
 * session) and `app/api/license/credits` (money out — HyperWhisper Cloud reads
 * the balance here and deducts against it).
 *
 * Both routes reach two collaborators at import time: the database layer
 * (`src/lib/db-layer.ts`) and the Stripe client (`lib/clients/stripe.ts`).
 * Both are replaced here with `mock.module`, so the tests run the REAL route
 * modules — the amount validation, the pricing arithmetic, the license-status
 * gate, the Stripe customer resolution and the reply shapes — with no
 * database, no Stripe account and no network.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for: the
 * source keeps enforcing entitlement server-side, and no test license key and
 * no bypass ever reaches it. Every key in the fixtures below is a made-up
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

export interface UpdateAccountKeyCall {
  id: string;
  updates: Record<string, unknown>;
}

export interface DeductCall {
  userId: string;
  amount: number;
}

export interface CustomerListQuery {
  email?: string;
  limit?: number;
}

/** Everything the routes asked their collaborators to do, in call order. */
export const calls = {
  findAccountByKey: [] as string[],
  updateAccountKey: [] as UpdateAccountKeyCall[],
  getCreditBalance: [] as string[],
  deductCreditBalance: [] as DeductCall[],
  customerList: [] as CustomerListQuery[],
  customerCreate: [] as Record<string, unknown>[],
  sessionCreate: [] as Record<string, unknown>[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** Rows the fake database holds, keyed by the exact stored key string. */
  rows: new Map<string, AccountKeyRow>(),
  /** Thrown by `findAccountByKey` when set — models a database fault. */
  lookupError: null as unknown,
  /** Thrown by `deductCreditBalance` when set — models a failed decrement. */
  deductError: null as unknown,
  /** Thrown by `getCreditBalance` when set. */
  balanceError: null as unknown,
  /** Balance `getCreditBalance` reports. */
  creditBalance: 4_200,
  /** Balance left after a successful `deductCreditBalance`. */
  balanceAfterDeduct: 3_200,
  /** Customers `stripe.customers.list` finds for the queried email. */
  existingCustomers: [] as Array<{ id: string }>,
  /** Id `stripe.customers.create` hands back. */
  createdCustomerId: "cus_created",
  /** Thrown by `stripe.checkout.sessions.create` when set. */
  sessionError: null as unknown,
  /** URL the created session carries. `null` models Stripe returning none. */
  sessionUrl: "https://checkout.stripe.test/c/pay/cs_test_1" as string | null,
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

export function resetHarness(): void {
  calls.findAccountByKey.length = 0;
  calls.updateAccountKey.length = 0;
  calls.getCreditBalance.length = 0;
  calls.deductCreditBalance.length = 0;
  calls.customerList.length = 0;
  calls.customerCreate.length = 0;
  calls.sessionCreate.length = 0;

  behaviour.rows = new Map();
  behaviour.lookupError = null;
  behaviour.deductError = null;
  behaviour.balanceError = null;
  behaviour.creditBalance = 4_200;
  behaviour.balanceAfterDeduct = 3_200;
  behaviour.existingCustomers = [];
  behaviour.createdCustomerId = "cus_created";
  behaviour.sessionError = null;
  behaviour.sessionUrl = "https://checkout.stripe.test/c/pay/cs_test_1";
}

/** Puts a row in the fake database under its own `key`. */
export function storeRow(row: AccountKeyRow): AccountKeyRow {
  behaviour.rows.set(row.key, row);
  return row;
}

/**
 * The routes log on their fault branches, and the deduction route logs on its
 * success path too. Swallow it so the run stays readable, and keep the lines
 * so a failing test can still be diagnosed.
 */
export const logLines: string[] = [];

const realConsole = { error: console.error, log: console.log };

export function silenceRouteLogging(): void {
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      logLines.push(`${level} ${args.map((a) => String(a)).join(" ")}`);
    };
  console.error = capture("error");
  console.log = capture("log");
}

export function restoreRouteLogging(): void {
  console.error = realConsole.error;
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
    options: { namedExports: Record<string, unknown> },
  ): void;
}

const moduleMock = mock as unknown as ModuleMocker;

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    findAccountByKey: async (key: string): Promise<AccountKeyRow | null> => {
      calls.findAccountByKey.push(key);
      if (behaviour.lookupError) throw behaviour.lookupError;
      return behaviour.rows.get(key) ?? null;
    },
    updateAccountKey: async (
      id: string,
      updates: Record<string, unknown>,
    ): Promise<void> => {
      calls.updateAccountKey.push({ id, updates });
    },
    getCreditBalance: async (userId: string): Promise<number> => {
      calls.getCreditBalance.push(userId);
      if (behaviour.balanceError) throw behaviour.balanceError;
      return behaviour.creditBalance;
    },
    deductCreditBalance: async (
      userId: string,
      amount: number,
    ): Promise<number> => {
      calls.deductCreditBalance.push({ userId, amount });
      if (behaviour.deductError) throw behaviour.deductError;
      return behaviour.balanceAfterDeduct;
    },
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      customers: {
        list: async (query: CustomerListQuery) => {
          calls.customerList.push(query);
          return { data: behaviour.existingCustomers };
        },
        create: async (params: Record<string, unknown>) => {
          calls.customerCreate.push(params);
          return { id: behaviour.createdCustomerId };
        },
      },
      checkout: {
        sessions: {
          create: async (params: Record<string, unknown>) => {
            calls.sessionCreate.push(params);
            if (behaviour.sessionError) throw behaviour.sessionError;
            return { id: "cs_test_1", url: behaviour.sessionUrl };
          },
        },
      },
    },
  },
});

/** Builds a POST request the way a native app or the web client sends one. */
export function postRequest(
  path: string,
  body: unknown,
  headers: Record<string, string> = {},
): NextRequest {
  return new NextRequest(`https://hyperwhisper.test${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

/** Builds a GET request with the given query string. */
export function getRequest(path: string): NextRequest {
  return new NextRequest(`https://hyperwhisper.test${path}`, { method: "GET" });
}

/** The single session Stripe was asked to create. Fails loudly when absent. */
export function onlySessionCreate(): Record<string, unknown> {
  if (calls.sessionCreate.length !== 1) {
    throw new Error(
      `expected exactly one checkout session, got ${calls.sessionCreate.length}`,
    );
  }
  return calls.sessionCreate[0];
}

/**
 * Loads a route module AFTER the mocks above are installed, so it binds to
 * them. Tests call these, never `import` a route path directly.
 */
export const loadCheckoutCreditsRoute = () =>
  import("@/app/api/checkout/credits/route");
export const loadLicenseCreditsRoute = () =>
  import("@/app/api/license/credits/route");
