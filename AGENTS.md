# AGENTS.md

> ⚠️ **This is the PUBLIC, open-source repository** (`ray-amjad/hyperwhisper-app`, Apache-2.0, world-readable). Everything committed here is public forever.
> - **Never** commit secrets, API keys, license keys, signing keys, customer data, personal paths/emails, or internal infra/business details. Secrets live in **Infisical only** (see the secrets note below) — it syncs to GitHub Actions / Vercel / Fly.
> - Internal-only material stays **out**: `.claude/`, `.codex/`, `app/ios/`, `tasks/`, `notes/`, `plans/` are gitignored — keep them that way. **Exception:** `.claude/skills/` **is** tracked (repo-shareable skills ship with the project). Since it's public, treat everything under `.claude/skills/` like any other committed file: **no secrets, API keys, tokens, or personal paths** in a skill's `SKILL.md` or scripts. `.env` files inside skills stay gitignored — put secrets there (or in Infisical), never inline.
> - HyperWhisper Cloud is the paid moat: entitlement is **enforced server-side**. Never add a client-side bypass, fake/test license key, or debug backdoor.

HyperWhisper — macOS / Windows / iOS speech-to-text app with a Fly.io transcription backend, Next.js marketing site, and Mintlify docs.

## Project map

- `app/macos` — macOS app (Swift / SwiftUI)
- `app/windows` — Windows app (C# / WPF / .NET 10)
- `app/ios` — iOS app
- `hyperwhisper-cloud` — Fly.io edge transcription service
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
- **Fly.io** — `hyperwhisper-cloud` runtime secrets.

Rotate or add a secret **in Infisical only** — never edit GitHub/Vercel/Fly directly or the next sync overwrites your change. Never commit secret values to the repo.
</important>

<important if="you are adding or modifying Mode properties, settings, or vocabulary fields on either platform">

Update the shared backup schema and field mappings in `shared-backup/` in the same change — its `CLAUDE.md` documents the required edits.
</important>

<important if="you regenerated the UniFFI bindings in shared-core-rs/bindings">

`shared-core-rs/bindings/` is the only place a binding is authored. Never vendor a copy of the **C#** binding — every .NET head compiles it once, through `app/shared-dotnet/HyperWhisper.SharedCore`. The macOS **Swift** copies (`app/macos/hyperwhisper/RustCore/hyperwhisper_core.swift`, `app/macos/hyperwhisper/Libraries/hyperwhisper_coreFFI.h`) stay, because `project.pbxproj` references those paths, but they must be byte-identical to the source.

```bash
tools/check-binding-drift.sh --fix   # refresh the Swift copies, then commit them with the source
tools/check-binding-drift.sh         # what CI runs (.github/workflows/binding-drift.yml)
```

`shared-core-rs/build-bindings.sh` runs the `--fix` step for you.
</important>

<important if="you are adding, editing, or reverting a Drizzle migration in nextjs/drizzle">

**Migrations are append-only once they are merged.** The migrator keeps one watermark and runs a migration only when its journal `when` is greater than the last applied one. So editing an already-merged migration's SQL never runs in production, and a new entry stamped with a `when` that is not past every entry above it is skipped for good. Both failures are silent.

- To undo a migration, add a **new** one (next number, fresh `when` from `date +%s000`) that reverses it. Never edit, rename, delete, or re-stamp an existing entry.
- CI enforces this on every pull request (`.github/workflows/drizzle-journal.yml`).
- Run the same check locally before you push:

```bash
BASE_REF=origin/main python3 .github/scripts/validate_drizzle_journal.py
```
</important>

<important if="you just deployed and are about to check or tail Vercel logs">

Only tail Vercel logs when the change touched `nextjs/` or otherwise directly affects the Next.js/Vercel runtime. macOS / Windows / iOS / Fly.io backend / Mintlify docs / integrations / routines / shared schemas / CI-only changes don't hit Vercel — skip log monitoring for those.
</important>
