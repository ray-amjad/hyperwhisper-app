import { pgTable, uuid, text, timestamp, index } from "drizzle-orm/pg-core";

/**
 * Append-only audit log of every transactional email we attempt to send
 * (success or permanent failure). One row = one logical email. Standalone /
 * email-keyed (no FK to license_keys) because welcome emails carry no key and
 * Stripe sends reference the key as a string, not our internal id.
 */
export const sentEmails = pgTable(
  "sent_emails",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    recipient: text("recipient").notNull(),
    emailType: text("email_type").notNull(),
    subject: text("subject"),
    providerMessageId: text("provider_message_id"),
    status: text("status").notNull(),
    errorMessage: text("error_message"),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    index("sent_emails_recipient_idx").on(table.recipient),
    index("sent_emails_created_at_idx").on(table.createdAt),
  ],
);
