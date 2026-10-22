/**
 * Test harness for the admin Customers router
 * (`server/api/routers/admin/customers.ts`).
 *
 * The router is money and entitlement code. An admin uses it to mint a
 * granted license key, to add credits to an account, to refund a license
 * payment (and optionally revoke the key), to move a customer to a new email,
 * and to read every customer's credits and net Stripe spend.
 *
 * Four collaborators are replaced here with `mock.module`: Better Auth
 * (`src/lib/auth.ts`), the database layer (`src/lib/db-layer.ts`), the Stripe
 * clients (`lib/clients/stripe.ts`: the shared `stripe` and the pinned refund
 * client `stripeRefundApi`), the email service (`lib/services/email.ts`) and
 * nothing else. The license-key generator, the
 * refund flow (`customer-refund.ts`) and the spend reader (`customer-spend.ts`)
 * stay REAL, so the tests run the router the way production does.
 *
 * Mocking at the module boundary is what the repo's CLAUDE.md asks for: the
 * `adminProcedure` middleware still decides who may call a procedure, and no
 * bypass and no test license key ever reaches the source. Every identifier in
 * the fixtures is a made-up string.
 *
 * The mocks are installed when this module is evaluated, and the `load*`
 * helpers are the only way the tests reach the source. A test file that
 * imports the router itself would bind to the real collaborators, so do not.
 */
import { callTRPCProcedure, type AnyRouter } from "@trpc/server";

import type { AccountKeyRow } from "@/src/lib/db-layer";

import { moduleMock, moduleUrl } from "./module-mock";

export type LicenseWithCredits = AccountKeyRow & { credits: number };

export interface ChargeFixture {
  id: string;
  status: string;
  disputed: boolean;
  amount: number;
  amount_refunded: number;
}

/** Everything the source asked its collaborators to do, in call order. */
export const calls = {
  customerPage: [] as Array<{ search?: string; page: number; pageSize: number }>,
  licensesForUserIds: [] as string[][],
  usersByIds: [] as string[][],
  findAccountByKey: [] as string[],
  findAccountById: [] as string[],
  getOrCreateUser: [] as Array<{ email: string; metadata?: { name?: string } }>,
  insertAccountKey: [] as Array<Record<string, unknown>>,
  getCreditBalance: [] as string[],
  grantCreditLot: [] as Array<Record<string, unknown>>,
  refundCreditGrant: [] as Array<Record<string, unknown>>,
  revokeAccountKey: [] as Array<{
    id: string;
    userId: string;
    options: { actingUserId?: string };
  }>,
  getUserById: [] as string[],
  getUserByEmail: [] as string[],
  updateCustomerEmail: [] as Array<{
    userId: string;
    email: string;
    options: { actingUserId?: string };
  }>,
  sendLicenseKey: [] as Array<Record<string, unknown>>,
  listCharges: [] as string[],
  listDisputes: [] as string[],
  retrieveSession: [] as string[],
  createRefund: [] as Array<{ paymentIntent: string; idempotencyKey: string }>,
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  session: null as { user: Record<string, unknown> } | null,
  customerPage: { userIds: [] as string[], totalCustomers: 0, page: 1 },
  licenses: [] as LicenseWithCredits[],
  users: new Map<string, { id: string; email: string }>(),
  /** How many leading `findAccountByKey` probes report a collision. */
  keyCollisions: 0,
  accountsById: new Map<string, AccountKeyRow>(),
  createdUser: { id: "user_new", email: "new@example.com" } as
    | { id: string; email: string }
    | null,
  insertedLicense: undefined as AccountKeyRow | null | undefined,
  creditBalance: 0,
  grantBalance: 0,
  userById: null as { id: string; email: string } | null,
  userByEmail: null as { id: string; email: string } | null,
  updateEmailError: null as unknown,
  customerPageError: null as unknown,
  refundCreditGrantError: null as unknown,
  /** Charges per Stripe customer id. An Error value makes the list throw. */
  charges: new Map<string, ChargeFixture[] | Error>(),
  disputeStatus: new Map<string, string>(),
  /** What `checkout.sessions.retrieve` reports as `payment_intent`. */
  paymentIntent: "pi_1" as string | { id: string } | null,
};

export function accountKeyRow(
  overrides: Partial<AccountKeyRow> = {},
): AccountKeyRow {
  return {
    id: "11111111-1111-4111-8111-111111111111",
    key: "HW-FAKE-0000-0001",
    email: "buyer@example.com",
    userId: "user_1",
    status: "active",
    polarLicenseKeyId: null,
    polarCustomerId: null,
    stripeCustomerId: "cus_1",
    stripeSessionId: "cs_1",
    createdAt: new Date("2026-01-01T00:00:00Z"),
    ...overrides,
  };
}

export function licenseRow(
  overrides: Partial<LicenseWithCredits> = {},
): LicenseWithCredits {
  return { ...accountKeyRow(overrides), credits: 0, ...overrides };
}

export function charge(overrides: Partial<ChargeFixture> = {}): ChargeFixture {
  return {
    id: "ch_1",
    status: "succeeded",
    disputed: false,
    amount: 1_000,
    amount_refunded: 0,
    ...overrides,
  };
}

export function resetHarness(): void {
  for (const list of Object.values(calls)) list.length = 0;

  behaviour.session = null;
  behaviour.customerPage = { userIds: [], totalCustomers: 0, page: 1 };
  behaviour.licenses = [];
  behaviour.users = new Map();
  behaviour.keyCollisions = 0;
  behaviour.accountsById = new Map();
  behaviour.createdUser = { id: "user_new", email: "new@example.com" };
  behaviour.insertedLicense = undefined;
  behaviour.creditBalance = 0;
  behaviour.grantBalance = 0;
  behaviour.userById = null;
  behaviour.userByEmail = null;
  behaviour.updateEmailError = null;
  behaviour.customerPageError = null;
  behaviour.refundCreditGrantError = null;
  behaviour.charges = new Map();
  behaviour.disputeStatus = new Map();
  behaviour.paymentIntent = "pi_1";
}

moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: { api: { getSession: async () => behaviour.session } },
    getPortalSession: async () => null,
  },
});

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    getAccountKeyCustomerPage: async (args: {
      search?: string;
      page: number;
      pageSize: number;
    }) => {
      calls.customerPage.push(args);
      if (behaviour.customerPageError) throw behaviour.customerPageError;
      return behaviour.customerPage;
    },
    getAccountKeysWithCreditsForUserIds: async (userIds: string[]) => {
      calls.licensesForUserIds.push([...userIds]);
      return behaviour.licenses;
    },
    getUsersByIds: async (ids: string[]) => {
      calls.usersByIds.push([...ids]);
      return behaviour.users;
    },
    findAccountByKey: async (key: string) => {
      calls.findAccountByKey.push(key);
      return calls.findAccountByKey.length <= behaviour.keyCollisions
        ? accountKeyRow({ key })
        : null;
    },
    findAccountById: async (id: string) => {
      calls.findAccountById.push(id);
      return behaviour.accountsById.get(id) ?? null;
    },
    getOrCreateUser: async (email: string, metadata?: { name?: string }) => {
      calls.getOrCreateUser.push({ email, metadata });
      return behaviour.createdUser;
    },
    insertAccountKey: async (data: Record<string, unknown>) => {
      calls.insertAccountKey.push(data);
      if (behaviour.insertedLicense !== undefined) return behaviour.insertedLicense;
      return accountKeyRow({
        id: "22222222-2222-4222-8222-222222222222",
        key: data.key as string,
        email: data.email as string,
        userId: data.userId as string,
        status: data.status as string,
        stripeCustomerId: null,
        stripeSessionId: null,
      });
    },
    getCreditBalance: async (userId: string) => {
      calls.getCreditBalance.push(userId);
      return behaviour.creditBalance;
    },
    grantCreditLot: async (data: Record<string, unknown>) => {
      calls.grantCreditLot.push(data);
      return { status: "processed", balance: behaviour.grantBalance };
    },
    refundCreditGrant: async (data: Record<string, unknown>) => {
      calls.refundCreditGrant.push(data);
      if (behaviour.refundCreditGrantError) throw behaviour.refundCreditGrantError;
      return { status: "processed", refundedAmount: 0 };
    },
    revokeAccountKey: async (
      id: string,
      userId: string,
      options: { actingUserId?: string } = {},
    ) => {
      calls.revokeAccountKey.push({ id, userId, options });
    },
    getUserById: async (id: string) => {
      calls.getUserById.push(id);
      return behaviour.userById;
    },
    getUserByEmail: async (email: string) => {
      calls.getUserByEmail.push(email);
      return behaviour.userByEmail;
    },
    updateCustomerEmail: async (
      userId: string,
      email: string,
      options: { actingUserId?: string } = {},
    ) => {
      calls.updateCustomerEmail.push({ userId, email, options });
      if (behaviour.updateEmailError) throw behaviour.updateEmailError;
    },
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      charges: {
        list: (params: { customer: string }) => ({
          autoPagingToArray: async () => {
            calls.listCharges.push(params.customer);
            const answer = behaviour.charges.get(params.customer) ?? [];
            if (answer instanceof Error) throw answer;
            return answer;
          },
        }),
      },
      disputes: {
        list: async (params: { charge: string }) => {
          calls.listDisputes.push(params.charge);
          const status = behaviour.disputeStatus.get(params.charge);
          return { data: status ? [{ status }] : [] };
        },
      },
    },
    // The refund flow's pinned client. The shared `stripe` above has no
    // checkout or refunds, so a refund that ran on it would throw here.
    stripeRefundApi: {
      checkout: {
        sessions: {
          retrieve: async (id: string) => {
            calls.retrieveSession.push(id);
            return { id, payment_intent: behaviour.paymentIntent };
          },
        },
      },
      refunds: {
        create: async (
          params: { payment_intent: string },
          options: { idempotencyKey: string },
        ) => {
          calls.createRefund.push({
            paymentIntent: params.payment_intent,
            idempotencyKey: options.idempotencyKey,
          });
          return { id: "re_1" };
        },
      },
    },
  },
});

moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: {
    emailService: {
      sendLicenseKey: async (data: Record<string, unknown>) => {
        calls.sendLicenseKey.push(data);
      },
    },
  },
});

export const loadTRPC = () => import("@/server/api/trpc");
export const loadCustomersRouter = () =>
  import("@/server/api/routers/admin/customers");

export function adminUser(overrides: Record<string, unknown> = {}) {
  return {
    id: "admin_1",
    email: "admin@example.com",
    name: "Admin",
    role: "admin",
    emailVerified: true,
    createdAt: new Date("2026-01-01T00:00:00Z"),
    updatedAt: new Date("2026-01-01T00:00:00Z"),
    ...overrides,
  };
}

/**
 * Builds the context with the REAL `createTRPCContext`, so `isAdmin` comes
 * from the role the fake session reports, exactly as in production.
 */
export async function contextFor(user: Record<string, unknown> | null) {
  behaviour.session = user ? { user } : null;
  const { createTRPCContext } = await loadTRPC();
  return createTRPCContext({ headers: new Headers({ cookie: "hw-session=x" }) });
}

/**
 * Invokes one procedure of the customers router through tRPC itself, so the
 * real `adminProcedure` middleware and the zod input parser run first.
 */
export async function callCustomers(
  path: string,
  type: "query" | "mutation",
  input: unknown,
  user: Record<string, unknown> | null = adminUser(),
): Promise<unknown> {
  const ctx = await contextFor(user);
  const { customersRouter } = await loadCustomersRouter();
  return callTRPCProcedure({
    router: customersRouter as unknown as AnyRouter,
    path,
    getRawInput: async () => input,
    ctx,
    type,
    signal: undefined,
    batchIndex: 0,
  });
}
