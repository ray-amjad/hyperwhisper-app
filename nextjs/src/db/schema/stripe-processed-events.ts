import { pgTable, text, timestamp, uniqueIndex } from "drizzle-orm/pg-core";

export const stripeProcessedEvents = pgTable(
  "stripe_processed_events",
  {
    eventId: text("event_id").primaryKey(),
    eventType: text("event_type").notNull(),
    stripeObjectId: text("stripe_object_id").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    uniqueIndex("stripe_processed_events_object_id_unique").on(
      table.stripeObjectId,
    ),
  ],
);
