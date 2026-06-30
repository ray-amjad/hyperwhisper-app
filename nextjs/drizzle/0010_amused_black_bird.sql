CREATE TABLE "sent_emails" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"recipient" text NOT NULL,
	"email_type" text NOT NULL,
	"subject" text,
	"provider_message_id" text,
	"status" text NOT NULL,
	"error_message" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE INDEX "sent_emails_recipient_idx" ON "sent_emails" USING btree ("recipient");--> statement-breakpoint
CREATE INDEX "sent_emails_created_at_idx" ON "sent_emails" USING btree ("created_at");