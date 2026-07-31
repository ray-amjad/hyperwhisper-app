---
name: macos-release
description: "macOS release workflow — generates user-facing release notes from git history, then triggers the macos-release.yml GitHub Action to build, sign, notarize, and publish. Use when the user wants to cut a new macOS version, bump the macOS version, release a specific version like '2.41.0', or generate/update the appcast."
allowed-tools:
    - Bash
    - Read
    - Edit
    - Grep
model: sonnet
---

# macOS Release Workflow

Cutting a macOS release is a single GitHub Actions dispatch. The `macos-release.yml`
workflow does all the heavy lifting; this skill's job is to feed it good inputs
(version, build number, release notes) and watch it to completion.

## When to Use This Skill

- "release macOS", "new macOS version", "cut a macOS release"
- "release 2.41.0" (or any version), "bump the macOS version"
- "generate the appcast" / "update the appcast"

## What the Workflow Does (so you don't have to)

Everything below happens inside the Action — **do not do any of it by hand**:

1. Bumps `MARKETING_VERSION` + `CURRENT_PROJECT_VERSION` in `project.pbxproj`
2. Builds (`xcodebuild archive`), uploads dSYMs to Sentry
3. Exports with Developer ID signing, creates a DMG
4. Codesigns → notarizes → staples the DMG
5. Sparkle Ed25519-signs the DMG
6. Prepends a new entry to `nextjs/public/appcast.xml`
7. Uploads the DMG to Cloudflare R2 (`builds.hyperwhisper.com`)
8. Commits the version bump + appcast and lands them on `main` via an
   auto-merged PR (the `main` ruleset requires PRs, so it can't push directly)
9. Creates a published GitHub Release tagged `macos/vX.Y.Z` with the DMG attached

You never bump the version, edit the appcast, or commit before dispatching.

## Prerequisites

- `gh` CLI authenticated (`gh auth status`)
- On `main`, up to date (`git pull`)
- No existing `macos/vX.Y.Z` tag for the target version
- Secrets are already configured (see [references/setup-guide.md](references/setup-guide.md)) —
  if the run fails on a missing-secret error, that guide explains what's needed.

## Instructions

### Step 1 — Version + build number

```bash
grep -E "MARKETING_VERSION|CURRENT_PROJECT_VERSION" app/macos/hyperwhisper.xcodeproj/project.pbxproj | head -4
```

- **Build number**: always the current one **+ 1**.
- **Version**: whatever the user asked for, else bump patch/minor from current.

### Step 2 — Release notes (appcast HTML)

The workflow injects `release_notes` straight into the appcast `<description>` that
users see in Sparkle's "What's New" dialog, so this is the one part that needs care.

```bash
# Previous macOS release tag, then macOS-only commits since then
git tag -l "macos/*" | sort -V | tail -1
git log <LAST_TAG>..HEAD --oneline --no-merges -- app/macos/
```

Format as HTML `<li>` items (no wrapping `<ul>` — the workflow adds it). Full rules,
examples, and an anti-jargon checklist live in
[references/content-guide.md](references/content-guide.md) and
[references/style-guide.md](references/style-guide.md). The essentials:

- **3–7 bullets**, ranked by user impact — lead with the most exciting change (new
  model, big perf win, redesign), end with bug fixes. Never lead with a fix.
- Write for regular users: what changed *for them*, not how it was built. No internal
  mechanism names (SSE, URLSession, Core Data, "cold-start tax", etc.).
- Combine related commits into one bullet; skip refactors, CI, docs, version bumps.
- If a commit adds a **new model, provider, major feature, or significant perf change**,
  it *must* appear — auto-generated notes have under-sold releases by dropping exactly
  these in favor of generic reliability bullets. Don't repeat that.
- If only backend/website/docs changed, use `<li>Bug fixes and improvements</li>`.

### Step 3 — GitHub release notes (markdown)

Separately, draft a structured markdown body for the GitHub release (`github_release_body`).
Same tone as the appcast; omit empty sections:

```markdown
# Highlights
- **Feature Name**: the single most impactful change

# Features & Improvements
- ...

# Bug Fixes
- ...
```

### Step 4 — Confirm with the user

Show them, and ask before dispatching:
- Version X.Y.Z (build NN)
- The appcast `<li>` notes
- The GitHub markdown notes

### Step 5 — Dispatch

Pre-flight, then run the workflow:

```bash
git branch --show-current            # must be "main"
git tag --list "macos/v$VERSION"     # must be empty
gh auth status

gh workflow run macos-release.yml \
  -f version="X.Y.Z" \
  -f build_number="NN" \
  -f release_notes="<li>Change 1</li><li>Change 2</li>" \
  -f github_release_body="# Highlights
- **Feature**: Description

# Bug Fixes
- Fixed something" \
  -f skip_upload=false
```

Dry-run (build + sign + notarize, but no R2 upload and no appcast push) — set
`skip_upload=true`. Useful to validate a green build without shipping.

### Step 6 — Monitor to completion

Always watch the run and report the outcome — don't just hand over a link.

```bash
sleep 5 && gh run list --workflow=macos-release.yml --limit=1 --json databaseId,status,createdAt
gh run watch <RUN_ID> --exit-status   # blocks; non-zero exit on failure
```

Provide the Actions URL too (resolve the repo dynamically so this skill stays portable):

```bash
REPO=$(gh repo view --json nameWithOwner -q .nameWithOwner)   # e.g. ray-amjad/hyperwhisper-app
echo "https://github.com/$REPO/actions/runs/<RUN_ID>"
```

### Step 7 — Report + after-release reminders

On success, tell the user and remind them to:
1. `git pull` — the Action pushed a commit (version bump + appcast).
2. Verify the appcast is live: `https://www.hyperwhisper.com/appcast.xml`.
3. Confirm the published GitHub Release has the DMG attached.

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `create-dmg` exits 2 | Normal ("no custom icon set"). The workflow tolerates it. |
| Notarization auth error | App-specific password expired — regenerate at account.apple.com and update the secret in Infisical. |
| Ed25519 signing fails | Workflow tries Sparkle's `sign_update`, then a CryptoKit fallback. If both fail, the `SPARKLE_ED25519_PRIVATE_KEY` secret is missing/wrong. |
| PR merge fails at the end | The workflow lands its commit via an auto-merged PR (0 approvals required on `main`). It retries 5×; if it still fails, check the PR it opened (`release/macos-vX.Y.Z`) and merge it manually. |
| Fails on a missing secret | See [references/setup-guide.md](references/setup-guide.md); secrets are managed in Infisical. |

## Key Files

| File | Purpose |
|------|---------|
| `.github/workflows/macos-release.yml` | The release Action (does everything) |
| `app/macos/hyperwhisper.xcodeproj/project.pbxproj` | Current version (read-only here) |
| `nextjs/public/appcast.xml` | Sparkle update feed (written by the Action) |
| `app/macos/ExportOptions.plist` | Developer ID export settings (Team ID injected from a secret at build time) |
