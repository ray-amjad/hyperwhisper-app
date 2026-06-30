// @ts-check
import { z } from "zod";

/**
 * Specify your server-side environment variables schema here.
 * This way you can ensure the app isn't built with invalid env vars.
 */
export const serverSchema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]),

  // Polar (kept for portal access to past invoices)
  POLAR_ORGANIZATION_ID: z.string(),
  POLAR_ACCESS_TOKEN: z.string(),

  // Stripe
  STRIPE_SECRET_KEY: z.string().startsWith("sk_"),
  STRIPE_LICENSE_PRODUCT_ID: z.string().startsWith("prod_"),
  STRIPE_CREDITS_PRODUCT_5: z.string().startsWith("prod_"),
  STRIPE_CREDITS_PRODUCT_10: z.string().startsWith("prod_"),
  STRIPE_CREDITS_PRODUCT_20: z.string().startsWith("prod_"),
  STRIPE_WEBHOOK_SECRET: z.string().startsWith("whsec_"),

  // Upstash Redis
  UPSTASH_REDIS_REST_URL: z.string().url(),
  UPSTASH_REDIS_REST_TOKEN: z.string(),

  // Resend Email
  RESEND_API_KEY: z.string(),

  // HyperWhisper Cloud API (for CF Workers to call credits endpoints)
  HYPERWHISPER_CLOUD_API_KEY: z.string().optional(),

  // Shared secret for internal license endpoints (Agentic Coding School calls
  // /api/internal/grant-license and /api/internal/licenses-for-email with it)
  HYPERWHISPER_INTERNAL_SECRET: z.string().optional(),

  // Provider model inventory endpoint (/models and /api/internal/models)
  OPENAI_API_KEY: z.string().optional(),
  ANTHROPIC_API_KEY: z.string().optional(),
  GEMINI_API_KEY: z.string().optional(),
  GROQ_API_KEY: z.string().optional(),
  XAI_API_KEY: z.string().optional(),
  CEREBRAS_API_KEY: z.string().optional(),

  // Dev bypass key for local development (skips license validation)
  DEV_BYPASS_KEY: z.string().optional(),

  // Outrank blog publishing webhook (POST /api/webhooks/add-blog-post).
  // OUTRANK_WEBHOOK_TOKEN is the operative auth: Outrank itself can only send a
  // static bearer token (no payload signing), so it must be treated as a
  // long-lived shared secret — keep it secret and rotate it on suspicion of
  // leak. OUTRANK_WEBHOOK_SIGNING_SECRET enables the optional HMAC path for a
  // signing-capable proxy in front of the webhook; when unset the bearer token
  // is the only auth (this is the permanent, expected state for direct Outrank
  // deliveries).
  OUTRANK_WEBHOOK_TOKEN: z.string().min(1),
  OUTRANK_WEBHOOK_SIGNING_SECRET: z.string().optional(),
});

/**
 * You can't destruct `process.env` as a regular object in the Next.js
 * middleware, so you have to do it manually here.
 * @type {{ [k in keyof z.input<typeof serverSchema>]: string | undefined }}
 */
export const serverEnv = {
  NODE_ENV: process.env.NODE_ENV,

  // Polar (kept for portal access to past invoices)
  POLAR_ORGANIZATION_ID: process.env.POLAR_ORGANIZATION_ID,
  POLAR_ACCESS_TOKEN: process.env.POLAR_ACCESS_TOKEN,

  // Stripe
  STRIPE_SECRET_KEY: process.env.STRIPE_SECRET_KEY,
  STRIPE_LICENSE_PRODUCT_ID: process.env.STRIPE_LICENSE_PRODUCT_ID,
  STRIPE_CREDITS_PRODUCT_5: process.env.STRIPE_CREDITS_PRODUCT_5,
  STRIPE_CREDITS_PRODUCT_10: process.env.STRIPE_CREDITS_PRODUCT_10,
  STRIPE_CREDITS_PRODUCT_20: process.env.STRIPE_CREDITS_PRODUCT_20,
  STRIPE_WEBHOOK_SECRET: process.env.STRIPE_WEBHOOK_SECRET,

  // Upstash Redis
  UPSTASH_REDIS_REST_URL: process.env.UPSTASH_REDIS_REST_URL,
  UPSTASH_REDIS_REST_TOKEN: process.env.UPSTASH_REDIS_REST_TOKEN,

  // Resend Email
  RESEND_API_KEY: process.env.RESEND_API_KEY,

  // HyperWhisper Cloud API (for CF Workers to call credits endpoints)
  HYPERWHISPER_CLOUD_API_KEY: process.env.HYPERWHISPER_CLOUD_API_KEY,

  // Shared secret for internal license endpoints (Agentic Coding School calls
  // /api/internal/grant-license and /api/internal/licenses-for-email with it)
  HYPERWHISPER_INTERNAL_SECRET: process.env.HYPERWHISPER_INTERNAL_SECRET,

  // Provider model inventory endpoint (/models and /api/internal/models)
  OPENAI_API_KEY: process.env.OPENAI_API_KEY,
  ANTHROPIC_API_KEY: process.env.ANTHROPIC_API_KEY,
  GEMINI_API_KEY: process.env.GEMINI_API_KEY,
  GROQ_API_KEY: process.env.GROQ_API_KEY,
  XAI_API_KEY: process.env.XAI_API_KEY,
  CEREBRAS_API_KEY: process.env.CEREBRAS_API_KEY,

  // Dev bypass key for local development (skips license validation)
  DEV_BYPASS_KEY: process.env.DEV_BYPASS_KEY,

  // Outrank blog publishing webhook (POST /api/webhooks/add-blog-post)
  OUTRANK_WEBHOOK_TOKEN: process.env.OUTRANK_WEBHOOK_TOKEN,
  OUTRANK_WEBHOOK_SIGNING_SECRET: process.env.OUTRANK_WEBHOOK_SIGNING_SECRET,
};

/**
 * Specify your client-side environment variables schema here.
 * This way you can ensure the app isn't built with invalid env vars.
 * To expose them to the client, prefix them with `NEXT_PUBLIC_`.
 */
export const clientSchema = z.object({
  NEXT_PUBLIC_ENVIRONMENT: z.enum(["development", "test", "production"]).optional(),
  NEXT_PUBLIC_SITE_URL: z.string().url().optional(),
  NEXT_PUBLIC_BILLING_PORTAL_URL: z
    .string()
    .url()
    .default("https://polar.sh/hyperwhisper/portal"),
  NEXT_PUBLIC_CLOUDFLARE_WORKER_URL: z.string().url(),
  NEXT_PUBLIC_POSTHOG_KEY: z.string().optional(),
  NEXT_PUBLIC_POSTHOG_HOST: z.string().url().optional(),
});

/**
 * You can't destruct `process.env` as a regular object, so you have to do
 * it manually here. This is because Next.js evaluates this at build time,
 * and only used environment variables are included in the build.
 * @type {{ [k in keyof z.input<typeof clientSchema>]: string | undefined }}
 */
export const clientEnv = {
  NEXT_PUBLIC_ENVIRONMENT: process.env.NEXT_PUBLIC_ENVIRONMENT,
  NEXT_PUBLIC_SITE_URL: process.env.NEXT_PUBLIC_SITE_URL,
  NEXT_PUBLIC_BILLING_PORTAL_URL: process.env.NEXT_PUBLIC_BILLING_PORTAL_URL,
  NEXT_PUBLIC_CLOUDFLARE_WORKER_URL: process.env.NEXT_PUBLIC_CLOUDFLARE_WORKER_URL,
  NEXT_PUBLIC_POSTHOG_KEY: process.env.NEXT_PUBLIC_POSTHOG_KEY,
  NEXT_PUBLIC_POSTHOG_HOST: process.env.NEXT_PUBLIC_POSTHOG_HOST,
};
