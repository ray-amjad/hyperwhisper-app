--> Reconcile any pre-existing duplicate `key` rows BEFORE enforcing uniqueness.
--> `key` is NOT NULL, so the NULL-distinct escape hatch does not apply here: any
--> exact-duplicate key value would make CREATE UNIQUE INDEX abort with 23505 and
--> (because vercel.json runs `npm run db:migrate` in the build command) fail the
--> entire production deploy. Drizzle runs each migration file in a single
--> transaction, so every statement below commits atomically with the index swap.
--> Strategy: for each duplicated key keep the oldest row (canonical) and re-home
--> its dependents onto it, then delete the surviving duplicates. Statements are
--> sequenced (each sees the prior statement's effects) to avoid the
--> same-snapshot pitfall of data-modifying CTEs.

--> Stage the canonical mapping so subsequent statements share one definition of
--> "which row wins" per duplicated key.
CREATE TEMP TABLE _lk_dups ON COMMIT DROP AS
WITH ranked AS (
  SELECT
    "id",
    "key",
    first_value("id") OVER (
      PARTITION BY "key"
      ORDER BY "created_at" ASC, "id"::text ASC
    ) AS canonical_id
  FROM "license_keys"
)
SELECT "id" AS dup_id, canonical_id
FROM ranked
WHERE "id" <> canonical_id;
--> statement-breakpoint
--> Ensure every canonical row that has any duplicate with a balance owns a balance
--> row, so the summed balance below has a target and is never lost on delete.
INSERT INTO "credit_balances" ("license_key_id", "balance")
SELECT DISTINCT d.canonical_id, 0
FROM _lk_dups d
WHERE EXISTS (
  SELECT 1 FROM "credit_balances" cb WHERE cb."license_key_id" = d.dup_id
)
ON CONFLICT ("license_key_id") DO NOTHING;
--> statement-breakpoint
--> Sum all duplicate balances into the canonical row's balance.
UPDATE "credit_balances" cb
SET "balance" = cb."balance" + agg.extra,
    "updated_at" = now()
FROM (
  SELECT d.canonical_id, COALESCE(SUM(dup_cb."balance"), 0) AS extra
  FROM _lk_dups d
  JOIN "credit_balances" dup_cb ON dup_cb."license_key_id" = d.dup_id
  GROUP BY d.canonical_id
) agg
WHERE cb."license_key_id" = agg.canonical_id;
--> statement-breakpoint
--> Device validations: (license_key_id, device_id) is UNIQUE. Drop duplicate-row
--> validations whose device already exists on the canonical row, then re-point the rest.
DELETE FROM "device_validations" dv
USING _lk_dups d
WHERE dv."license_key_id" = d.dup_id
  AND EXISTS (
    SELECT 1 FROM "device_validations" canon
    WHERE canon."license_key_id" = d.canonical_id
      AND canon."device_id" = dv."device_id"
  );
--> statement-breakpoint
UPDATE "device_validations" dv
SET "license_key_id" = d.canonical_id
FROM _lk_dups d
WHERE dv."license_key_id" = d.dup_id;
--> statement-breakpoint
--> Credit grants: (source_type, source_id) is globally unique, so re-pointing
--> license_key_id can never collide. Re-point them onto the canonical row.
UPDATE "credit_grants" cg
SET "license_key_id" = d.canonical_id
FROM _lk_dups d
WHERE cg."license_key_id" = d.dup_id;
--> statement-breakpoint
--> Drop the now-orphaned duplicate license_keys rows. Any leftover dependents are
--> cascade-deleted, but the steps above have already re-homed everything of value.
DELETE FROM "license_keys" lk
USING _lk_dups d
WHERE lk."id" = d.dup_id;
--> statement-breakpoint
DROP INDEX IF EXISTS "idx_license_keys_key";--> statement-breakpoint
CREATE UNIQUE INDEX "idx_license_keys_key" ON "license_keys" USING btree ("key");