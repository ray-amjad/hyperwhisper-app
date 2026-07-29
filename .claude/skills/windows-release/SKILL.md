---
name: windows-release
description: "Windows release workflow — picks the version, writes user-facing release notes from git history, triggers the windows-release.yml GitHub Action to build/sign/publish both architectures, then verifies the update feed is actually live. Use when the user wants to cut a new Windows version, bump the Windows version, release a specific version like '1.8.2', or ship a Windows fix to users."
allowed-tools:
    - Bash
    - Read
    - Edit
    - Grep
model: sonnet
---

# Windows Release Workflow

Cutting a Windows release is a single GitHub Actions dispatch. `windows-release.yml`
does all the heavy lifting; this skill's job is to feed it good inputs, watch it, and
**verify the appcast actually reached users** — the step that has silently failed before.

## When to Use This Skill

- "release Windows", "new Windows version", "cut a Windows release"
- "release 1.8.2" (or any version), "bump the Windows version"
- "ship this Windows fix to users"

## What the Workflow Does (so you don't have to)

Everything below happens inside the Action — **do not do any of it by hand**:

1. Preflight (`verify_release_readiness.ps1`) — semver shape, notes contain `<li>`,
   requested version `>=` csproj version and `>` top appcast version, no existing
   local/remote tag or GitHub release
2. Bumps `<Version>` / `<AssemblyVersion>` / `<FileVersion>` in `HyperWhisper.csproj`
3. Builds the Rust shared core for **both** `x86_64-pc-windows-msvc` and
   `aarch64-pc-windows-msvc`, copies the DLLs into `Resources/rust-core/`
4. Runs smoke tests (FFI checksum handshake catches binding ↔ DLL drift)
5. Builds both installers via `build-release.ps1 -Architecture both`
6. Authenticode-signs both via SSL.com eSigner (CodeSignTool + self-computed TOTP)
7. Ed25519-signs both with NetSparkle, derives the public key from the seed and
   asserts it matches `Ed25519PublicKey` in `UpdateService.cs`
8. Uploads both installers to Cloudflare R2 (`builds.hyperwhisper.com`)
9. Prepends ARM64 + x64 `<item>` blocks to `nextjs/public/appcast-windows.xml`
10. Validates the appcast (`nextjs/scripts/validate-windows-appcast.js`)
11. Commits the bump + appcast and lands it on `main` via an auto-merged PR
12. Creates a published GitHub Release tagged `windows/vX.Y.Z` with both installers
13. Verifies the version actually went live on the update feed and that both
    installers download (`nextjs/scripts/check-appcast-live.js`) — the run fails if
    the release was published but not delivered

You never bump the version, edit the appcast, or commit before dispatching.

## Prerequisites

- `gh` CLI authenticated (`gh auth status`)
- On `main`, up to date (`git pull`)
- No existing `windows/vX.Y.Z` tag (the Action's preflight also checks this)
- Secrets live in Infisical and sync to the **Production** environment — the job
  declares `environment: Production`, so a missing-secret failure means Infisical,
  not the workflow

## Instructions

### Step 1 — Version

```bash
grep -m1 "<Version>" app/windows/HyperWhisper/HyperWhisper.csproj
grep -m1 -o "<sparkle:shortVersionString>[0-9.]*</sparkle:shortVersionString>" nextjs/public/appcast-windows.xml
```

Must be strictly greater than the appcast's top version. Patch bump unless the user
says otherwise.

### Step 2 — Work out what's actually in the release

The monorepo ships five products; only `app/windows/` reaches Windows users. Filter
hard, or you will write notes about a macOS fix.

```bash
git tag -l "windows/*" | sort -V | tail -1
git log <LAST_TAG>..HEAD --oneline --no-merges -- app/windows/
```

If that returns nothing, **there is no release to cut** — say so instead of shipping
a no-op build. Changes to `.github/workflows/`, `nextjs/`, `integrations/`,
`mintlify-help/`, or `app/macos/` do not ship in the Windows binary.

### Step 3 — Confirm the build is green

The Windows PR gate (`windows-ci.yml`) compiles the app and runs smoke tests. Check it
passed on the commit you're about to release — a red or absent run means the release
build is the first real compile, and it will fail 20 minutes in.

```bash
gh run list --workflow=windows-ci.yml --limit=5 \
  --json headSha,event,status,conclusion \
  -q '.[] | "\(.event) \(.status)/\(.conclusion) \(.headSha[0:8])"'
git rev-parse --short HEAD
```

### Step 4 — Release notes (appcast HTML)

The workflow wraps these in `<h2>What's New in X.Y.Z</h2><ul>…</ul>`, so provide
**bare `<li>` items only** — no `<ul>`, and no `<b>` header (that's the macOS format).
The preflight rejects notes without `<li`.

Content rules are shared with the macOS skill — read
[../macos-release/references/content-guide.md](../macos-release/references/content-guide.md)
and [../macos-release/references/style-guide.md](../macos-release/references/style-guide.md).
The essentials:

- 3–7 bullets ranked by user impact; lead with the most exciting change, end with
  fixes. Never lead with a fix.
- Write for regular users. No internal mechanism names — a user has never heard of
  `WhisperProcessor`, `DisposeAsync`, UniFFI, or NetSparkle.
- Combine related commits; skip refactors, CI, docs, version bumps.
- A single-fix release gets one honest bullet naming the symptom the user saw. Don't
  pad it to hit the bullet count, and don't hide it behind "Bug fixes and improvements"
  — users searching for their exact error should recognise it.

### Step 5 — GitHub release notes (markdown)

Separately draft `github_release_body`. Same tone, but here it's *useful* to include
the literal error string in backticks so anyone searching the error lands on the
release. Omit empty sections:

```markdown
# Highlights
- **Feature Name**: the single most impactful change

# Features & Improvements
- ...

# Bug Fixes
- ...
```

### Step 6 — Confirm with the user

Show them and wait:
- Version X.Y.Z, and the one-line summary of what's in it
- The appcast `<li>` notes
- The GitHub markdown notes
- `skip_upload` — `false` ships; `true` builds and signs but skips R2, appcast,
  commit, and release (use for validating a green build without shipping)

### Step 7 — Dispatch

Put the notes in shell variables — they contain `<`, `>`, backticks, em dashes, and
newlines that get mangled if inlined into the `gh` call.

```bash
NOTES='<li>First change</li><li>Second change</li>'
GH_BODY='# Bug Fixes
- **Thing**: description'

gh workflow run windows-release.yml \
  -f version="X.Y.Z" \
  -f release_notes="$NOTES" \
  -f github_release_body="$GH_BODY" \
  -f skip_upload=false
```

### Step 8 — Monitor to completion

Expect ~35–40 minutes (two Rust targets, two installers, eSigner round-trips). Run
the watcher **in the background** so you're not blocking:

```bash
gh run list --workflow=windows-release.yml --limit=1 --json databaseId,status
gh run watch <RUN_ID> --exit-status --interval 30    # run_in_background: true
```

### Step 9 — Verify the release actually landed

Exit code 0 is not proof. Check all four artefacts:

```bash
gh release view windows/vX.Y.Z --json name,publishedAt,assets \
  -q '"\(.name) \(.publishedAt)\n\(.assets | map(.name) | join(", "))"'
curl -sI https://builds.hyperwhisper.com/HyperWhisper-X.Y.Z-x64-Setup.exe | head -1
curl -sI https://builds.hyperwhisper.com/HyperWhisper-X.Y.Z-arm64-Setup.exe | head -1
git pull --ff-only origin main    # the Action pushed the bump + appcast
```

### Step 10 — Verify the update feed is LIVE (do not skip)

<important>
Publishing the release does **not** deliver it. The Windows app polls
`https://www.hyperwhisper.com/appcast-windows.xml` (`UpdateService.cs`, `AppcastUrl`),
which is a static file in `nextjs/public/` — it only reaches users when **Vercel
deploys the Next.js site**. The Action commits the appcast to the repo and stops there.
A release can go green, with signed installers on GitHub and R2, while being delivered
to nobody.
</important>

The release workflow now runs this check itself and fails if the version never goes
live, so a green run is proof of delivery. Run it by hand only if you dispatched with
`skip_upload`, or if you want to re-confirm:

```bash
cd nextjs && node scripts/check-appcast-live.js --platform windows --version X.Y.Z
```

It polls the live appcast (cache-busted — a cached read can report a stale version for
days) and `HEAD`s both installer URLs. If it times out, users are not getting the
update. Diagnose the deploy side:

```bash
vercel ls hyperwhisper --scope ray-amjad --prod 2>&1 | head -6
```

A last production deploy older than the release commit is the smoking gun. Report it
to the user and ask before force-deploying — promoting to production is an
outward-facing action affecting the live site, and the site may contain unrelated
unshipped work.

### Step 11 — Report

Tell the user:
1. Release URL + both installer sizes
2. Appcast live check — pass or fail, explicitly
3. Anything that needs a human (stale production deploy, failed preview builds)

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Preflight: "must be greater than appcast top version" | Someone already released that version, or you read the csproj version instead of the appcast's. |
| Preflight: "Release notes must include HTML `<li>` items" | You passed plain text or a `<ul>` wrapper. Bare `<li>` items only. |
| Ed25519 "public key mismatch" | `NETSPARKLE_ED25519_PRIV` in Infisical no longer matches `Ed25519PublicKey` in `UpdateService.cs`. Never "fix" this by editing the constant — that orphans every installed copy of the app. |
| Authenticode signing fails | SSL.com eSigner credentials (`ES_*`) — the workflow computes TOTP itself because CodeSignTool's internal TOTP fails with this secret. |
| Build fails on the Rust core | `shared-core-rs/rust-toolchain.toml` pins the toolchain; the ARM64 cross target is added in the workflow. A missing DLL hard-errors via the `EnsureRustCoreDll` target. |
| PR merge fails at the end | The bump lands via auto-merged PR (`main` requires PRs). It retries 5×; check the `release/windows-vX.Y.Z` PR and merge manually. |
| Release is green but users don't see the update | Step 10. Almost always a stale Vercel production deploy. |
| Vercel production deploys silently stop happening | `env_vars_too_large` — Vercel caps a target's env vars at 64KB total and rejects the deployment at *creation*, so nothing appears in the dashboard, no email fires, and the project just goes quiet. Infisical syncs the whole secret set to Vercel Production, including large build secrets the Next.js app never reads, which is what pushes it over. The error only surfaces if you trigger a deploy manually. Fix by narrowing the Infisical → Vercel sync, **never** by deleting vars in Vercel (the next sync restores them). |
| `nextjs` preview deploys are red | Read the log before dismissing it. Previews used to fail for environment reasons and were safely ignored; that is no longer a standing excuse. |

## Key Files

| File | Purpose |
|------|---------|
| `.github/workflows/windows-release.yml` | The release Action (does everything) |
| `.github/workflows/windows-ci.yml` | PR gate — compile + smoke tests |
| `app/windows/HyperWhisper/HyperWhisper.csproj` | Current version (read-only here) |
| `app/windows/HyperWhisper/scripts/verify_release_readiness.ps1` | Preflight validation |
| `app/windows/build-release.ps1` | Installer build (Inno Setup, both arches) |
| `nextjs/public/appcast-windows.xml` | NetSparkle update feed (written by the Action) |
| `nextjs/scripts/check-appcast-live.js` | Delivery check — polls the *live* feed, verifies downloads |
| `app/windows/HyperWhisper/Services/UpdateService.cs` | `AppcastUrl` + embedded Ed25519 public key |
