# CloudKit Schema Promotion — Playbook

**When to read this:** You modified a Core Data entity that lives in the `Cloud` configuration (currently only `Vocabulary`) and you're about to ship a macOS release, or a user has reported vocabulary sync broken after installing an update.

**TL;DR:** `NSPersistentCloudKitContainer` creates record types in the CloudKit **Development** environment automatically the first time your app runs. But the **Production** environment — which is what every shipping user hits — is locked behind a manual promotion step. Ship without promoting and users see a silent `CKPartialFailure` loop.

---

## Which Core Data changes trigger this?

The `HyperWhisper.xcdatamodeld` has two configurations (see `PersistenceController.swift`):

- **Local** — `Transcript`, `Mode`, `RecordingSession`, `UsageTracking`. Device-only. **You do NOT need to promote anything for changes to these.**
- **Cloud** — `Vocabulary`. Mirrored to CloudKit via `NSPersistentCloudKitContainer`. **Any change here means schema promotion is required.**

Specifically, schema promotion is required when you:

- Add a new entity to the `Cloud` configuration
- Add/remove/rename an attribute on a `Cloud`-configured entity
- Add/remove/rename a relationship on a `Cloud`-configured entity
- Change the type of an existing attribute
- Add indexes used by CloudKit queries

If you're only adding a new *optional* attribute, CoreData's lightweight migration will handle the local side — but CloudKit still needs the new field declared in Production schema before it can store it.

---

## Symptoms of a missed promotion

After a user installs a release that changed the Cloud schema without promoting, you'll see:

**In the user's macOS logs** (via `log show --last 10m --predicate 'process == "HyperWhisper"'`):

```
CKError ... server message = "Cannot create new type CD_Vocabulary in production schema"
CKErrorDomain Code=2 (CKPartialFailure)
CKInternalErrorDomain Code=1011
_recoverFromPartialError: fatal errors were found
_requestAbortedNotInitialized
```

The container enters a reset loop: CoreData purges metadata → re-runs setup → hits the same schema rejection → loops forever until the process is killed. Even after restart, the same process state keeps recurring because the server schema genuinely doesn't have the new type.

**In the CloudKit Console UI:** the Development sidebar shows `Record Types – Modified`, `Indexes – Modified`, `Security Roles – Modified`. Production shows the **old** schema with no sign of the change.

---

## Fix: Option A — CloudKit Console (web UI)

This is the fallback when you can't run `cktool` (e.g. no Xcode available, or you need to handle it from a phone).

1. Go to https://icloud.developer.apple.com/dashboard/
2. Click **CloudKit Database**
3. Breadcrumb: select container `iCloud.com.hyperwhisper.hyperwhisper` and environment **Development**
4. In the bottom-left of the sidebar, click **Deploy Schema Changes…**
5. Review the diff dialog carefully:
   - Expected: one or more `Create` / `Modify` entries for your changed `CD_*` types
   - Expected: `Create N indexes` for any type you added/modified
   - Expected: `Modify _world`, `Modify _icloud`, `Modify _creator` role entries (standard — these always appear)
   - ❌ If you see `Delete` operations for types you didn't intend to remove, **cancel and investigate** — CloudKit Production is effectively forward-only and deletions are disruptive
6. Click **Deploy**. Promotion is near-instant for small schemas.
7. Verify: navigate to `/environments/PRODUCTION/types` and confirm your new record type(s) are listed.

**Gotcha:** the Development-side "Modified" labels disappear immediately after deploy. If you see them persist after clicking Deploy, the click didn't land — retry.

---

## Fix: Option B — `cktool` CLI (preferred for automation)

Apple ships `cktool` bundled with Xcode. This is what we'd use if we ever automate schema promotion in CI.

### Setup (one-time)

1. Generate a **Management token** in CloudKit Console → **Tokens & Keys** → create new → role: **Management**.
2. Save the token to a local file or secret store. Treat it like a credential — it can modify production schemas.

### Useful commands

Run `xcrun cktool --help` for the authoritative list — the subcommand names below may have drifted between Xcode versions.

```bash
# Inspect current Production schema
xcrun cktool export-schema \
  --team-id YOUR_TEAM_ID \
  --container-id iCloud.com.hyperwhisper.hyperwhisper \
  --environment PRODUCTION \
  --output-file /tmp/prod-schema.ckdb

# Inspect current Development schema (what's about to get promoted)
xcrun cktool export-schema \
  --team-id YOUR_TEAM_ID \
  --container-id iCloud.com.hyperwhisper.hyperwhisper \
  --environment DEVELOPMENT \
  --output-file /tmp/dev-schema.ckdb

# Diff them to see exactly what promotion would do
diff /tmp/prod-schema.ckdb /tmp/dev-schema.ckdb

# Promote Development → Production
xcrun cktool deploy-schema-changes \
  --team-id YOUR_TEAM_ID \
  --container-id iCloud.com.hyperwhisper.hyperwhisper \
  --environment PRODUCTION \
  --management-token "$CKTOOL_MANAGEMENT_TOKEN"
```

Your Apple Team ID and the container `iCloud.com.hyperwhisper.hyperwhisper` are hardcoded in the entitlements — if you're working on a different container, check `hyperwhisper-release.entitlements`.

---

## Why we don't automate this in the release workflow

It was proposed, but rejected for these reasons:

1. **Most releases don't touch the Cloud schema.** Automating would run the deploy step on every release, including ones where there's nothing to deploy. That's noise.
2. **Schema promotion is forward-only.** Production can add record types but can't remove them, so accidentally promoting a half-finished experimental schema is painful to recover from. A manual gate is a feature here, not friction.
3. **Management token sprawl.** Automating means adding `CKTOOL_MANAGEMENT_TOKEN` to GitHub secrets, which is another credential with production-mutation privileges rotating through CI. Not worth it for a ~yearly event.

If you're doing a release that *does* change the Cloud schema, treat schema promotion as a manual step in your release checklist, like notarization troubleshooting or TestFlight uploads.

---

## What CloudKit Console does and doesn't show

Relevant when you're inspecting a container and wondering "where's my data?":

- **You cannot see other users' records.** Every HyperWhisper user's vocabulary lives in their *own* Private Database under their own Apple ID. Apple's privacy model means the developer has no API path to those records. There is no "list all users" query.
- **Telemetry and logs are aggregated and PII-free.** Request counts, error codes, operation latencies — you see rates and percentiles, never per-user activity.
- **"Act As iCloud Account"** in the bottom-left lets you impersonate an account, but only one you own (signing in as yourself). Useful for querying your own test records.
- **Record queries only return records in databases you control:** the Public Database (HyperWhisper doesn't use one) or your own Private Database when you're acting as yourself.
- The dashboard is therefore useful for: inspecting **schema** (record type structure, indexes, roles), watching **error rates** in telemetry, reading **server-side request logs** to diagnose client issues, and querying **your own test data** when acting as yourself.

## History: the v2.33.0 incident (2026-04-11)

First release shipping `NSPersistentCloudKitContainer` for vocabulary sync. The full chain of mistakes, in the order they surfaced:

### Timeline

| Commit | What it did | What it broke / fixed |
|---|---|---|
| `fd600d3f` | feat(macos): iCloud vocabulary sync with opt-in toggle | Initial feature — added `Cloud` configuration to Core Data model, `NSPersistentCloudKitContainer` wiring, Settings toggle |
| `a09b60f7` | build(macos): wire Developer ID provisioning profile for CloudKit | Set `PROVISIONING_PROFILE_SPECIFIER` on Debug + Release, fixed dev entitlements to use `production` APS environment |
| `3c40ed59` | ci(macos-release): install provisioning profile before build | Added CI step to decode `PROVISIONING_PROFILE_BASE64` and verify iCloud container |
| `755db67e` | refactor(macos): move iCloud vocabulary sync toggle to Settings | UI polish, not load-bearing to the incident |
| `0ae04111` | **fix(macos): drop aps-environment entitlement for iCloud sync** | Fix #1 below |
| `6b717de5` | **fix(macos): add provisioningProfiles to ExportOptions.plist** | Fix #2 below |
| (web UI) | **Deploy Schema Changes → Production in CloudKit Console** | Fix #3 below |

### Fix #1 — Missing Push Notifications capability on the App ID

**What happened:** The first release attempt (workflow run `24275951405`) failed at the `Export archive` step with:

```
error: exportArchive "HyperWhisper.app" requires a provisioning profile with
the iCloud and Push Notifications features.
** EXPORT FAILED **
```

**Why:** `hyperwhisper-dev.entitlements` and `hyperwhisper-release.entitlements` both declared `com.apple.developer.aps-environment = production`. `NSPersistentCloudKitContainer` docs suggest enabling this so CloudKit can deliver silent pushes for near-real-time sync. But the App ID in the Apple Developer Portal had *never* had Push Notifications enabled — only iCloud CloudKit containers. When xcodebuild went to export, it looked for a provisioning profile that covered both iCloud AND Push Notifications, couldn't find one, and aborted.

Critically, the CI step `3c40ed59` *did* verify the installed profile contained `com.apple.developer.icloud-container-identifiers` — but it never checked for `aps-environment`, so the verification passed while the real problem sat invisible.

**Fix:** Removed `com.apple.developer.aps-environment` from both entitlements files. Vocabulary sync doesn't need real-time push — it's a word list that changes rarely, so `NSPersistentCloudKitContainer` falling back to launch-time + periodic polling is totally fine. This avoided an Apple Developer Portal round-trip (regenerate App ID capability → regenerate profile → rotate `PROVISIONING_PROFILE_BASE64` GitHub secret) and shipped the same day.

**Lesson:** For sync features with low update frequency (vocabulary, user preferences, bookmarks, etc.), skip `aps-environment` entirely. Reserve Push Notifications capability for features that genuinely need sub-second update propagation.

### Fix #2 — `ExportOptions.plist` missing `provisioningProfiles` dict

**What happened:** The second release attempt (run `24276309100`, triggered after Fix #1) failed at `Export archive` again, but with a *different* error:

```
error: exportArchive "HyperWhisper.app" requires a provisioning profile
with the iCloud feature.
```

Note the subtle difference: no more mention of Push Notifications (so Fix #1 had worked) — but now "requires a provisioning profile with the iCloud feature" despite the profile with iCloud being installed and verified by CI.

**Why:** `ExportOptions.plist` used `signingStyle: manual` but had no `provisioningProfiles` dict. With manual signing, xcodebuild does *not* auto-discover installed profiles — you must explicitly map bundle IDs to profile names in the export options. The profile was sitting right there in `~/Library/MobileDevice/Provisioning Profiles/` but xcodebuild didn't know to use it.

**Fix:** Added to `ExportOptions.plist`:

```xml
<key>provisioningProfiles</key>
<dict>
    <key>com.hyperwhisper.hyperwhisper</key>
    <string>HyperWhisper Developer ID</string>
</dict>
```

Third release attempt (`24276442055`) succeeded end-to-end: archive → export → notarize → staple → sign → appcast update → R2 upload → GitHub release. ~10 minutes total.

**Lesson:** Manual signing needs a full triple to work: (1) keychain identity installed, (2) provisioning profile installed in `~/Library/MobileDevice/Provisioning Profiles/`, and (3) `ExportOptions.plist` explicitly mapping the bundle ID to the profile's display name. Missing any one of the three and export fails with cryptic errors.

### Fix #3 — CloudKit schema not promoted to Production

**What happened:** v2.33.0 shipped successfully. User installed it, enabled iCloud Vocabulary Sync in Settings, and nothing visibly broke. But `log show --last 10m --predicate 'process == "HyperWhisper"'` revealed:

```
15:29:14  Successfully set up CloudKit integration for store ...
          iCloud.com.hyperwhisper.hyperwhisper:Production
15:29:17  CKErrorDomain Code=26 (zone not found)
          CKInternalErrorDomain Code=2036 (schema mismatch)
15:29:18  Successfully set up CloudKit integration (retry)
15:29:20  server message = "Cannot create new type CD_Vocabulary in production schema"
          CKErrorDomain Code=2 (CKPartialFailure)
          CKInternalErrorDomain Code=1011
15:29:20  _recoverFromPartialError: fatal errors were found
          _requestAbortedNotInitialized
```

The container authenticated fine, registered `CKDatabaseSubscription`, then tried to push a vocabulary record up and got rejected because `CD_Vocabulary` didn't exist in Production. CoreData entered a reset loop — purge metadata, retry setup, fail again — with the same result every cycle.

**Why:** `NSPersistentCloudKitContainer` creates record types in the **Development** environment automatically the first time the app runs with a new Core Data model. Production is explicitly locked behind manual promotion, for safety reasons inherited from CloudKit's 2014 design as a Parse-style BaaS. We'd tested vocabulary sync locally against Development during development but never promoted the schema to Production before shipping.

**Fix:** Navigated to https://icloud.developer.apple.com/dashboard/ → CloudKit Database → container `iCloud.com.hyperwhisper.hyperwhisper` → Development environment → "Deploy Schema Changes…" in the left sidebar. The confirm dialog showed:

- **Record Types (1):** Create `CD_Vocabulary` type
- **Indexes (1):** Create 16 indexes for `CD_Vocabulary`
- **Security Roles (3):** Modify `_world`, `_icloud`, `_creator` roles

Clicked Deploy. Schema appeared in Production within seconds. User force-quit and relaunched HyperWhisper. New process (PID 1282) re-ran container setup cleanly, ran `CKFetchDatabaseChangesOperation` and `CKModifyRecordsOperation` against Production successfully, zero `CKPartialFailure` / `CKErrorDomain Code=2` errors. Sync working end-to-end.

**Lesson:** This document's entire reason for existing. Any Core Data change that touches the `Cloud` configuration needs a CloudKit schema promotion step before the release ships — or, if forgotten, immediately after the first user hits the `CKPartialFailure` error. A promotion takes ~30 seconds in the web UI and has no downside since it's the exact same schema the client was already trying to use.

### What I'd do differently

1. **Make `3c40ed59`'s CI verification step more thorough.** It only checked `com.apple.developer.icloud-container-identifiers`. Should also verify `aps-environment` and any other capability-gated entitlements against the installed profile so mismatches fail loud at build-time instead of at the export step 8 minutes later.
2. **Add a pre-flight schema check before shipping Cloud-configuration changes.** Could be as simple as running `xcrun cktool export-schema --environment PRODUCTION` and diffing against the local `.xcdatamodeld`. If they differ, print a loud warning: "Production schema is out of date — promote via CloudKit Console before releasing."
3. **Document this flow *before* shipping the first Cloud-configuration feature**, not after hitting the `CKPartialFailure` loop in production. (This document, written in retrospect.)

All three issues were independently capable of breaking the release. Only the first was visible during the build; the second only surfaced at export time; the third only surfaced at runtime on an installed user's machine. If you're shipping Cloud-schema changes in the future, verify all three: **entitlements match provisioned capabilities, `ExportOptions.plist` maps the profile, schema is promoted to Production.**
