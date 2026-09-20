/**
 * Test harness for the three public license endpoints:
 * `app/api/license/validate`, `app/api/license/activate` and
 * `app/api/license/deactivate`.
 *
 * Those routes reach two collaborators at import time: the database layer
 * (`src/lib/db-layer.ts`) and the Upstash-backed rate limiter
 * (`lib/rate-limit.ts`). Both are replaced here with `mock.module`, so the
 * tests run the REAL route modules — the rate-limit gate, the body parsing,
 * the entitlement check in `src/lib/license-validation.ts` and the reply
 * shapes — with no database, no Redis and no network.
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

export interface DeviceValidationCall {
  licenseKeyId: string;
  deviceId: string;
  deviceName?: string;
}

/** Everything the routes asked their collaborators to do, in call order. */
export const calls = {
  rateLimit: [] as string[],
  findAccountByKey: [] as string[],
  upsertDeviceValidation: [] as DeviceValidationCall[],
  getCreditBalance: [] as string[],
};

/** What the collaborators answer. Each test sets only what it cares about. */
export const behaviour = {
  /** `false` makes the limiter reject the next request. */
  rateLimitSuccess: true,
  /** Rows the fake database holds, keyed by the exact stored key string. */
  rows: new Map<string, AccountKeyRow>(),
  /** Thrown by `findAccountByKey` when set — models a database fault. */
  lookupError: null as unknown,
  /** Thrown by `upsertDeviceValidation` when set. */
  deviceTrackingError: null as unknown,
  creditBalance: 4_200,
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
  calls.rateLimit.length = 0;
  calls.findAccountByKey.length = 0;
  calls.upsertDeviceValidation.length = 0;
  calls.getCreditBalance.length = 0;

  behaviour.rateLimitSuccess = true;
  behaviour.rows = new Map();
  behaviour.lookupError = null;
  behaviour.deviceTrackingError = null;
  behaviour.creditBalance = 4_200;
}

/** Puts a row in the fake database under its own `key`. */
export function storeRow(row: AccountKeyRow): AccountKeyRow {
  behaviour.rows.set(row.key, row);
  return row;
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
    findAccountByKey: async (key: string): Promise<AccountKeyRow | null> => {
      calls.findAccountByKey.push(key);
      if (behaviour.lookupError) throw behaviour.lookupError;
      return behaviour.rows.get(key) ?? null;
    },
    upsertDeviceValidation: async (
      licenseKeyId: string,
      deviceId: string,
      deviceName?: string,
    ): Promise<void> => {
      calls.upsertDeviceValidation.push({ licenseKeyId, deviceId, deviceName });
      if (behaviour.deviceTrackingError) throw behaviour.deviceTrackingError;
    },
    getCreditBalance: async (userId: string): Promise<number> => {
      calls.getCreditBalance.push(userId);
      return behaviour.creditBalance;
    },
  },
});

moduleMock.module(moduleUrl("../lib/rate-limit.ts"), {
  namedExports: {
    licenseValidateRateLimiter: {
      limit: async (identifier: string) => {
        calls.rateLimit.push(identifier);
        return { success: behaviour.rateLimitSuccess };
      },
    },
  },
});

/** Builds a POST request the way a native app sends one. */
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

/**
 * Loads a route module AFTER the mocks above are installed, so it binds to
 * them. Tests call these, never `import` a route path directly.
 */
export const loadValidateRoute = () =>
  import("@/app/api/license/validate/route");
export const loadActivateRoute = () =>
  import("@/app/api/license/activate/route");
export const loadDeactivateRoute = () =>
  import("@/app/api/license/deactivate/route");
export const loadAccountValidateRoute = () =>
  import("@/app/api/account/validate/route");
export const loadAccountActivateRoute = () =>
  import("@/app/api/account/activate/route");
export const loadAccountDeactivateRoute = () =>
  import("@/app/api/account/deactivate/route");
