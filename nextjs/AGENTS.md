# AGENTS.md

Next.js marketing & license website. API is **tRPC v11**, with a few REST endpoints kept for specific use cases (see below). Data layer is **Drizzle ORM**.

<important if="you are adding or modifying an API endpoint, or deciding between tRPC and REST">

Default to tRPC. These REST endpoints are kept intentionally — don't migrate them to tRPC:

| Endpoint | Reason Kept |
|----------|-------------|
| `/api/license/*` | **App compatibility** - Native app makes direct HTTP calls, can't use tRPC |
| `/api/checkout/*` | Redirect-based flows that work better as REST |
| `/api/download` | GET does an HTTP redirect to the download URL, not a JSON response |
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

<!-- BEGIN:nextjs-agent-rules -->

# This is NOT the Next.js you know

This version has breaking changes — APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` (resolved from this file's directory; in monorepos the `next` package may not be visible from the repo root) before writing any code. Heed deprecation notices.

This block is written and re-added by `next dev` — verify at `node_modules/next/dist/server/lib/generate-agent-files.js`. Removing it from a diff only re-creates the uncommitted change; committing it with your work keeps the tree clean.

<!-- END:nextjs-agent-rules -->
