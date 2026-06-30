import {
  pgTable,
  uuid,
  text,
  timestamp,
  uniqueIndex,
  index,
} from "drizzle-orm/pg-core";
import { relations } from "drizzle-orm";
import { licenseKeys } from "./license-keys";

export const deviceValidations = pgTable(
  "device_validations",
  {
    id: uuid("id").defaultRandom().primaryKey(),
    licenseKeyId: uuid("license_key_id")
      .notNull()
      .references(() => licenseKeys.id, { onDelete: "cascade" }),
    deviceId: text("device_id").notNull(),
    deviceName: text("device_name"),
    createdAt: timestamp("created_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
    lastValidatedAt: timestamp("last_validated_at", { withTimezone: true })
      .notNull()
      .defaultNow(),
  },
  (table) => [
    uniqueIndex("idx_device_license_device").on(
      table.licenseKeyId,
      table.deviceId
    ),
    index("idx_device_validations_device_id").on(table.deviceId),
    index("idx_device_validations_last_validated").on(table.lastValidatedAt),
  ]
);

// Relations

export const deviceValidationsRelations = relations(
  deviceValidations,
  ({ one }) => ({
    licenseKey: one(licenseKeys, {
      fields: [deviceValidations.licenseKeyId],
      references: [licenseKeys.id],
    }),
  })
);
