/**
 * The one fake `AccountKeyRow` builder the route-test harnesses share.
 *
 * Every harness re-exports `accountKeyRow` so its tests keep importing it
 * from the harness. A harness whose tests pin a different `id`, `key` or
 * `status` builds its own with `accountKeyRowWithDefaults`, so a field added
 * to `AccountKeyRow` is added here once.
 */

import type { AccountKeyRow } from "@/src/lib/db-layer";

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

/** An `accountKeyRow` with a harness's own defaults; per-call overrides still win. */
export function accountKeyRowWithDefaults(
  defaults: Partial<AccountKeyRow>,
): (overrides?: Partial<AccountKeyRow>) => AccountKeyRow {
  return (overrides = {}) => accountKeyRow({ ...defaults, ...overrides });
}
