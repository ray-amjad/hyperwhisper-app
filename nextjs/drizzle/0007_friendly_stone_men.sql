-- Dedupe defense for Polar license imports (issue #379 follow-up).
--
-- Back the read-then-write dedupe in importLicenseFromPolar with a real unique
-- constraint so two concurrent imports of casing/whitespace variants of the
-- same Polar key cannot both insert a license row.
--
-- Pre-existing rows may already contain duplicate polar_license_key_id values
-- (the exact harm of #379), which would make CREATE UNIQUE INDEX fail. Before
-- creating the index, keep the polar_license_key_id only on the earliest row in
-- each duplicate group and clear it on the rest. No rows are deleted and credit
-- grants are untouched; the cleared rows simply stop participating in the
-- Polar-id dedupe lookup (they remain reachable by their license key).
UPDATE "license_keys" AS lk
SET "polar_license_key_id" = NULL
WHERE "polar_license_key_id" IS NOT NULL
  AND EXISTS (
    SELECT 1
    FROM "license_keys" AS earlier
    WHERE earlier."polar_license_key_id" = lk."polar_license_key_id"
      AND (
        earlier."created_at" < lk."created_at"
        OR (earlier."created_at" = lk."created_at" AND earlier."id" < lk."id")
      )
  );
--> statement-breakpoint
CREATE UNIQUE INDEX "idx_license_keys_polar_license_key_id" ON "license_keys" USING btree ("polar_license_key_id");