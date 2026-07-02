# macOS Release Workflow — Setup Guide

The `macos-release.yml` Action reads its credentials from **GitHub Actions
Production-environment secrets**. This repo is public, so **no secret values live
here** — they are managed in **Infisical**, which syncs them out to the GitHub
`Production` environment (and Vercel / Fly for other services). Rotate or add a
secret **in Infisical only**; editing GitHub directly gets overwritten on the next
sync. The `macos-release` job declares `environment: Production` so it can read them.

If a run fails with a missing-secret or auth error, this table tells you which
secret is involved. To fix, update it in Infisical and re-run.

## Secrets the workflow uses

| Secret | What it's for |
|--------|---------------|
| `DEVELOPER_ID_P12_BASE64` | Base64 of the "Developer ID Application" cert + private key (.p12), used to codesign the app and DMG. |
| `DEVELOPER_ID_P12_PASSWORD` | Password protecting that .p12. |
| `PROVISIONING_PROFILE_BASE64` | Base64 provisioning profile — required for the CloudKit entitlement. |
| `PROVISIONING_PROFILE_UUID` | UUID the profile is installed under. |
| `APPLE_ID` | Apple ID email used for notarization. |
| `APP_SPECIFIC_PASSWORD` | App-specific password for `notarytool`. **These expire** — regenerate at account.apple.com if notarization fails with an auth error. |
| `APPLE_TEAM_ID` | Apple Developer Team ID. Injected into `ExportOptions.plist` at build time (the plist ships with a `YOUR_TEAM_ID` placeholder so the real ID stays out of source). |
| `SENTRY_DSN` | Crash-reporting DSN, substituted into `Info.plist`. Empty for forks → crash reporting simply disabled. |
| `SENTRY_AUTH_TOKEN` | Uploads dSYMs to Sentry. Regenerate under Sentry → Settings → Auth Tokens. |
| `SPARKLE_ED25519_PRIVATE_KEY` | Ed25519 seed that signs the DMG for Sparkle auto-update. **Never regenerate** — the matching public key is baked into the shipped app's `SUPublicEDKey`, so rotating it breaks auto-update for every existing user. |
| `CLOUDFLARE_ACCOUNT_ID` | R2 account for the DMG upload. |
| `CLOUDFLARE_R2_ACCESS_KEY_ID` | R2 API token — access key. |
| `CLOUDFLARE_R2_SECRET_ACCESS_KEY` | R2 API token — secret key. |
| `R2_BUCKET_NAME` | R2 bucket backing `builds.hyperwhisper.com`. |

## Notes

- **Sparkle key is the one you must never touch.** Everything else can be rotated;
  regenerating the Sparkle Ed25519 key orphans every installed copy from updates.
- The workflow imports the cert into a throwaway keychain and deletes it on exit, and
  injects the Team ID into `ExportOptions.plist` at runtime — so neither the cert nor
  the Team ID is ever committed.
- Verifying setup without shipping: dispatch with `-f skip_upload=true`. It builds,
  signs, and notarizes but skips the R2 upload and appcast push.
