import {
  index,
  numeric,
  pgTable,
  text,
  timestamp,
  uniqueIndex,
  uuid,
} from "drizzle-orm/pg-core";
import { relations } from "drizzle-orm";
import { licenseKeys } from "./license-keys";

export const creditGrants = pgTable(
  "credit_grants",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    licenseKeyId: uuid("license_key_id")
      .notNull()
      .references(() => licenseKeys.id, { onDelete: "cascade" }),
    sourceType: text("source_type").notNull(),
    sourceId: text("source_id").notNull(),
    originalAmount: numeric("original_amount", { precision: 20, scale: 2 })
      .notNull(),
    remainingAmount: numeric("remaining_amount", { precision: 20, scale: 2 })
      .notNull(),
    refundedAmount: numeric("refunded_amount", { precision: 20, scale: 2 })
      .notNull()
      .default("0"),
    status: text("status").notNull().default("active"),
    // When this grant's credits expire and stop counting toward the spendable
    // balance. Null means never expires (e.g. trial credits). Enforcement is a
    // lazy filter (`expires_at IS NULL OR expires_at > now()`) in the read/spend
    // queries — there is no cron, so an expired row stays status='active' but is
    // excluded from balance and spend. New paid/minted grants are stamped
    // created_at + 365 days; see grantCreditLotInTransaction in db-layer.ts.
    expiresAt: timestamp("expires_at", { withTimezone: true }),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
    updatedAt: timestamp("updated_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    uniqueIndex("credit_grants_source_unique").on(
      table.sourceType,
      table.sourceId,
    ),
    index("credit_grants_license_source_remaining_idx").on(
      table.licenseKeyId,
      table.sourceType,
      table.remainingAmount,
    ),
    // Supports the per-license active-and-unexpired scan used by balance/spend.
    index("credit_grants_license_status_expires_idx").on(
      table.licenseKeyId,
      table.status,
      table.expiresAt,
    ),
  ],
);

export const creditGrantsRelations = relations(creditGrants, ({ one }) => ({
  licenseKey: one(licenseKeys, {
    fields: [creditGrants.licenseKeyId],
    references: [licenseKeys.id],
  }),
}));
