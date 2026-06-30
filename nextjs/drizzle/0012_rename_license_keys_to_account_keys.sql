-- Rename the table license_keys -> account_keys to reflect the new model: credits
-- are pooled per ACCOUNT, and a key is just the credential that unlocks its owner's
-- wallet. This is a pure rename — no rows, columns, or data change. The `key`
-- column and the JSON wire field stay `license_key` (installed native apps depend
-- on them); only the Postgres identifiers move to the new name.

ALTER TABLE "license_keys" RENAME TO "account_keys";--> statement-breakpoint

-- Indexes do not auto-rename with the table; re-key all seven.
ALTER INDEX "idx_license_keys_stripe_session" RENAME TO "idx_account_keys_stripe_session";--> statement-breakpoint
ALTER INDEX "idx_license_keys_polar_license_key_id" RENAME TO "idx_account_keys_polar_license_key_id";--> statement-breakpoint
ALTER INDEX "idx_license_keys_email" RENAME TO "idx_account_keys_email";--> statement-breakpoint
ALTER INDEX "idx_license_keys_key" RENAME TO "idx_account_keys_key";--> statement-breakpoint
ALTER INDEX "idx_license_keys_user_id" RENAME TO "idx_account_keys_user_id";--> statement-breakpoint
ALTER INDEX "idx_license_keys_polar_customer" RENAME TO "idx_account_keys_polar_customer";--> statement-breakpoint
ALTER INDEX "idx_license_keys_stripe_customer" RENAME TO "idx_account_keys_stripe_customer";--> statement-breakpoint

-- Constraints keep their old names after a table rename; align them with the new
-- table name so drizzle's snapshot matches and future diffs stay clean.
ALTER TABLE "account_keys" RENAME CONSTRAINT "license_keys_pkey" TO "account_keys_pkey";--> statement-breakpoint
ALTER TABLE "account_keys" RENAME CONSTRAINT "license_keys_user_id_user_id_fk" TO "account_keys_user_id_user_id_fk";--> statement-breakpoint

-- The referencing FK on device_validations encodes the old table name; rename it
-- too. (Credit tables now reference "user", so they're unaffected.)
ALTER TABLE "device_validations" RENAME CONSTRAINT "device_validations_license_key_id_license_keys_id_fk" TO "device_validations_license_key_id_account_keys_id_fk";
