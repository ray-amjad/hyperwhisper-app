/**
 * Test harness for the tRPC HTTP entry point (`app/api/trpc/[trpc]/route.ts`)
 * and the router tree it serves (`server/api/root.ts`, including the admin
 * `stats` and `devices` routers).
 *
 * The route is the only door every browser call to `/api/trpc/*` goes
 * through, and the admin namespace behind it reads every customer's devices,
 * license keys and Stripe count. The tests drive the REAL route handler with a
 * real `Request`, so the fetch adapter, the superjson transformer, the real
 * `createTRPCContext`, the real `adminProcedure` / `protectedProcedure`
 * middleware and the zod input parsers all run first.
 *
 * Only the collaborators at the edge are replaced with `mock.module`: Better
 * Auth (`src/lib/auth.ts`), the database layer (`src/lib/db-layer.ts`), the
 * shared Stripe client (`lib/clients/stripe.ts`), the `stripe` package (the
 * admin customers router builds its own client from it at load), the mailer
 * (`lib/services/email.ts`) and the rate limiters (`lib/rate-limit.ts`, which
 * would otherwise open an Upstash client). No bypass and no test license key
 * reaches the source; who is signed in comes from the fake session alone, and
 * every identifier in the fixtures is a made-up string.
 *
 * The mocks are installed when this module is evaluated, and `loadRoute` is
 * the only way the tests reach the source. A test file that imports the route
 * itself would bind to the real collaborators, so do not.
 */
import { createRequire } from "node:module";
import { mock } from "node:test";
import { pathToFileURL } from "node:url";

import superjson from "superjson";

export interface DeviceCountRow {
  licenseKeyId: string;
  email: string;
  licenseKey: string;
  deviceCount: number;
}

export interface DeviceRow {
  deviceId: string;
  deviceName: string | null;
  createdAt: Date;
  lastValidatedAt: Date;
}

/** Everything the source asked its collaborators to do, in call order. */
export const calls = {
  getSession: [] as Headers[],
  deviceCounts: [] as Array<number | undefined>,
  devicesForLicense: [] as Array<{ licenseKeyId: string; days?: number }>,
  stripeCustomersList: [] as Array<Record<string, unknown>>,
  /** Any other db-layer function a test did not expect to be reached. */
  otherDb: [] as string[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  session: null as { user: Record<string, unknown> } | null,
  deviceCounts: [] as DeviceCountRow[],
  devices: [] as DeviceRow[],
  dbError: null as unknown,
  stripeCustomers: [] as Array<{ id: string }>,
  stripeError: null as unknown,
};

export function resetHarness(): void {
  for (const list of Object.values(calls)) list.length = 0;
  behaviour.session = null;
  behaviour.deviceCounts = [];
  behaviour.devices = [];
  behaviour.dbError = null;
  behaviour.stripeCustomers = [];
  behaviour.stripeError = null;
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

moduleMock.module(moduleUrl("../src/lib/auth.ts"), {
  namedExports: {
    auth: {
      api: {
        getSession: async ({ headers }: { headers: Headers }) => {
          calls.getSession.push(headers);
          return behaviour.session;
        },
      },
    },
    getPortalSession: async () => null,
  },
});

/** A db-layer function the admin paths under test must never reach. */
function unexpectedDb(name: string) {
  return async () => {
    calls.otherDb.push(name);
    throw new Error(`unexpected db-layer call: ${name}`);
  };
}

moduleMock.module(moduleUrl("../src/lib/db-layer.ts"), {
  namedExports: {
    getDeviceCountsPerLicense: async (sinceDays?: number) => {
      calls.deviceCounts.push(sinceDays);
      if (behaviour.dbError) throw behaviour.dbError;
      return behaviour.deviceCounts;
    },
    getDevicesForLicense: async (licenseKeyId: string, days?: number) => {
      calls.devicesForLicense.push({ licenseKeyId, days });
      if (behaviour.dbError) throw behaviour.dbError;
      return behaviour.devices;
    },
    ...Object.fromEntries(
      [
        "upsertEmail",
        "getAccountKeysByEmail",
        "getCreditBalancesForUsers",
        "getPaidCreditGrantsForUsers",
        "getAccountKeyCustomerPage",
        "getOrCreateUser",
        "insertAccountKey",
        "findAccountByKey",
        "findAccountById",
        "getCreditBalance",
        "grantCreditLot",
        "refundCreditGrant",
        "revokeAccountKey",
        "getAccountKeysWithCreditsForUserIds",
        "getUserById",
        "getUserByEmail",
        "getUsersByIds",
        "updateCustomerEmail",
      ].map((name) => [name, unexpectedDb(name)]),
    ),
  },
});

moduleMock.module(moduleUrl("../lib/clients/stripe.ts"), {
  namedExports: {
    stripe: {
      customers: {
        list: async (params: Record<string, unknown>) => {
          calls.stripeCustomersList.push(params);
          if (behaviour.stripeError) throw behaviour.stripeError;
          return { data: behaviour.stripeCustomers };
        },
      },
    },
  },
});

/** Stands in for the `stripe` package's default export. Never called here. */
class FakeStripe {}

// nextjs has no `"type": "module"`, so tsx loads the routers as CommonJS and
// `import Stripe from "stripe"` becomes a `require` of the package's CJS entry.
moduleMock.module(
  pathToFileURL(createRequire(import.meta.url).resolve("stripe")).href,
  { defaultExport: FakeStripe },
);

moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: { emailService: {} },
});

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: { limit: async () => ({ success: true }) },
    licenseValidateRateLimiter: { limit: async () => ({ success: true }) },
    latencyIngestRateLimiter: { limit: async () => ({ success: true }) },
  },
});

/**
 * Loads the route through a variable specifier: a static `.ts` path with
 * brackets is awkward to import, and the harness must be the only importer.
 */
const ROUTE_PATH = "../app/api/trpc/[trpc]/route.ts";
export const loadRoute = (): Promise<{
  GET: (req: Request) => Promise<Response>;
  POST: (req: Request) => Promise<Response>;
}> => import(moduleUrl(ROUTE_PATH));

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

export function plainUser(overrides: Record<string, unknown> = {}) {
  return adminUser({
    id: "user_1",
    email: "someone@example.com",
    role: "user",
    ...overrides,
  });
}

export interface TRPCHttpResult {
  status: number;
  /** Deserialized `result.data`, when the call succeeded. */
  data?: unknown;
  /** Deserialized `error` shape, when the call failed. */
  error?: {
    message: string;
    code: number;
    data: { code: string; httpStatus: number; path?: string };
  };
}

const ORIGIN = "https://hyperwhisper.test";

async function readResult(res: Response): Promise<TRPCHttpResult> {
  const body = (await res.json()) as {
    result?: { data: Parameters<typeof superjson.deserialize>[0] };
    error?: Parameters<typeof superjson.deserialize>[0];
  };
  if (body.error) {
    return {
      status: res.status,
      error: superjson.deserialize(body.error) as TRPCHttpResult["error"],
    };
  }
  return {
    status: res.status,
    data: superjson.deserialize(body.result!.data),
  };
}

/** Sends one tRPC query as a real `GET /api/trpc/<path>?input=` request. */
export async function httpQuery(
  path: string,
  input?: unknown,
  user: Record<string, unknown> | null = adminUser(),
): Promise<TRPCHttpResult> {
  behaviour.session = user ? { user } : null;
  const url = new URL(`/api/trpc/${path}`, ORIGIN);
  if (input !== undefined) {
    url.searchParams.set("input", JSON.stringify(superjson.serialize(input)));
  }
  const { GET } = await loadRoute();
  return readResult(
    await GET(new Request(url, { headers: { cookie: "hw-session=abc" } })),
  );
}

/** Sends one tRPC mutation as a real `POST /api/trpc/<path>` request. */
export async function httpMutation(
  path: string,
  input: unknown,
  user: Record<string, unknown> | null = adminUser(),
): Promise<TRPCHttpResult> {
  behaviour.session = user ? { user } : null;
  const { POST } = await loadRoute();
  return readResult(
    await POST(
      new Request(new URL(`/api/trpc/${path}`, ORIGIN), {
        method: "POST",
        headers: { cookie: "hw-session=abc", "content-type": "application/json" },
        body: JSON.stringify(superjson.serialize(input)),
      }),
    ),
  );
}

export const loadRoot = () => import("@/server/api/root");
