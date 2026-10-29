# Deploying the website on Cloudflare Workers

The site (`nextjs/`) runs as the Cloudflare Worker **`hyperwhisper-web`**, built
with [`@opennextjs/cloudflare`](https://opennext.js.org/cloudflare). GitHub
Actions (`.github/workflows/nextjs-deploy.yml`) builds, migrates and deploys it
on every push to `main`. There are no per-PR previews; `nextjs-ci` (typecheck, tests and an
OpenNext build) is the pre-merge gate.

| File | Role |
| --- | --- |
| `wrangler.jsonc` | Worker config: bindings, cron triggers, observability |
| `open-next.config.ts` | ISR cache: R2 incremental cache, DO queue, DO tag cache |
| `worker.ts` | Worker entry: wraps `.open-next/worker.js`, adds `scheduled()` and drops a proxied `Content-Encoding` the client did not accept (`lib/cloudflare/content-encoding.ts`) |
| `config/cron-jobs.mjs` | cron expression -> internal route |
| `config/mintlify-rewrites.mjs` | `/docs` and friends -> Mintlify (was `vercel.json`) |
| `scripts/cf-build.mjs` | `pnpm run cf:build`: OpenNext build with build-only secret placeholders; drops the database pages' empty build-time renders from the ISR cache |
| `public/_headers` | security + CORS headers for static files (appcasts), and the one-year `immutable` cache on hashed `/_next/static/*` files |
| `src/db/index.ts` | per-request Drizzle client over the `HYPERDRIVE` binding |
| `../.github/workflows/nextjs-deploy.yml` | build, `db:migrate`, deploy on push to `main` |

## 1. One-time Cloudflare setup (owner)

Workers **Paid** plan is required (Free allows 10 ms CPU and a 3 MiB bundle;
this Worker is about 4.5 MiB gzipped).

1. **Hyperdrive**, with caching **off**. Hyperdrive caches `SELECT` results for
   about 60 s by default, which breaks read-after-write for licences, sessions
   and credits:

   ```bash
   cd nextjs
   npx wrangler hyperdrive create hyperwhisper-web-db \
     --connection-string="$PLANETSCALE_DATABASE_URL" --caching-disabled
   ```

   Run it in a shell where the URL comes from Infisical (for example
   `infisical run --env=prod -- ...`); never paste it into a file. Put the
   returned id in `wrangler.jsonc` in place of `REPLACE_WITH_HYPERDRIVE_ID`
   **before** this branch merges, or the first deploy fails.
2. **R2 bucket** `hyperwhisper-web-opennext-cache` (the deploy creates it if
   its token may, but create it up front):
   `npx wrangler r2 bucket create hyperwhisper-web-opennext-cache`.
   Add a lifecycle rule that deletes objects older than 30 days: cache keys
   include the build id, so every deploy leaves the previous set behind.
3. **Deploys: GitHub Actions** (`.github/workflows/nextjs-deploy.yml`). On
   every push to `main` that touches `nextjs/**` or `shared-types/**` (or by
   hand: Actions -> nextjs-deploy -> Run workflow, branch `main`), one job
   runs, in order:

   1. `pnpm install`, `pnpm run validate-appcasts`, `pnpm run cf:build`
   2. `pnpm run db:migrate` (section 2)
   3. `pnpm exec opennextjs-cloudflare deploy`

   Use `opennextjs-cloudflare deploy`, never plain `wrangler deploy`: it fills
   the R2 cache with the prerendered pages before uploading the Worker.
   Workers Builds (Cloudflare's git integration) is **disconnected**; with
   both on, every push would deploy twice.

   The release workflows (`macos-release`, `windows-release`,
   `linux-release`) merge their appcast PR with `github.token`, and a push
   made with that token starts no other workflow. So each one runs
   `gh workflow run nextjs-deploy.yml` after the merge; without that, a
   release would never reach the live appcast.

   The build has no database, so `cf:build` removes the build-time renders
   of `/en/blog`, `/en/blog/<code post>`, `/en/latency`,
   `/en/choosing-a-model` and `/sitemap.xml` from the cache it uploads (they
   would show 0 latency samples and only the 2 code blog posts, and count as
   fresh for a whole revalidate window after each deploy). The first request
   for each after a deploy renders it from the database (one blocking render)
   and stores it; after that they revalidate as usual (blog 60 s, the rest
   1 h). If the database is down at that moment the request gets a 500 rather
   than an empty page. A new database-backed static page must be added to
   `DB_RENDERED_ROUTES` in `scripts/cf-build.mjs`.

   **Build values** (set in the workflow; `NEXT_PUBLIC_*` are inlined into
   the browser bundle, so they must be public):

   | Name | Value |
   | --- | --- |
   | `NEXT_PUBLIC_ENVIRONMENT` | `production` |
   | `NEXT_PUBLIC_POSTHOG_KEY` | the public PostHog project key, from the `Production` environment |
   | `NEXT_PUBLIC_SITE_URL` | `https://www.hyperwhisper.com` |
   | `SENTRY_RELEASE` | the commit SHA |
   | `CLOUDFLARE_HYPERDRIVE_LOCAL_CONNECTION_STRING_HYPERDRIVE` | `postgres://unused:unused@127.0.0.1:5432/unused` (deploy step only; not a secret, see below) |

   `cf:build` sets `SKIP_ENV_VALIDATION=1` and fake values for the server
   secrets some SDKs need at import time (Stripe, Resend, Upstash). The build
   step is given no real secret, so none can be inlined; the script also
   fails the build if a placeholder shows up in `.open-next/`.

   The dummy Hyperdrive string is required: `opennextjs-cloudflare deploy`
   first starts wrangler's local platform proxy to read the bindings, and that
   throws `no local hyperdrive connection string` for the `HYPERDRIVE` binding
   unless this variable is set. Nothing connects to it; the deployed Worker
   uses the real Hyperdrive config. The same applies to `pnpm run deploy` or
   `opennextjs-cloudflare populateCache` run from a laptop.
4. **Runtime secrets** (Infisical -> Cloudflare Workers sync, Worker
   `hyperwhisper-web`). Scope the sync to the website's names only; the
   Production environment also holds large signing blobs
   (`DEVELOPER_ID_P12_BASE64`, `PROVISIONING_PROFILE_BASE64`,
   `GOOGLE_SERVICE_ACCOUNT_JSON`, ...) that exceed the 5 KB per-secret limit.
   - Required (validated at runtime by `src/env/server.mjs`):
     `BETTER_AUTH_URL`, `BETTER_AUTH_SECRET`, `STRIPE_SECRET_KEY`,
     `STRIPE_WEBHOOK_SECRET`, `UPSTASH_REDIS_SITE_URL`,
     `UPSTASH_REDIS_SITE_TOKEN`, `RESEND_API_KEY`, `CRON_SECRET`,
     `OUTRANK_WEBHOOK_TOKEN`.
   - Optional: `HYPERWHISPER_INTERNAL_SECRET`, `CREDIT_POOL_ID_SECRET`,
     `OUTRANK_WEBHOOK_SIGNING_SECRET`, `SUPPORT_EMAIL`,
     `HYPERWHISPER_CLOUD_URLS`, `SENTRY_DSN`, `SENTRY_ENVIRONMENT`,
     `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `GROQ_API_KEY`,
     `XAI_API_KEY`, `CEREBRAS_API_KEY`.
   - Not needed on the Worker: `PLANETSCALE_DATABASE_URL` (the Worker uses
     Hyperdrive; it is only a fallback), `DEV_BYPASS_KEY`.
   - Plain var in `wrangler.jsonc`, not a secret: `DEPLOY_ENV=production`.
5. **GitHub `Production` environment**, filled by the Infisical ->
   GitHub sync `prod-github-sync` (prod, path `/`). The deploy reads:
   - `PLANETSCALE_DATABASE_URL` (migrations)
   - `CLOUDFLARE_API_TOKEN`: an account API token named
     "GitHub Actions - hyperwhisper-web deploy", scoped to the Ray Amjad LTD
     account, with Workers Scripts: Edit, Workers R2 Storage: Edit,
     Hyperdrive: Edit and Account Settings: Read
   - `CLOUDFLARE_ACCOUNT_ID`
   - `NEXT_PUBLIC_POSTHOG_KEY`

   Its deployment branches are restricted to `main`, so no other branch can
   read the production database URL or the deploy token.

## 2. Migrations

`nextjs-deploy.yml` runs `pnpm run db:migrate` on every deploy, after the
build and before the Worker is replaced (Drizzle skips migrations that already
ran, so a deploy with none is a no-op). A failed build never touches the
database; a failed migration stops the deploy and the old Worker keeps
serving, as a failed `db:migrate` did on Vercel. To recover, fix it on `main`,
or re-run the workflow.

Expand-first migrations (schema first, the code that needs it later) are
still the safer habit: the migration lands seconds before the new Worker, and
requests already in flight on the old Worker see the new schema.

## 3. Cron

`wrangler.jsonc` `triggers.crons` fires `worker.ts` `scheduled()`, which calls
the route in `config/cron-jobs.mjs` through the Next handler with
`Authorization: Bearer $CRON_SECRET` (UTC):

| Cron | Route |
| --- | --- |
| `17 4 * * *` | `/api/internal/latency/prune` |
| `37 4 * * *` | `/api/internal/sessions/prune` |

A failed run throws, so it shows as an error in Workers Logs. Test locally with
`npx wrangler dev --test-scheduled` and
`curl "http://localhost:8787/__scheduled?cron=17+4+*+*+*"`.

## 4. Local

- `pnpm dev`: plain `next dev`, no Workers runtime. The DB client falls back to
  `PLANETSCALE_DATABASE_URL`.
- `pnpm preview`: builds and runs the real Worker in workerd. Needs a
  gitignored `.dev.vars` (see `.dev.vars.example`) and, in the shell,
  `CLOUDFLARE_HYPERDRIVE_LOCAL_CONNECTION_STRING_HYPERDRIVE=<dev postgres url>`.
- Never deploy from a machine with a populated `nextjs/.env*`: the OpenNext
  build inlines `.env` files into the Worker.
- `next build` rewrites `tsconfig.json`'s `jsx`; check `git diff` afterwards.
  Commit `"jsx": "react-jsx"`: with `"preserve"` the tsx-run test suite fails
  with `React is not defined`.

## 5. Cutover (done 2026-10-29; kept for reference)

1. Before merging: the real Hyperdrive id is in `wrangler.jsonc`;
   `PLANETSCALE_DATABASE_URL` is in the GitHub `Production` environment
   (synced from Infisical) and that environment is limited to `main`
   (section 1, step 5). Merge, watch the `nextjs-deploy` run, then smoke-test
   `https://hyperwhisper-web.<account>.workers.dev`: `/en`, `/de/zzz` (exactly
   one `noindex`), `/docs`, `/appcast.xml` (CORS header), `/en/blog`,
   `/en/latency`, sign-in, `/api/geo/nearest-region?regions=iad,fra`, and
   `curl -s .../docs/llms.txt | head -1` with no `--compressed`: it must print
   `# HyperWhisper`, not binary (a client without `Accept-Encoding` gets the
   Mintlify response uncompressed).
2. Attach the custom domains: Worker -> Settings -> Domains & Routes -> add
   `www.hyperwhisper.com` and `hyperwhisper.com` (or add the commented
   `routes` block in `wrangler.jsonc`). This replaces the Vercel DNS records.
3. Recreate Vercel's apex redirect as a Cloudflare Single Redirect:
   `hyperwhisper.com/*` -> `https://www.hyperwhisper.com/${path}`, status
   **307 or 308, not 301**: clients POST to `/api/license/validate`.
4. Enable HSTS for the zone (SSL/TLS -> Edge Certificates); Vercel sent
   `max-age=63072000`.
5. Optional: a Cache Rule for `/models` and `/api/internal/models` so their
   `s-maxage` is honoured (Worker responses are not edge-cached by default).
   Optional: the "Add visitor location headers" managed transform is not
   needed; `/api/geo/nearest-region` reads `request.cf`.
6. Verify on the real domain:
   - appcasts: `node scripts/check-appcast-live.js --platform windows --version <current>`
   - Stripe: send a test event to the webhook endpoint from the Stripe
     dashboard and check it answers 2xx.
   - Auth: magic-link sign-in and licence-key sign-in end to end.
   - Cron: the next 04:17 / 04:37 UTC runs succeed in Workers Logs.
   - Sentry: an error reaches Sentry (if `SENTRY_DSN` is set). On workerd the
     SDK logs `[Sentry] Failed to register diagnostics-channel injection hooks`
     at startup; the Worker keeps serving, but delivery from Workers has not
     been verified, so trigger a test error and confirm it lands.
7. Update the privacy policy's sub-processor list (Vercel -> Cloudflare) in
   `app/[locale]/legal/privacy-policy/page.tsx`.
8. Only then delete the Vercel project, and remove the Infisical -> Vercel sync.

## 6. Logs and status

```bash
npx wrangler deployments list --name hyperwhisper-web
npx wrangler tail hyperwhisper-web --status error --format pretty   # streams; Ctrl-C to stop
```

Past logs: Workers & Pages -> `hyperwhisper-web` -> Logs (observability is on).
