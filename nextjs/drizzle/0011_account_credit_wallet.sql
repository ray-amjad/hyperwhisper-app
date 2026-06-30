-- Re-key the credit ledger from license_key_id (per-key) to user_id (per-account).
-- Credits become ONE pooled balance per account; a license/account key is just a
-- credential that unlocks its owner's wallet. This migration is DATA-PRESERVING:
-- existing per-key grants are backfilled to their owning user, and credit_balances
-- (only a denormalized cache) is rebuilt as one row per account = SUM of that
-- account's active, unexpired remaining_amount. No one loses credits.

-- ============================================================================
-- credit_grants: add user_id, backfill from each grant's owning license, make it
-- NOT NULL + FK -> user, then re-key the indexes and drop license_key_id.
-- ============================================================================
ALTER TABLE "credit_grants" ADD COLUMN "user_id" text;--> statement-breakpoint

-- Backfill every grant's account from its license key. The old FK (onDelete
-- cascade) guaranteed every grant had a live license, and every license has a
-- non-null user_id, so this populates every row.
UPDATE "credit_grants" g
SET "user_id" = lk."user_id"
FROM "license_keys" lk
WHERE g."license_key_id" = lk."id";--> statement-breakpoint

ALTER TABLE "credit_grants" ALTER COLUMN "user_id" SET NOT NULL;--> statement-breakpoint
ALTER TABLE "credit_grants" ADD CONSTRAINT "credit_grants_user_id_user_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."user"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint

DROP INDEX "credit_grants_license_source_remaining_idx";--> statement-breakpoint
DROP INDEX "credit_grants_license_status_expires_idx";--> statement-breakpoint
ALTER TABLE "credit_grants" DROP CONSTRAINT "credit_grants_license_key_id_license_keys_id_fk";--> statement-breakpoint
ALTER TABLE "credit_grants" DROP COLUMN "license_key_id";--> statement-breakpoint

CREATE INDEX "credit_grants_user_source_remaining_idx" ON "credit_grants" USING btree ("user_id","source_type","remaining_amount");--> statement-breakpoint
CREATE INDEX "credit_grants_user_status_expires_idx" ON "credit_grants" USING btree ("user_id","status","expires_at");--> statement-breakpoint

-- ============================================================================
-- credit_balances: only a denormalized cache, so clear it, re-key to the account,
-- and repopulate as one row per account = SUM of that account's active, unexpired
-- remaining_amount. (It self-heals on the next ledger op regardless of expiry,
-- but we seed it correct so the dashboard reads right immediately.)
-- ============================================================================
DELETE FROM "credit_balances";--> statement-breakpoint
ALTER TABLE "credit_balances" DROP CONSTRAINT "credit_balances_license_key_id_license_keys_id_fk";--> statement-breakpoint
ALTER TABLE "credit_balances" DROP CONSTRAINT "credit_balances_license_key_id_unique";--> statement-breakpoint
ALTER TABLE "credit_balances" DROP COLUMN "license_key_id";--> statement-breakpoint
ALTER TABLE "credit_balances" ADD COLUMN "user_id" text NOT NULL;--> statement-breakpoint
ALTER TABLE "credit_balances" ADD CONSTRAINT "credit_balances_user_id_unique" UNIQUE("user_id");--> statement-breakpoint
ALTER TABLE "credit_balances" ADD CONSTRAINT "credit_balances_user_id_user_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."user"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint

INSERT INTO "credit_balances" ("id", "user_id", "balance", "updated_at")
SELECT gen_random_uuid(), "user_id", SUM("remaining_amount"), now()
FROM "credit_grants"
WHERE "status" = 'active'
  AND "remaining_amount" > 0
  AND ("expires_at" IS NULL OR "expires_at" > now())
GROUP BY "user_id";
