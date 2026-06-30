# AGENTS.md

Next.js marketing & license website. API is **tRPC v10.45.3**, with a few REST endpoints kept for specific use cases (see below). Data layer is **Drizzle ORM**.

<important if="you are adding or modifying an API endpoint, or deciding between tRPC and REST">

Default to tRPC. These REST endpoints are kept intentionally — don't migrate them to tRPC:

| Endpoint | Reason Kept |
|----------|-------------|
| `/api/license/*` | **App compatibility** - Native app makes direct HTTP calls, can't use tRPC |
| `/api/checkout/*` | Redirect-based flows that work better as REST |
| `/api/download` | GET does HTTP redirect to CDN, not a JSON response |
| `/api/webhooks/stripe` | **Signature verification** - Needs raw request body for HMAC validation |
| `/api/auth/*` | Better Auth handlers |
</important>

<important if="you are changing the database schema or writing/running migrations">

1. Edit table definitions in `nextjs/src/db/schema/` (split by domain: `auth.ts`, `license-keys.ts`, `blog-posts.ts`, etc.)
2. Generate migration: `npm run db:generate`
3. Apply locally: `npm run db:migrate` (runs `tsx src/db/migrate.ts`)
4. For custom SQL (triggers, functions): create a manual SQL file in `nextjs/drizzle/`

NEVER apply migrations to the remote server. Prompt the user to do this manually.
</important>

<important if="you are running a local production build to verify changes">

Local builds require `SKIP_ENV_VALIDATION=1` to skip Stripe/service env var checks:
```bash
SKIP_ENV_VALIDATION=1 npm run build
```
</important>

<important if="you are making i18n, locale, or translation-string changes">

Locale config is `i18n.ts`; translation JSON files live in `messages/` (see `messages/AGENTS.md` for JSON syntax rules).
</important>