import { pgTable, uuid, numeric, timestamp, text } from "drizzle-orm/pg-core";
import { relations } from "drizzle-orm";
import { user } from "./auth";

export const creditBalances = pgTable("credit_balances", {
  id: uuid("id").defaultRandom().primaryKey(),
  userId: text("user_id")
    .notNull()
    .references(() => user.id, { onDelete: "cascade" })
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
  user: one(user, {
    fields: [creditBalances.userId],
    references: [user.id],
  }),
}));
