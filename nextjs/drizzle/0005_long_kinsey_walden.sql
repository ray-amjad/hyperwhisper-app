ALTER TABLE "credit_balances" ALTER COLUMN "balance" SET DATA TYPE numeric(20, 2);--> statement-breakpoint
ALTER TABLE "credit_balances" ALTER COLUMN "balance" SET DEFAULT '0';