ALTER TABLE "credit_grants" ADD COLUMN "expires_at" timestamp with time zone;--> statement-breakpoint
CREATE INDEX "credit_grants_license_status_expires_idx" ON "credit_grants" USING btree ("license_key_id","status","expires_at");--> statement-breakpoint
-- Backfill: every existing grant gets a full year from launch (1 year from the
-- moment this migration runs). Guarded by IS NULL so it stamps the launch date
-- exactly once and never overwrites an expiry set by a later code path.
UPDATE "credit_grants" SET "expires_at" = now() + interval '1 year' WHERE "expires_at" IS NULL;