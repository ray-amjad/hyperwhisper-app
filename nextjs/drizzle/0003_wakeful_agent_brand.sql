CREATE TABLE "stripe_processed_events" (
	"event_id" text PRIMARY KEY NOT NULL,
	"event_type" text NOT NULL,
	"stripe_object_id" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
