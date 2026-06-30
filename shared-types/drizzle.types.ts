/**
 * Drizzle-inferred types for the Neon database.
 *
 * These types are derived from the Drizzle schema definitions in nextjs/src/db/schema/.
 */

import type { InferSelectModel, InferInsertModel } from "drizzle-orm";
import type { licenseKeys } from "../nextjs/src/db/schema/license-keys";
import type { creditBalances } from "../nextjs/src/db/schema/credit-balances";
import type { deviceValidations } from "../nextjs/src/db/schema/device-validations";
import type { emails } from "../nextjs/src/db/schema/emails";
import type {
  user,
  session,
  account,
  verification,
} from "../nextjs/src/db/schema/auth";

// ── License Keys ──────────────────────────────────────────────
export type LicenseKey = InferSelectModel<typeof licenseKeys>;
export type NewLicenseKey = InferInsertModel<typeof licenseKeys>;

// ── Credit Balances ───────────────────────────────────────────
export type CreditBalance = InferSelectModel<typeof creditBalances>;
export type NewCreditBalance = InferInsertModel<typeof creditBalances>;

// ── Device Validations ────────────────────────────────────────
export type DeviceValidation = InferSelectModel<typeof deviceValidations>;
export type NewDeviceValidation = InferInsertModel<typeof deviceValidations>;

// ── Emails ────────────────────────────────────────────────────
export type Email = InferSelectModel<typeof emails>;
export type NewEmail = InferInsertModel<typeof emails>;

// ── Auth (Better Auth) ───────────────────────────────────────
export type User = InferSelectModel<typeof user>;
export type NewUser = InferInsertModel<typeof user>;

export type Session = InferSelectModel<typeof session>;
export type NewSession = InferInsertModel<typeof session>;

export type Account = InferSelectModel<typeof account>;
export type NewAccount = InferInsertModel<typeof account>;

export type Verification = InferSelectModel<typeof verification>;
export type NewVerification = InferInsertModel<typeof verification>;
