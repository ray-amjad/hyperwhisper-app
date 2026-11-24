# Merge gate for ray-amjad/hyperwhisper-app

This file, `.claude/merge-gate.md`, is the merge gate for HyperWhisper Dev, and for Percy when a run names this repo. It says what may squash-merge with no ask to Ray, and what proof that needs. One copy, in git, next to the code it governs.

Ray set these rules on 24 Nov 2026, and added the HyperWhisper-specific picks the same day (transcription privacy, installers and auto-update, large or stale PRs, fork PRs, destructive migrations held; Cloud routing, URL and docs-move changes, and additive migrations auto-merge). The same base rules apply in `ray-amjad/hyperwhisper-ios` and `ray-amjad/hyperwhisper-android`. Each repo keeps its own copy and names its own paths. This copy names the paths in this repo.

A change to this file is a change to what ships without Ray. It is held (§3).

## 1. Facts

| Fact | Value |
|---|---|
| Repo | `ray-amjad/hyperwhisper-app` |
| Default branch | `main` |
| Apps | `app/macos` (Swift), `app/windows` (.NET), `app/linux`, `nextjs` (marketing and licence site), `hyperwhisper-cloud` (Fly.io transcription), `mintlify-help` |
| Package managers | pnpm at the repo root (`pnpm-lock.yaml`). `hyperwhisper-cloud` uses Bun (`hyperwhisper-cloud/bun.lock`). Rust is `shared-core-rs/Cargo.lock` |
| Website | https://www.hyperwhisper.com and https://hyperwhisper.com, Cloudflare Worker `hyperwhisper-web-prod` (`nextjs/wrangler.jsonc`) |
| Deploy on merge | Website: `.github/workflows/nextjs-deploy.yml` builds and deploys `main`, and runs `db:migrate` first. Cloud: `.github/workflows/cloud-deploy-prod.yml` on cloud paths. Desktop apps ship through the manual release workflows, not on merge |
| Secrets | Infisical only. Nothing secret belongs in git |
| CI the gate reads | Every workflow run on the exact PR head SHA. Unfiltered on every PR: `static-gates`, `Binding Drift`, `Drizzle journal`. The rest are path-filtered (`macos-ci`, `windows-ci`, `linux-ci`, `nextjs-ci`, `cloud-ci`, `shared-core-tests`, `deps-audit`, and the deploy workflows) |
| Managing bot | HyperWhisper Dev |
| Implementing worker | Percy |

## 2. Auto-merge

A PR outside the held areas (§3) squash-merges with no ask to Ray once all three are true on the exact head commit:

1. CI is green on that commit (§4).
2. Code review is done and its findings are addressed.
3. HyperWhisper Dev has checked Percy's verification proof. Dev reads the proof (commands, results, and screenshots where the change is visible) and confirms it covers the diff.

Then squash-merge (§6). Report it in the daily digest (§5), not as its own message.

These areas were picked on purpose for auto-merge. They follow the three steps above and need no ask:

- Cloud routing: provider or model selection, the model catalog, and fallbacks. For example `hyperwhisper-cloud/src/providers/**` (except `vendor-api-keys.ts`, which is auth), `hyperwhisper-cloud/src/lib/stt-models.ts`, `hyperwhisper-cloud/src/lib/llm-models.ts`, `shared-models/models-catalog.json`, `shared-app-classification/cloud-stt-catalog.json`. Prices and credit metering stay billing (§3).
- URL, redirect, and docs-move changes: Next.js redirects and rewrites, moved or renamed pages, and `mintlify-help` navigation and page moves. A moved legal or privacy page stays held (§3).
- Additive migrations (§3a).
- Everything else that §3 does not hold.

Read the diff at merge time, not from the issue title. A review round can pull a held path into a PR that started without one.

## 3. Held for Ray

These stay held for Ray's explicit go-ahead. Copy-only and styling-only changes count. A bug fix in one of these areas is held too. If the diff touches a held area even in part, the whole PR is held.

Leave the PR open and send Ray a one-line ask (§5): the PR link and which area it touches. His go-ahead waives this section only. Check §2 items 1–3 again on the current head, then squash-merge.

A path not listed below is still held when the diff changes that area's behaviour. The lists are the known map, so a known file is not missed.

### Billing

Stripe, webhooks, or charging code. Prices, plans, limits, or credits, including credit reservation and metering. Refunds or credit grants. Pricing-page copy or styling. Every billing change, even copy.

- `nextjs/lib/clients/stripe.ts`
- `nextjs/lib/services/stripe-customer.ts`, `nextjs/lib/services/stripe-webhook.ts`
- `nextjs/app/api/webhooks/stripe/**`
- `nextjs/app/api/checkout/**`
- `nextjs/app/api/account/credits/**`, `nextjs/app/api/license/credits/**`
- `nextjs/app/api/internal/grant-license/**`
- `nextjs/lib/credits-per-minute.ts`, `nextjs/src/lib/credits-email.ts`
- `nextjs/components/landing/PricingSection.tsx`
- `nextjs/components/credits/**`, `nextjs/app/[locale]/credits/**`
- `nextjs/components/customer/dashboard/BillingCard.tsx`
- `hyperwhisper-cloud/src/middleware/credits.ts`
- Billing tables: `nextjs/src/db/schema/credit-balances.ts`, `nextjs/src/db/schema/credit-grants.ts`, `nextjs/src/db/schema/stripe-processed-events.ts`

### Auth

Sessions, cookies, and Better Auth config. API keys, scopes, and rate limits. Roles and admin permissions. OAuth, sign-in, 2FA, and invite flows. Sign-in page copy or styling. Every auth change, even copy.

- `nextjs/src/lib/auth.ts` and the other `nextjs/src/lib/auth-*.ts` files (client, rate-limit storage, enabled paths, event log, licence-key plugin)
- `nextjs/src/db/schema/auth.ts`, `nextjs/src/db/schema/account-keys.ts`
- `nextjs/app/api/auth/**`
- `nextjs/app/[locale]/user/sign-in/**`, `nextjs/app/[locale]/customer/sign-in/**`
- `nextjs/server/api/routers/admin/**`, `nextjs/app/api/mcp/admin/**`
- `hyperwhisper-cloud/src/middleware/auth.ts`, `hyperwhisper-cloud/src/middleware/rate-limit.ts`
- `hyperwhisper-cloud/src/lib/account-key.ts`, `hyperwhisper-cloud/src/providers/vendor-api-keys.ts`

### CI, release, and signing

- `.github/workflows/**`, including `macos-release.yml`, `windows-release.yml`, `linux-release.yml`, `nextjs-deploy.yml`, and `cloud-deploy*.yml`
- macOS sign and notarize: `app/macos/sign_before_archive.sh`, `app/macos/resign_for_notarization.sh`, `app/macos/sign_whisper_framework.sh`, `app/macos/hyperwhisper/*.entitlements`
- Windows Authenticode is the signing step in `.github/workflows/windows-release.yml`
- Linux APT signing is the signing step in `.github/workflows/linux-release.yml`

### Secrets, tokens, and infra config

Env vars, Cloudflare / Workers / wrangler config, and deployment infra.

- `nextjs/wrangler.jsonc`
- `hyperwhisper-cloud/fly.prod.toml`, `hyperwhisper-cloud/fly.staging.toml`, `hyperwhisper-cloud/fly.dev.toml`, `hyperwhisper-cloud/Dockerfile`
- `fly-log-shipper/fly.toml`
- Any committed env schema or deploy config that sets runtime configuration

Secret values stay in Infisical. A diff that commits a secret value is held, and it has to come out before anyone merges.

### Legal and privacy copy

Privacy policy, terms, licences, and any data-handling statement. A sentence that states what the product does with a user's data is this area, whatever file it sits in.

- `nextjs/app/[locale]/legal/**` (privacy policy, terms of service, refund policy)
- `nextjs/components/legal/**`
- `mintlify-help/data-privacy.mdx`
- `LICENSE` files (`LICENSE`, `nextjs/LICENSE`, `hyperwhisper-cloud/LICENSE`, `mintlify-help/LICENSE`)

### Transcription privacy

What audio or text is stored, logged, or sent off the device or server: to Sentry, to analytics (PostHog), or to logs. That covers recording and transcript retention, history storage, Sentry and log redaction, and any new field on an error report or analytics event that can carry audio, a transcript, a prompt, or screen text.

- Sentry and its redaction: `app/macos/hyperwhisper/Utilities/SentryService*.swift`, `app/windows/HyperWhisper/Services/SentryService*.cs`, `app/shared-dotnet/HyperWhisper.Telemetry/**`, `hyperwhisper-cloud/src/lib/error-tracking.ts`, `nextjs/sentry.*.config.ts`
- Logs and their redaction: `hyperwhisper-cloud/src/lib/logging.ts`, `hyperwhisper-cloud/src/lib/log-redaction.ts`, `nextjs/lib/shared/redact.ts`
- Analytics: `nextjs/contexts/PostHogProvider.tsx`, `nextjs/src/lib/posthog-client.ts`, and any analytics event the desktop apps send
- Stored audio and transcripts: `app/shared-dotnet/HyperWhisper.Application/CompletedAudioRetention.cs`, `app/shared-dotnet/HyperWhisper.Application/HistoryRepository.cs`, `app/windows/HyperWhisper/Services/HistoryService.cs`, `app/windows/HyperWhisper/Services/PendingHistoryStore.cs`, `app/macos/hyperwhisper/CoreData/PersistenceController+Transcripts.swift`

A history or Sentry change that only fixes a crash and does not change what is kept or sent is still held: the reviewer cannot tell that from the title.

### Installers and auto-update

Installers, packages, and the update feeds the apps read.

- Linux packaging and the APT repo: `packaging/linux/**` (including `scripts/generate-apt-repository.sh`, `debian/postinst`, `debian/postrm`), `app/linux/HyperWhisper.Linux.Platform/System/LinuxPackageUpdateProbe.cs`
- Windows installer: `app/windows/setup-*.iss`, `app/windows/dependencies/*.iss`
- macOS packaging: the DMG build in `.github/workflows/macos-release.yml` (also held as CI) and `.github/assets/dmg-background.png`
- Update feeds and updaters: `nextjs/public/appcast.xml`, `nextjs/public/appcast-windows.xml`, `nextjs/lib/shared/appcast.ts`, `nextjs/server/api/routers/download-appcast.ts`, `nextjs/scripts/*appcast*.js`, `app/macos/hyperwhisper/Utilities/AppcastParser.swift`, `app/windows/HyperWhisper/Services/AppcastService.cs`, `app/windows/HyperWhisper/Services/UpdateService.cs`

### Destructive migrations

A migration that drops or renames a column or a table, or narrows a type, is held. Additive migrations are not (§3a).

### PR shape

These are held whatever paths they touch:

- Large or stale PRs: about 40 or more changed files, or a PR that needed a heavy conflict rebase onto `main`.
- PRs from outside contributors on a fork (the head repo is not `ray-amjad/hyperwhisper-app`). Read it with `gh pr view <n> --json isCrossRepository`.

### This file

An edit to `.claude/merge-gate.md` is held.

## 3a. Migrations and lockfiles

Additive database migrations, schema edits, dependency bumps, and lockfiles are not held. They follow §2. A migration that drops or renames a column is held (§3, Destructive migrations).

Verification takes extra care on these:

- The migration is reversible, or it is expand-only and safe to leave applied. In `nextjs/drizzle/`, migrations are append-only: add a new file to undo an old one. Leave a merged entry's SQL, name, and journal `when` alone. The new `when` has to be later than every entry above it. `nextjs-deploy.yml` runs `db:migrate` while the previous Worker is still serving, so the change has to work for both. Check with `BASE_REF=origin/main python3 .github/scripts/validate_drizzle_journal.py`.
- The lockfile diff matches the intended bump and nothing else. The lockfiles here are `pnpm-lock.yaml`, `hyperwhisper-cloud/bun.lock`, and `shared-core-rs/Cargo.lock`.

A migration or lockfile hunk that also touches a §3 path holds the whole PR. The auth and billing schema files named in §3 stay held as those areas.

## 4. CI on the exact head

Every workflow run on the PR head SHA is `completed` with conclusion `success`. Read the SHA from the PR.

```bash
gh api "repos/ray-amjad/hyperwhisper-app/actions/runs?head_sha=<full sha>" \
  --jq '.workflow_runs[] | [.name, .status, .conclusion] | @tsv'
```

Wait out a `queued` or `in_progress` run. A path-filtered workflow that never started is not a failure. A red run blocks the merge. Name the failing job when you report the PR.

## 5. Reporting

Auto-merges go in one daily digest to Ray. The digest lists what merged, the PR links, and the evidence: green CI on the head SHA, review done, and the proof HyperWhisper Dev checked.

A held PR gets one line to Ray: the area, the PR link, and the ask. Leave it there until he answers. Send a fresh line only when the hold itself changes.

## 6. Squash-merge

```bash
gh pr merge <n> --repo ray-amjad/hyperwhisper-app --squash --delete-branch
gh pr view <n> --repo ray-amjad/hyperwhisper-app --json state,mergeCommit
```

`state` must read `MERGED` before anyone claims it. A refused merge (branch protection, a conflict) leaves the PR open. Say what refused it.
