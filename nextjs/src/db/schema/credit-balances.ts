import { pgTable, uuid, numeric, timestamp } from "drizzle-orm/pg-core";
import { relations } from "drizzle-orm";
import { licenseKeys } from "./license-keys";

export const creditBalances = pgTable("credit_balances", {
  id: uuid("id").defaultRandom().primaryKey(),
  licenseKeyId: uuid("license_key_id")
    .notNull()
    .references(() => licenseKeys.id, { onDelete: "cascade" })
    .unique(),
  balance: numeric("balance", { precision: 20, scale: 2 })
    .notNull()
    .default("0"),
  updatedAt: timestamp("updated_at", { withTimezone: true })
    .notNull()
    .defaultNow(),
});

// Relations

export const creditBalancesRelations = relations(creditBalances, ({ one }) => ({
  licenseKey: one(licenseKeys, {
    fields: [creditBalances.licenseKeyId],
    references: [licenseKeys.id],
  }),
}));
