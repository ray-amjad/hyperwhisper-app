CREATE TABLE "stt_latency_samples" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"provider" text NOT NULL,
	"model" text,
	"fly_region" text NOT NULL,
	"audio_seconds" integer,
	"duration_bucket" text NOT NULL,
	"latency_ms" integer NOT NULL,
	"ok" boolean NOT NULL,
	"failure_kind" text,
	"attempt" integer NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE INDEX "stt_latency_samples_agg_idx" ON "stt_latency_samples" USING btree ("duration_bucket","created_at","provider","fly_region");--> statement-breakpoint
CREATE INDEX "stt_latency_samples_created_at_idx" ON "stt_latency_samples" USING btree ("created_at");