---
name: linux-release
description: "Linux release workflow — bumps the version by hand (the Action will NOT do it for you), writes user-facing release notes from git history, triggers the linux-release.yml GitHub Action to build/package/publish the .deb, then verifies the release and download page actually landed. Use when the user wants to cut a new Linux release, bump the Linux version, release a specific version like '1.12.0', ship a Linux fix to users, or publish the Debian package / APT repository."
allowed-tools:
    - Bash
    - Read
    - Edit
    - Grep
model: sonnet
---

# Linux Release Workflow

Cutting a Linux release is a version bump **you land first**, then a single GitHub
Actions dispatch. This is the one thing that makes Linux different from the macOS and
Windows skills, and getting it backwards wastes a 30-minute build.

## When to Use This Skill

- "release Linux", "new Linux version", "cut a Linux release"
- "release 1.12.0" (or any version), "bump the Linux version"
- "ship this Linux fix to users", "publish the deb"

## The one rule that differs from macOS and Windows

<important>
`linux-release.yml` **does not bump the version**. It *validates* it. The run fails in
the first minute unless the version you dispatch already equals **both**:

- `<Version>` in `app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj`
- the top entry in `packaging/linux/debian/changelog`

So the order is: bump both files → land them on `main` via a PR → dispatch. The macOS
and Windows Actions bump for you; this one does not.
</important>

## Picking the version — it is not an independent counter

The Linux `<Version>` field is deliberately **pinned to the Windows app's version**.
A comment in the csproj says so directly, because it drifted once (Windows 1.11.0 vs
Linux 1.1.0) and the Linux About page then read "Version 1.1.0", which looks like a
truncated minor field rather than a real version.

That same field is what the release workflow reads as the package version. So the
Debian package version and the About-page version are the same number, and that number
follows Windows.

```bash
grep -m1 "<Version>" app/windows/HyperWhisper/HyperWhisper.csproj   # the target
grep -m1 "<Version>" app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj
git tag -l "linux/*" | sort -V | tail -1                            # last shipped tag
```

Expect the last `linux/vX.Y.Z` tag to look "behind" the csproj version — that is normal
and not a bug. Ship the version that matches Windows. If the user asks for a number
that breaks the sync, say what the comment says and let them decide; it is their call,
but they should make it knowing it reintroduces the drift bug.

## What the Workflow Does (so you don't have to)

1. Runs the required Linux quality gates (`linux-ci.yml`) and asserts they tested this
   exact commit
2. Validates version shape, csproj/changelog agreement, `main`-only publishing,
   non-empty notes, and that no tag or release already exists
3. Probes the R2 and (if deploying) APT SSH + GPG credentials **before** the long build,
   so a stale key fails in a minute rather than after 30
4. Builds the release Rust core for `x86_64-unknown-linux-gnu`
5. Publishes the self-contained Avalonia app plus the `parakeet-engine` daemon
6. Builds, tests, and `lintian`-lints the `.deb`
7. Generates a static APT repository (signed only when `sign_apt=true`)
8. Creates `SHA256SUMS`, publishes the `linux/vX.Y.Z` GitHub Release with the assets
9. Mirrors the assets to Cloudflare R2 (`builds.hyperwhisper.com`)
10. Optionally deploys the signed APT repository, then reads it back over HTTPS
11. Writes `nextjs/public/linux-latest.json` (the website download button) via an
    auto-merged PR

## Prerequisites

- `gh` CLI authenticated (`gh auth status`)
- On `main`, up to date (`git pull`)
- No existing `linux/vX.Y.Z` tag (the Action checks the remote too)
- Secrets live in Infisical and sync to the **Production** environment

## Instructions

### Step 1 — Work out what's actually in the release

Unlike Windows, a shared-code change genuinely ships on Linux: the workflow compiles
`shared-core-rs` into the package, and the catalogs are consumed at build time. So
filter wider than `app/linux/`:

```bash
LAST=$(git tag -l "linux/*" | sort -V | tail -1)
git log $LAST..HEAD --oneline --no-merges -- \
  app/linux/ packaging/linux/ tools/parakeet-engine-dotnet/ \
  shared-core-rs/ shared-models/ shared-app-classification/
```

Narrow to `app/linux/ packaging/linux/` to see what is unambiguously Linux-only. If
both come back empty, **there is no release to cut** — say so rather than shipping a
no-op build.

### Step 2 — Confirm the build is green

```bash
gh run list --workflow=linux-ci.yml --limit=5 \
  --json headSha,event,status,conclusion \
  -q '.[] | "\(.event) \(.status)/\(.conclusion) \(.headSha[0:8])"'
git rev-parse --short HEAD
```

The release dispatch reruns these gates anyway, but a red gate means the release fails
after you have already prepared the bump.

### Step 3 — Bump the version and land it on `main`

Both files, one commit, one PR. `main` requires a PR (0 approvals), so this cannot be
pushed directly.

The Debian changelog entry needs the maintainer line from the previous entry. Copy it
programmatically rather than retyping it — the address is redacted in tool output, and
a mistyped maintainer line fails `lintian`:

```bash
VERSION="X.Y.Z"
git checkout -b release/linux-v$VERSION-version-bump

MAINT=$(grep -m1 '^ -- ' packaging/linux/debian/changelog | sed -E 's/^ -- (.*)  .*$/\1/')
{
  echo "hyperwhisper ($VERSION) stable; urgency=medium"
  echo
  echo "  * One bullet per user-visible change, wrapped at ~76 columns."
  echo
  echo " -- ${MAINT}  $(date -u +'%a, %d %b %Y %H:%M:%S +0000')"
  echo
  cat packaging/linux/debian/changelog
} > /tmp/changelog.new && mv /tmp/changelog.new packaging/linux/debian/changelog
```

For the csproj, keep `<AssemblyVersion>` and `<FileVersion>` in step with Windows too —
copy those values across rather than typing them, for the same redaction reason:

```bash
python3 - <<'PY'
import re
win = open("app/windows/HyperWhisper/HyperWhisper.csproj").read()
asm = re.search(r"<AssemblyVersion>(.*?)</AssemblyVersion>", win).group(1)
fil = re.search(r"<FileVersion>(.*?)</FileVersion>", win).group(1)
p = "app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj"
c = open(p).read()
c = re.sub(r"<Version>.*?</Version>", "<Version>X.Y.Z</Version>", c, count=1)
c = re.sub(r"<AssemblyVersion>.*?</AssemblyVersion>", f"<AssemblyVersion>{asm}</AssemblyVersion>", c)
c = re.sub(r"<FileVersion>.*?</FileVersion>", f"<FileVersion>{fil}</FileVersion>", c)
open(p, "w").write(c)
PY
```

Stage **only** these two files — the working tree often carries unrelated local edits,
and sweeping them into a release commit is how unrelated work ships by accident:

```bash
git add app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj packaging/linux/debian/changelog
git status --short          # confirm nothing else is staged
git commit -m "release(linux): bump version to $VERSION, in step with Windows"
git push -u origin release/linux-v$VERSION-version-bump
gh pr create --base main --title "release(linux): bump version to $VERSION" --body "..."
gh pr merge --squash --auto --delete-branch
```

`gh pr merge --delete-branch` switches you back to `main` and fast-forwards it, so you
usually do not need a separate checkout afterwards. Verify before dispatching:

```bash
git show origin/main:app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj | grep '<Version>'
git show origin/main:packaging/linux/debian/changelog | head -1
```

### Step 4 — Release notes (plain markdown, no HTML)

There is no appcast here, so there is no `<li>` format. `release_notes` goes straight
to `gh release create --notes`. Content and tone rules are shared — read
[../macos-release/references/content-guide.md](../macos-release/references/content-guide.md)
and [../macos-release/references/style-guide.md](../macos-release/references/style-guide.md).

Match the shape of the previous release, which ends with an install section:

```bash
gh release view $LAST --json body -q '.body'
```

````markdown
# Highlights
- **Feature Name**: the single most impactful change

# Features & Improvements
- ...

# Bug Fixes
- ...

# Install

Download `hyperwhisper_X.Y.Z_amd64.deb`, then:

```bash
sudo apt install ./hyperwhisper_X.Y.Z_amd64.deb
```

Check the download against `SHA256SUMS` first if you want to.
````

### Step 5 — Choose the publish flags, and confirm with the user

| Input | Meaning |
|---|---|
| `publish_github` | `true` ships: tag, GitHub Release, R2 mirror, website version file. `false` is a dry run that uploads a 14-day artifact for physical testing. |
| `sign_apt` | Signs the APT repository with the Production GPG key. When `false`, the APT archive is **withheld** from the release, R2, `SHA256SUMS`, and `linux-latest.json`, so an unsigned repository never reaches a user. |
| `deploy_apt` | Deploys the signed repository to the live HTTPS origin. Requires `publish_github=true` **and** `sign_apt=true`, or the run fails immediately. |

Shipped releases so far have used `publish_github=true, sign_apt=false, deploy_apt=false`.
Check what the last release actually did before assuming; do not enable APT signing or
deployment on your own initiative, since it publishes a repository users add as a
trusted source.

Show the user the version, the notes, and the flags, and wait.

### Step 6 — Dispatch

Put the notes in a shell variable — they contain backticks, fenced code blocks, and
newlines that get mangled if inlined.

```bash
NOTES='# Highlights
- **Thing**: description'

gh workflow run linux-release.yml \
  -f version="X.Y.Z" \
  -f publish_github=true \
  -f sign_apt=false \
  -f deploy_apt=false \
  -f release_notes="$NOTES"
```

### Step 7 — Monitor to completion

Expect ~30 minutes. Run the watcher **in the background** so you are not blocking:

```bash
gh run list --workflow=linux-release.yml --limit=1 --json databaseId,status
gh run watch <RUN_ID> --exit-status --interval 30    # run_in_background: true
```

### Step 8 — Verify the release actually landed

Exit code 0 is not proof. Check the release, the mirror, and the website file:

```bash
gh release view linux/vX.Y.Z --json name,publishedAt,assets \
  -q '"\(.name) \(.publishedAt)\n\(.assets | map(.name) | join(", "))"'
curl -sI https://builds.hyperwhisper.com/hyperwhisper_X.Y.Z_amd64.deb | head -1
git pull --ff-only origin main
cat nextjs/public/linux-latest.json
```

<important>
`linux-latest.json` is a static file in `nextjs/public/`, so the download page only
shows the new version once **Vercel deploys the Next.js site**. The Action commits the
file and stops there. Same failure mode as the Windows appcast: the release can be
green and complete while the download page still advertises the old version.
</important>

If the site is stale, check the deploy side and report it rather than force-deploying —
promoting to production is outward-facing and the site may carry unrelated unshipped
work:

```bash
vercel ls hyperwhisper --scope ray-amjad --prod 2>&1 | head -6
```

### Step 9 — Report

1. Release URL and asset list
2. Whether the `.deb` is downloadable from R2
3. Whether the download page went live, explicitly
4. Anything needing a human (stale deploy, withheld APT archive)

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| "Requested version X must match the Linux project (Y) and Debian changelog (Z)" | Step 3 was skipped or only half-landed. The Action never bumps; you do. |
| "Publishing is allowed only from refs/heads/main" | Dispatched from a branch. Re-dispatch from `main`. |
| "APT deployment requires publish_github=true and sign_apt=true" | `deploy_apt` cannot stand alone. |
| "Non-empty release notes are required when publishing" | `release_notes` was empty or whitespace. |
| "Remote tag already exists" | That version already shipped. Pick the next one. |
| R2 or APT credential probe fails | The key is stale. Rotate it **in Infisical** (`--env=prod`) and wait for the sync. Editing the GitHub secret directly is overwritten by the next sync. |
| `lintian --fail-on warning` fails | Usually a malformed changelog entry — check the maintainer line and the trailing date format. |
| The release shipped without the APT archive | Expected when `sign_apt=false`. The archive is withheld on purpose so an unsigned repository never reaches users. |
| Website PR did not merge | The release itself shipped. Merge `release/linux-vX.Y.Z-site` by hand to update the download page. |

## Key Files

| File | Purpose |
|------|---------|
| `.github/workflows/linux-release.yml` | The release Action |
| `.github/workflows/linux-ci.yml` | Required quality gates, reused by the release |
| `app/linux/HyperWhisper.Linux/HyperWhisper.Linux.csproj` | Version — bump by hand, in step with Windows |
| `packaging/linux/debian/changelog` | Package version — must match the csproj exactly |
| `packaging/linux/scripts/build-deb.sh` | Builds the `.deb` |
| `packaging/linux/scripts/generate-apt-repository.sh` | Builds the static APT repository |
| `packaging/linux/release-evidence/<version>.json` | Optional reviewed hardware evidence, attached when present |
| `nextjs/public/linux-latest.json` | Website download button (written by the Action) |
