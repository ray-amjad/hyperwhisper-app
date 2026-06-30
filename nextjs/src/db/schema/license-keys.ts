import {
  pgTable,
  uuid,
  text,
  timestamp,
  uniqueIndex,
  index,
} from "drizzle-orm/pg-core";
import { relations } from "drizzle-orm";
import { user } from "./auth";
import { creditBalances } from "./credit-balances";
import { creditGrants } from "./credit-grants";
import { deviceValidations } from "./device-validations";

export const licenseKeys = pgTable(
  "license_keys",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    userId: text("user_id")
      .notNull()
      .references(() => user.id, { onDelete: "cascade" }),
    key: text("key").notNull(),
    email: text("email").notNull(),
    status: text("status").notNull().default("granted"),
    polarLicenseKeyId: text("polar_license_key_id"),
    polarCustomerId: text("polar_customer_id"),
    stripeCustomerId: text("stripe_customer_id"),
    stripeSessionId: text("stripe_session_id"),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    uniqueIndex("idx_license_keys_stripe_session").on(table.stripeSessionId),
    // Backs the read-then-write dedupe in importLicenseFromPolar: a unique
    // index on the stable Polar license-key id prevents two concurrent imports
    // of casing/whitespace variants of the same key from both inserting a row.
    // NULLs are distinct in Postgres unique indexes, so Stripe-only license
    // rows (no polar_license_key_id) are unaffected.
    uniqueIndex("idx_license_keys_polar_license_key_id").on(
      table.polarLicenseKeyId
    ),
    index("idx_license_keys_email").on(table.email),
    uniqueIndex("idx_license_keys_key").on(table.key),
    index("idx_license_keys_user_id").on(table.userId),
    index("idx_license_keys_polar_customer").on(table.polarCustomerId),
    index("idx_license_keys_stripe_customer").on(table.stripeCustomerId),
  ]
);

// Relations

export const licenseKeysRelations = relations(licenseKeys, ({ one, many }) => ({
  user: one(user, {
    fields: [licenseKeys.userId],
    references: [user.id],
  }),
  creditBalance: one(creditBalances, {
    fields: [licenseKeys.id],
    references: [creditBalances.licenseKeyId],
  }),
  creditGrants: many(creditGrants),
  deviceValidations: many(deviceValidations),
}));
