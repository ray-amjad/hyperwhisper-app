# Deploying the website on Cloudflare Workers

The site (`nextjs/`) runs as the Cloudflare Worker **`hyperwhisper-web`**, built
with [`@opennextjs/cloudflare`](https://opennext.js.org/cloudflare). Workers
Builds (Cloudflare's git integration) builds and deploys it on every push to
`main`. There are no per-PR previews; `nextjs-ci` (typecheck, tests and an
OpenNext build) is the pre-merge gate.

| File | Role |
| --- | --- |
| `wrangler.jsonc` | Worker config: bindings, cron triggers, observability |
| `open-next.config.ts` | ISR cache: R2 incremental cache, DO queue, DO tag cache |
| `worker.ts` | Worker entry: wraps `.open-next/worker.js`, adds `scheduled()` and drops a proxied `Content-Encoding` the client did not accept (`lib/cloudflare/content-encoding.ts`) |
| `config/cron-jobs.mjs` | cron expression -> internal route |
| `config/mintlify-rewrites.mjs` | `/docs` and friends -> Mintlify (was `vercel.json`) |
| `scripts/cf-build.mjs` | `pnpm run cf:build`: OpenNext build with build-only secret placeholders; drops the database pages' empty build-time renders from the ISR cache |
| `scripts/wait-for-migrations.mjs` | deploy gate: waits for `nextjs-migrate` to succeed on a commit that ships a migration |
| `public/_headers` | security + CORS headers for static files (appcasts), and the one-year `immutable` cache on hashed `/_next/static/*` files |
| `src/db/index.ts` | per-request Drizzle client over the `HYPERDRIVE` binding |
| `../.github/workflows/nextjs-migrate.yml` | `db:migrate` on push to `main` |

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
3. **Workers Builds**: Workers & Pages -> Create -> Import a repository ->
   `ray-amjad/hyperwhisper-app`, Worker name `hyperwhisper-web`.

   | Setting | Value |
   | --- | --- |
   | Root directory | `nextjs` |
   | Production branch | `main` |
   | Non-production branch builds | **off** |
   | Build command | `npx --yes pnpm@10.33.0 install && npx --yes pnpm@10.33.0 run validate-appcasts && npx --yes pnpm@10.33.0 run cf:build` |
   | Deploy command | `node scripts/wait-for-migrations.mjs && npx --yes pnpm@10.33.0 exec opennextjs-cloudflare deploy` |
   | Build watch paths (include) | `nextjs/*`, `shared-types/*` |

   Use `opennextjs-cloudflare deploy`, never plain `wrangler deploy`: it fills
   the R2 cache with the prerendered pages before uploading the Worker.
   `wait-for-migrations.mjs` must stay in front of it (section 2).

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

   **Build variables** (Settings -> Build -> Variables; these exist only during
   the build, and `NEXT_PUBLIC_*` values are inlined into the browser bundle):

   | Name | Value |
   | --- | --- |
   | `SKIP_DEPENDENCY_INSTALL` | `1` (nextjs/ has no committed lockfile; the build command installs with pnpm) |
   | `NODE_VERSION` | `22` |
   | `NEXT_PUBLIC_ENVIRONMENT` | `production` |
   | `NEXT_PUBLIC_POSTHOG_KEY` | the public PostHog project key |
   | `NEXT_PUBLIC_POSTHOG_HOST` | optional |
   | `NEXT_PUBLIC_SITE_URL` | `https://www.hyperwhisper.com` |
   | `CLOUDFLARE_HYPERDRIVE_LOCAL_CONNECTION_STRING_HYPERDRIVE` | `postgres://unused:unused@127.0.0.1:5432/unused` (not a secret; see below) |

   `cf:build` sets `SKIP_ENV_VALIDATION=1` and fake values for the server
   secrets some SDKs need at import time (Stripe, Resend, Upstash). They never
   reach the Worker; the script fails the build if one shows up in
   `.open-next/`. Infisical's Cloudflare sync writes Worker secrets, not build
   variables, so the `NEXT_PUBLIC_*` values above are set in the dashboard
   (they are public anyway).

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
5. **GitHub `Production` environment** (required before merge):
   - It must hold `PLANETSCALE_DATABASE_URL`, synced from Infisical (add the
     name to the Infisical -> GitHub Production sync). It is not there today.
     Without it `nextjs-migrate` fails, and the deploy gate then blocks every
     deploy of a commit that touches `nextjs/drizzle/**` or `nextjs/src/db/**`,
     starting with the merge of this migration (it changes `src/db/index.ts`).
   - Restrict its deployment branches to `main` (Settings -> Environments ->
     Production -> Deployment branches and tags -> Selected branches -> `main`).
     The workflow's job already runs only on `refs/heads/main`; the
     environment rule makes sure no other workflow or branch can read the
     production database URL.

## 2. Migrations

`nextjs-migrate.yml` runs `pnpm run db:migrate` on pushes to `main` that touch
`nextjs/drizzle/**` or `nextjs/src/db/**` (or by hand: Actions ->
nextjs-migrate -> Run workflow, branch `main`; any other branch is skipped).
It starts in parallel with the Workers Build, so the deploy command first runs
`scripts/wait-for-migrations.mjs`. When the commit touches those paths, it
polls the public GitHub API for the `nextjs-migrate` run on that commit and
deploys only after it concluded `success`. A failed run, no run within
4 minutes, or a run still going after 10 minutes fails the deploy, and the old
Worker keeps serving, as a failed `db:migrate` did on Vercel. To recover, fix
and re-run the migration on `main`, then retry the Workers build. The check
diffs the commit against its first parent, which matches squash merges; a
direct push of several commits is checked by its last commit only.

Expand-first migrations (schema first, the code that needs it later) are
still the safer habit: the gate stops a deploy against a missing schema, but
it does not make the migration and the new code switch over at the same
instant.

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

## 5. Cutover (DNS for hyperwhisper.com is already on Cloudflare)

1. Before merging: the real Hyperdrive id is in `wrangler.jsonc`;
   `PLANETSCALE_DATABASE_URL` is in the GitHub `Production` environment
   (synced from Infisical) and that environment is limited to `main`
   (section 1, step 5). Merge, check that `nextjs-migrate` succeeded (the
   deploy waits for it), watch the Workers Build, then smoke-test
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
