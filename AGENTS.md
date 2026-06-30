# AGENTS.md

> ⚠️ **This is the PUBLIC, open-source repository** (`ray-amjad/hyperwhisper-app`, Apache-2.0, world-readable). Everything committed here is public forever.
> - **Never** commit secrets, API keys, license keys, signing keys, customer data, personal paths/emails, or internal infra/business details. Secrets live in **Infisical only** (see the secrets note below) — it syncs to GitHub Actions / Vercel / Fly.
> - Internal-only material stays **out**: `.claude/`, `.codex/`, `app/ios/`, `tasks/`, `notes/`, `plans/` are gitignored — keep them that way.
> - HyperWhisper Cloud is the paid moat: entitlement is **enforced server-side**. Never add a client-side bypass, fake/test license key, or debug backdoor.

HyperWhisper — macOS / Windows / iOS speech-to-text app with a Fly.io transcription backend, Next.js marketing site, and Mintlify docs.

## Project map

- `app/macos` — macOS app (Swift / SwiftUI)
- `app/windows` — Windows app (C# / WPF / .NET 10)
- `app/ios` — iOS app
- `backend-v2-flyio` — Fly.io edge transcription service (git submodule)
- `nextjs` — Next.js marketing & license website
- `mintlify-help` — Mintlify documentation site
- `shared-types` — TypeScript types shared across projects (Drizzle types)
- `shared-backup` — Cross-platform backup schema (JSON Schema + CLAUDE.md)
- `shared-prompts` — Post-processing prompt templates shared across platforms
- `shared-models` — Cross-platform per-model metadata catalog (`models-catalog.json`)
- `shared-app-classification` — App-type & cloud-STT catalogs (`app-type-catalog.json`, `cloud-stt-catalog.json`)
- `integrations` — External integrations (`hyperwhisper-mcp`)
- `tasks` — Development tasks & planning (file under `windows/` or `macos/`)
- `tools` — Native build scripts (parakeet-engine, sherpa-onnx)
- `commands` — Platform-specific CLI utilities
- `routines` — Scheduled automation routines

<important if="you are adding, rotating, or referencing any secret, API key, or credential">

**Infisical is the single source of truth for all secrets**, and auto-syncs out to:

- **GitHub Actions** — Production (and Preview) **environment** secrets, NOT repo-level. CI jobs that need them must declare `environment: Production` (e.g. `macos-release` / `windows-release`).
- **Vercel** — `nextjs` env vars (prod + preview).
- **Fly.io** — `backend-v2-flyio` runtime secrets.

Rotate or add a secret **in Infisical only** — never edit GitHub/Vercel/Fly directly or the next sync overwrites your change. Never commit secret values to the repo.
</important>

<important if="you are adding or modifying Mode properties, settings, or vocabulary fields on either platform">

Update the shared backup schema and field mappings in `shared-backup/` in the same change — its `CLAUDE.md` documents the required edits.
</important>

<important if="you just deployed and are about to check or tail Vercel logs">

Only tail Vercel logs when the change touched `nextjs/` or otherwise directly affects the Next.js/Vercel runtime. macOS / Windows / iOS / Fly.io backend / Mintlify docs / integrations / routines / shared schemas / CI-only changes don't hit Vercel — skip log monitoring for those.
</important>
