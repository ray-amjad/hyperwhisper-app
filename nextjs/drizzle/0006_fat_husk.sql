CREATE TABLE "credit_grants" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"license_key_id" uuid NOT NULL,
	"source_type" text NOT NULL,
	"source_id" text NOT NULL,
	"original_amount" numeric(20, 2) NOT NULL,
	"remaining_amount" numeric(20, 2) NOT NULL,
	"refunded_amount" numeric(20, 2) DEFAULT '0' NOT NULL,
	"status" text DEFAULT 'active' NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
ALTER TABLE "credit_grants" ADD CONSTRAINT "credit_grants_license_key_id_license_keys_id_fk" FOREIGN KEY ("license_key_id") REFERENCES "public"."license_keys"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
CREATE UNIQUE INDEX "credit_grants_source_unique" ON "credit_grants" USING btree ("source_type","source_id");--> statement-breakpoint
CREATE INDEX "credit_grants_license_source_remaining_idx" ON "credit_grants" USING btree ("license_key_id","source_type","remaining_amount");--> statement-breakpoint
INSERT INTO "credit_grants" (
  "license_key_id",
  "source_type",
  "source_id",
  "original_amount",
  "remaining_amount",
  "refunded_amount",
  "status",
  "created_at",
  "updated_at"
)
SELECT
  "license_key_id",
  'legacy_unknown',
  "license_key_id"::text,
  "balance",
  "balance",
  0,
  CASE WHEN "balance" > 0 THEN 'active' ELSE 'spent' END,
  NOW(),
  NOW()
FROM "credit_balances"
WHERE "balance" > 0
ON CONFLICT ("source_type", "source_id") DO NOTHING;
