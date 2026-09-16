/**
 * Test harness for `lib/services/stripe-webhook.ts`.
 *
 * The webhook module reaches four collaborators at import time: the Stripe
 * client, the email service, the license-key generator and the database
 * layer. Every one of them is replaced here with `mock.module`, so the tests
 * run the REAL webhook module — its idempotency guards, its refund arithmetic
 * and its pooling rule — with no database, no network and no production
 * change. Mocking at the module boundary is what the repo's CLAUDE.md asks
 * for: no test license key and no client-side bypass ever reaches the source.
 *
 * The mocks are installed when this module is evaluated, and `loadWebhook()`
 * is the only way the tests reach the source. A test file that imports the
 * webhook path itself would bind to the real collaborators, so do not.
 */
import { mock } from "node:test";

import type { AccountKeyInsert, AccountKeyRow } from "@/src/lib/db-layer";

export interface SentEmail {
  kind: "license" | "mint" | "topup";
  payload: Record<string, unknown>;
}

export interface GrantForEventArgs {
  eventId: string;
  eventType: string;
  stripeObjectId: string;
  userId: string;
  creditAmount: number;
  sourceType: string;
  sourceId: string;
}

export interface GrantLotArgs {
  userId: string;
  amount: number;
  sourceType: string;
  sourceId: string;
}

export interface RefundArgs {
  sourceType: string;
  sourceId: string;
}

/** Everything the webhook module did, in call order. */
export const calls = {
  findAccountByKey: [] as string[],
  findAccountByStripeSession: [] as string[],
  getAccountKeysByEmail: [] as string[],
  insertAccountKey: [] as AccountKeyInsert[],
  getOrCreateUser: [] as Array<{ email: string; data: unknown }>,
  revokeAccountKey: [] as Array<{ id: string; userId: string }>,
  revokeWebAccess: [] as string[],
  grantCreditLot: [] as GrantLotArgs[],
  grantCreditsForStripeEvent: [] as GrantForEventArgs[],
  refundCreditGrant: [] as RefundArgs[],
  getCreditBalance: [] as string[],
  generateLicenseKey: 0,
  emails: [] as SentEmail[],
  stripeSessionQueries: [] as Array<{ payment_intent?: string; limit?: number }>,
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** Keys the generator hands out, in order. Falls back to a unique key. */
  generatedKeys: [] as string[],
  /** Keys that `findAccountByKey` reports as already taken. */
  takenKeys: new Set<string>(),
  bySession: new Map<string, AccountKeyRow>(),
  byEmail: new Map<string, AccountKeyRow[]>(),
  user: { id: "user_1" } as { id: string } | null,
  insertError: null as unknown,
  /** Row `insertAccountKey` returns. `null` models a failed insert. */
  insertedRow: null as AccountKeyRow | null,
  grantLotError: null as unknown,
  grantForEventResult: "processed" as "processed" | "duplicate",
  creditBalance: 12_345,
  refundResult: { status: "processed", refundedAmount: 500 } as {
    status: "processed" | "duplicate";
    refundedAmount: number;
  },
  stripeSessions: [] as unknown[],
  emailSuccess: true,
};

export function accountKeyRow(overrides: Partial<AccountKeyRow> = {}): AccountKeyRow {
  return {
    id: "key_row_1",
    key: "HW-AAAA-BBBB-CCCC",
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
  calls.findAccountByStripeSession.length = 0;
  calls.getAccountKeysByEmail.length = 0;
  calls.insertAccountKey.length = 0;
  calls.getOrCreateUser.length = 0;
  calls.revokeAccountKey.length = 0;
  calls.revokeWebAccess.length = 0;
  calls.grantCreditLot.length = 0;
  calls.grantCreditsForStripeEvent.length = 0;
  calls.refundCreditGrant.length = 0;
  calls.getCreditBalance.length = 0;
  calls.emails.length = 0;
  calls.stripeSessionQueries.length = 0;
  calls.generateLicenseKey = 0;

  behaviour.generatedKeys = [];
  behaviour.takenKeys = new Set();
  behaviour.bySession = new Map();
  behaviour.byEmail = new Map();
  behaviour.user = { id: "user_1" };
  behaviour.insertError = null;
  behaviour.insertedRow = null;
  behaviour.grantLotError = null;
  behaviour.grantForEventResult = "processed";
  behaviour.creditBalance = 12_345;
  behaviour.refundResult = { status: "processed", refundedAmount: 500 };
  behaviour.stripeSessions = [];
  behaviour.emailSuccess = true;
}

/**
 * The webhook module logs on every branch. Swallow it so a 30-test run stays
 * readable, and keep the lines so a failing test can still be diagnosed.
 */
export const logLines: string[] = [];

const realConsole = {
  log: console.log,
  warn: console.warn,
  error: console.error,
};

export function silenceWebhookLogging(): void {
  const capture =
    (level: string) =>
    (...args: unknown[]): void => {
      logLines.push(`${level} ${args.map((a) => String(a)).join(" ")}`);
    };
  console.log = capture("log");
  console.warn = capture("warn");
  console.error = capture("error");
}

export function restoreWebhookLogging(): void {
  console.log = realConsole.log;
  console.warn = realConsole.warn;
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

const emailOutcome = () =>
  behaviour.emailSuccess
    ? { success: true }
    : { success: false, error: "resend refused the message" };

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    findAccountByKey: async (key: string): Promise<AccountKeyRow | null> => {
      calls.findAccountByKey.push(key);
      if (behaviour.takenKeys.has(key)) return accountKeyRow({ key });
      const pooled: AccountKeyRow[] = [];
      behaviour.byEmail.forEach((rows) => pooled.push(...rows));
      const hit = pooled.find((row) => row.key === key);
      if (hit) return hit;
      return null;
    },
    findAccountByStripeSession: async (sessionId: string): Promise<AccountKeyRow | null> => {
      calls.findAccountByStripeSession.push(sessionId);
      return behaviour.bySession.get(sessionId) ?? null;
    },
    getAccountKeysByEmail: async (email: string): Promise<AccountKeyRow[]> => {
      calls.getAccountKeysByEmail.push(email);
      return behaviour.byEmail.get(email) ?? [];
    },
    insertAccountKey: async (data: AccountKeyInsert): Promise<AccountKeyRow | null> => {
      calls.insertAccountKey.push(data);
      if (behaviour.insertError) throw behaviour.insertError;
      return (
        behaviour.insertedRow ??
        accountKeyRow({
          key: data.key,
          email: data.email,
          userId: data.userId,
          status: data.status ?? "granted",
          stripeSessionId: data.stripeSessionId ?? null,
          stripeCustomerId: data.stripeCustomerId ?? null,
        })
      );
    },
    getOrCreateUser: async (email: string, data: unknown) => {
      calls.getOrCreateUser.push({ email, data });
      return behaviour.user;
    },
    revokeAccountKey: async (id: string, userId: string): Promise<void> => {
      calls.revokeAccountKey.push({ id, userId });
    },
    revokeWebAccess: async (userId: string): Promise<void> => {
      calls.revokeWebAccess.push(userId);
    },
    grantCreditLot: async (args: GrantLotArgs) => {
      calls.grantCreditLot.push(args);
      if (behaviour.grantLotError) throw behaviour.grantLotError;
      return { status: "processed" as const, balance: behaviour.creditBalance };
    },
    grantCreditsForStripeEvent: async (args: GrantForEventArgs) => {
      calls.grantCreditsForStripeEvent.push(args);
      return behaviour.grantForEventResult;
    },
    refundCreditGrant: async (args: RefundArgs) => {
      calls.refundCreditGrant.push(args);
      return behaviour.refundResult;
    },
    getCreditBalance: async (userId: string): Promise<number> => {
      calls.getCreditBalance.push(userId);
      return behaviour.creditBalance;
    },
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      checkout: {
        sessions: {
          list: async (query: { payment_intent?: string; limit?: number }) => {
            calls.stripeSessionQueries.push(query);
            return { data: behaviour.stripeSessions };
          },
        },
      },
    },
  },
});

moduleMock.module(moduleUrl("../lib/services/license-key.ts"), {
  namedExports: {
    generateLicenseKey: (): string => {
      const next = behaviour.generatedKeys[calls.generateLicenseKey];
      calls.generateLicenseKey += 1;
      return next ?? `HW-GEN-${calls.generateLicenseKey}`;
    },
  },
});

moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendLicenseKey: async (payload: Record<string, unknown>) => {
        calls.emails.push({ kind: "license", payload });
        return emailOutcome();
      },
      sendCreditMint: async (payload: Record<string, unknown>) => {
        calls.emails.push({ kind: "mint", payload });
        return emailOutcome();
      },
      sendCreditTopUp: async (payload: Record<string, unknown>) => {
        calls.emails.push({ kind: "topup", payload });
        return emailOutcome();
      },
    },
  },
});

/**
 * Loads the module under test AFTER the mocks above are installed, so it binds
 * to them. Tests call this, never `import` the source path directly.
 */
export const loadWebhook = () => import("@/lib/services/stripe-webhook");
