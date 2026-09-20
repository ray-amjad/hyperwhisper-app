import { pgTable, uuid, text, timestamp } from "drizzle-orm/pg-core";

export const emails = pgTable("emails", {
  id: uuid("id").defaultRandom().primaryKey(),
  email: text("email").notNull().unique(),
  source: text("source"),
  createdAt: timestamp("created_at", { withTimezone: true })
    .notNull()
    .defaultNow(),
});
