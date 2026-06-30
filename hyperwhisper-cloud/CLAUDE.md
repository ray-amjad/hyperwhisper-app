# CLAUDE.md

HyperWhisper Cloud — Bun + Hono transcription service on Fly.io. Routes audio across Deepgram / Groq / ElevenLabs / Grok / Azure MAI / Google Chirp with credit accounting via Upstash Redis and the Next.js license API.

## Project map

- `src/routes/transcribe.ts` — main `/transcribe` route, fallback chain, header gates
- `src/providers/` — per-upstream clients (`deepgram.ts`, `groq.ts`, `elevenlabs.ts`, `xai-stt.ts`, `azure-mai.ts`, `google-chirp.ts`) plus shared `types.ts`, `utils.ts`
- `src/lib/` — `constants.ts` (size caps + replay limit), `gcs-storage.ts`, `google-auth.ts`, `cost-calculator.ts`, `redis.ts`, `responses.ts`, `logging.ts`
- `src/middleware/` — `auth.ts`, `credits.ts`, `rate-limit.ts`
- `references/custom-vocab.md` — Deepgram vocabulary boosting rules

## Deployment environments

| Environment | URL | Fly app | Deployed by |
|-------------|-----|---------|-------------|
| Development | `transcribe-dev-v2.hyperwhisper.com` | `hyperwhisper-transcribe-dev` | manual / local (`fly.dev.toml`) |
| Staging     | `transcribe-staging-v2.hyperwhisper.com` | `hyperwhisper-transcribe-staging` | GitHub Actions pre-prod gate (`fly.staging.toml`) |
| Production  | `transcribe-prod-v2.hyperwhisper.com` | `hyperwhisper-transcribe` | GitHub Actions (`fly.prod.toml`) |

<important if="you need to build, typecheck, or deploy the backend">

| Command | What it does |
|---|---|
| `bun --bun ./node_modules/.bin/tsc --noEmit` | TypeScript typecheck |
| `fly deploy --config fly.dev.toml` | Deploy to dev (`hyperwhisper-transcribe-dev`) — manual/local |
| `fly deploy --config fly.staging.toml` | Deploy to staging (`hyperwhisper-transcribe-staging`) — normally CI-only |
| `fly deploy --config fly.prod.toml` | Deploy to prod (`hyperwhisper-transcribe`) |
| `fly logs --app hyperwhisper-transcribe-dev` | Tail dev logs |
| `fly logs --app hyperwhisper-transcribe-staging` | Tail staging logs |
| `fly logs --app hyperwhisper-transcribe` | Tail prod logs |

Iterate locally against dev; CI deploys staging as the pre-prod gate and smoke-tests it before promoting to prod.
</important>

<important if="you are about to set, change, rotate, or deploy Fly secrets / env vars on the staging or production apps, or a deploy is failing with `<VAR> not configured`">

Don't touch them with `fly secrets set` / `fly secrets deploy`. The Fly secrets on `hyperwhisper-transcribe-staging` and `hyperwhisper-transcribe` (provider API keys, Upstash, Google SA, license API URL, etc.) are **synced automatically from Infisical** — a manual change drifts from the source of truth and gets overwritten on the next sync. To add or rotate a secret, change it in Infisical and re-sync; that stages and deploys it to the Fly apps. If a deploy 500s with `<VAR> not configured`, the secrets are staged-but-not-deployed or out of sync — re-sync from Infisical rather than running `fly secrets deploy` by hand.
</important>

<important if="you are touching Deepgram vocabulary, the `initial_prompt` query param, or `buildInitialTranscriptionPrompt` in any client">

Read `references/custom-vocab.md`. Nova-3 only supports `keyterm` in monolingual mode; auto-detect mode silently drops `keywords` / `keyterm`. Client-side cap: 100 terms.
</important>

<important if="you are modifying the transcribe route, the request flow, or which clients hit the Fly host">

Client entry points that terminate at `/transcribe`:
- macOS: `app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/HyperWhisperCloudProvider.swift` (`buildInitialTranscriptionPrompt`) and `HyperWhisperRoutedTranscription.swift` (for Azure MAI / Google Chirp)
- Windows: `app/windows/HyperWhisper/Services/HyperWhisperCloudService.cs` and `HyperWhisperRoutedTranscriptionClient.cs`

Changes to query params (`account_key` / legacy `license_key`, `device_id`, `language`, `initial_prompt`, `mode`), `X-STT-Provider`, response shape, or error codes must land in clients in the same PR cycle.

The auth credential is accepted under **two param names**: `account_key` (canonical, preferred) and `license_key` (legacy alias). Every entry point (`transcribe`, `assistant`, `post-process`, `usage`, `ws-streaming-deepgram`) reads `account_key` first, then falls back to `license_key`, so installed native apps that still send `license_key` keep working. Both carry the same key string — they're aliases, not different credentials.
</important>

<important if="you are touching google-chirp.ts, gcs-storage.ts, or the GCS scratch bucket">

Speech V2 sync `recognize` enforces TWO caps: ≤ 10 MB AND ≤ ~60 s. Both rejections are 400 INVALID_ARGUMENT. `google-chirp.ts` gates inline on both — the duration gate uses `estimateAudioSeconds(byteLength, contentType)` with a 55 s budget. Logs `provider.inline_disqualified_by_duration` when a payload fits bytes but fails duration.

Delivery paths:
- ≤ 9.5 MB AND ≤ ~55 s estimated → inline base64 → sync `recognize` (~5–15 s)
- otherwise → GCS upload → `batchRecognize` LRO + polling (~15–300 s)

batchRecognize submits intentionally OMIT `processingStrategy`. The unset default is Google's IMMEDIATE path; the named `DYNAMIC_BATCHING` enum is the opposite — a deferred 24-hour queue. Don't re-add the field.

`batchRecognize` result shape gotcha: `totalBilledDuration` lives on `fileResult.metadata`, NOT `fileResult.transcript.metadata`. `normalizeSpeechResults` merges all three candidate metadata levels (transcript, file, operation) — preserve the merge when refactoring.

`delivery` log field: post-deploy values are `inline` or `gcs+batch` (was `gcs` historically). Axiom queries on `delivery == 'gcs'` need updating to `delivery in ['gcs', 'gcs+batch']`.

On unwind (throw or poll deadline) the provider issues a best-effort `operations.cancel` so Google stops billing — `provider.operation_cancelled` event. Residual orphan risk: if the *submit* RPC times out before reading `submitData.name`, we never learned the operation name; the GCS delete still runs and Google 404s on the scratch.

GCS upload timeout: `max(30 s, 1 s per 100 KB)`. Transient GCS failures (timeout, network error, 429, 5xx) route through `ProviderUnavailableError('GCS upload', ...)` so transcribe.ts surfaces 502 via the chain-fail path. Non-transient (403 bad IAM, 404 missing bucket) stay as plain `Error` to fail fast.

Pre-buffer size gate: `/transcribe` 413s before allocating an ArrayBuffer when `X-STT-Provider: google-chirp` + no bucket + Content-Length > 9.5 MB.
</important>

<important if="you are spinning up a new Fly app or env that needs the GCS scratch bucket">

1. Create a private GCS bucket in the same region as `GOOGLE_SPEECH_REGION` (default `global`). Suggested name: `hyperwhisper-stt-scratch`.
2. Grant the Speech service account on the bucket:
   - `roles/storage.objectAdmin` (write + delete)
   - `roles/speech.editor` is already on the SA at project level
3. Add a lifecycle rule: delete objects in prefix `stt-temp/` older than 1 day (durable backstop for any object that escapes the `finally` delete).
4. Set the Fly secret:
   ```bash
   fly secrets set GOOGLE_SPEECH_GCS_BUCKET=hyperwhisper-stt-scratch \
     --app hyperwhisper-transcribe-dev
   fly secrets set GOOGLE_SPEECH_GCS_BUCKET=hyperwhisper-stt-scratch \
     --app hyperwhisper-transcribe
   ```

Without the secret, Chirp falls back to inline-only and 413s any audio > 9.5 MB. No other code changes are required to enable the GCS+batch path.
</important>

<important if="you are touching azure-mai.ts or anything that routes audio to Azure MAI-Transcribe">

MAI-Transcribe 1.5 accepts only **WAV, MP3, FLAC**. Anything else (m4a, mp4, webm, opus, ogg, aac, wma) throws `UnsupportedAudioFormatError` BEFORE the multipart upload is built and surfaces as HTTP **415**. Logs `provider.unsupported_audio_format`. Client implication: macOS/Windows must pre-convert non-accepted formats before sending — usually to WAV.

Upload cap: 300 MB. Larger payloads throw `AudioTooLargeError('Azure MAI', ...)` at entry → HTTP 413.

`azure-mai` is a **self-only fallback chain** — no silent substitution to Deepgram/Groq. A 5xx surfaces as 502 with the upstream error preserved.

Ref: https://learn.microsoft.com/en-us/azure/ai-services/speech-service/mai-transcribe
</important>

<important if="you are touching the ElevenLabs provider, the fly-replay logic, or the FALLBACK_CHAINS map">

ElevenLabs is geo-blocked from `nrt` (JP), `bom` (IN), `maa` (IN) — the block surfaces as a 200 OK with a text/html FAQ page, NOT a JSON error. `transcribe.ts` sets a `fly-replay` header to `iad` when these regions handle an `elevenlabs` request.

Fly only honours replay for bodies ≤ 1 MB (we gate at 900 KB / `FLY_REPLAY_MAX_BODY_BYTES` for safety). Oversized uploads from a blocked region:
- log `transcribe.fly_replay_skipped_oversized`
- drop ElevenLabs from the active fallback chain
- execute the next provider (Deepgram → Groq) in-region

`google-chirp` and `azure-mai` are self-only chains; touching the chain map can break the "no silent substitution" contract.
</important>

<important if="you are touching a provider's data retention / logging / training opt-out behaviour, or adding a new provider">

The **public source of truth** for our retention/training posture is the docs site page `mintlify-help/data-privacy.mdx` (in the app repo, ships at hyperwhisper.com/docs/data-privacy) — keep it accurate when this behaviour shifts.

Controls applied in this backend: Deepgram `mip_opt_out=true` per request (also in `ws-streaming-deepgram.ts`); AssemblyAI `DELETE /v2/transcript/{id}` after fetch (`bestEffortDeleteTranscript`); Soniox async-artifact delete; Groq account-level ZDR; Gemini paid-tier (no training); OpenAI clean by default.

ElevenLabs zero-retention (`enable_logging=false`) is **enterprise-only** and gated behind `ELEVENLABS_ZERO_RETENTION` (default off) — we're on a standard plan, so it retains by default. Don't send the flag unconditionally; a standard account can have the request rejected. Grok and Mistral retain ~30 days with no self-serve opt-out (enterprise contract only).
</important>
