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
 * Stripe clients (`lib/clients/stripe.ts`), the mailer
 * (`lib/services/email.ts`), the rate limiters (`lib/rate-limit.ts`, which
 * would otherwise open an Upstash client) and the validated server env
 * (`src/env/server.mjs`, empty: the routers fall back to their defaults). No bypass and no test license key
 * reaches the source; who is signed in comes from the fake session alone, and
 * every identifier in the fixtures is a made-up string.
 *
 * The mocks are installed when this module is evaluated, and `loadRoute` is
 * the only way the tests reach the source. A test file that imports the route
 * itself would bind to the real collaborators, so do not.
 */
import superjson from "superjson";

import { moduleMock, moduleUrl } from "./module-mock";

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
  devicesForLicense: [] as Array<{
    licenseKeyId: string;
    days?: number;
    page?: { limit?: number; offset?: number; search?: string };
  }>,
  stripeCustomersList: [] as Array<Record<string, unknown>>,
  /** Any other db-layer function a test did not expect to be reached. */
  otherDb: [] as string[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  session: null as { user: Record<string, unknown> } | null,
  deviceCounts: [] as DeviceCountRow[],
  devices: [] as DeviceRow[],
  /** The total `getDevicesForLicense` reports; null means `devices.length`. */
  deviceTotal: null as number | null,
  dbError: null as unknown,
  /** The one other db-layer function that throws `dbError` instead of refusing. */
  dbErrorOn: null as string | null,
  stripeCustomers: [] as Array<{ id: string }>,
  stripeError: null as unknown,
  /**
   * Thrown by download.ts's client-IP lookup when set: reaches its outer catch.
   * Not the rate limiter, which fails open since #2136 and so never gets there.
   */
  downloadOuterError: null as unknown,
};

export function resetHarness(): void {
  for (const list of Object.values(calls)) list.length = 0;
  behaviour.session = null;
  behaviour.deviceCounts = [];
  behaviour.devices = [];
  behaviour.deviceTotal = null;
  behaviour.dbError = null;
  behaviour.dbErrorOn = null;
  behaviour.stripeCustomers = [];
  behaviour.stripeError = null;
  behaviour.downloadOuterError = null;
}

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
    // Only a seeded error replaces the alarm: `dbErrorOn` alone must not
    // silence it, nor `throw null`.
    if (name === behaviour.dbErrorOn && behaviour.dbError !== null) {
      throw behaviour.dbError;
    }
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
    DEVICES_PAGE_SIZE: 50,
    getDevicesForLicense: async (
      licenseKeyId: string,
      days?: number,
      page?: { limit?: number; offset?: number; search?: string },
    ) => {
      calls.devicesForLicense.push({ licenseKeyId, days, page });
      if (behaviour.dbError) throw behaviour.dbError;
      return {
        devices: behaviour.devices,
        total: behaviour.deviceTotal ?? behaviour.devices.length,
      };
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
    // The admin customers router's pinned refund client. Never called here.
    stripeRefundApi: {},
  },
});

moduleMock.module(moduleUrl("../lib/services/email.ts"), {
  namedExports: { emailService: {} },
});

// The routers read site URL, support email and the database URL from the
// validated env (#682). None is set here, as none was in process.env before.
moduleMock.module(moduleUrl("../src/env/server.mjs"), {
  namedExports: { env: {} },
});

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    downloadEmailRateLimiter: { limit: async () => ({ success: true }) },
    licenseValidateRateLimiter: { limit: async () => ({ success: true }) },
    latencyIngestRateLimiter: { limit: async () => ({ success: true }) },
  },
});

moduleMock.module(moduleUrl("../server/api/routers/download-ip.ts"), {
  namedExports: {
    getClientIPFromHeaders: () => {
      if (behaviour.downloadOuterError) throw behaviour.downloadOuterError;
      return "203.0.113.1";
    },
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
  /** The response body exactly as it went over the wire. */
  raw: string;
  /** Deserialized `result.data`, when the call succeeded. */
  data?: unknown;
  /** Deserialized `error` shape, when the call failed. */
  error?: {
    message: string;
    code: number;
    data: { code: string; httpStatus: number; path?: string; stack?: string };
  };
}

const ORIGIN = "https://hyperwhisper.test";

async function readResult(res: Response): Promise<TRPCHttpResult> {
  const raw = await res.text();
  const body = JSON.parse(raw) as {
    result?: { data: Parameters<typeof superjson.deserialize>[0] };
    error?: Parameters<typeof superjson.deserialize>[0];
  };
  if (body.error) {
    return {
      status: res.status,
      raw,
      error: superjson.deserialize(body.error) as TRPCHttpResult["error"],
    };
  }
  return {
    status: res.status,
    raw,
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
