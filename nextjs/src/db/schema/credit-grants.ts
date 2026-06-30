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
  ],
);

export const creditGrantsRelations = relations(creditGrants, ({ one }) => ({
  licenseKey: one(licenseKeys, {
    fields: [creditGrants.licenseKeyId],
    references: [licenseKeys.id],
  }),
}));
