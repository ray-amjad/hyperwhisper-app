# HyperWhisper Cloud

This repository contains the source code of the **HyperWhisper Cloud** transcription service — the backend that the macOS, Windows, and iOS HyperWhisper apps talk to when a user opts into cloud transcription, cloud post-processing, or the screen-aware Assistant mode.

It is fully open source under **Apache-2.0** — the same license as the rest of HyperWhisper — and published here for **auditability**. If you use HyperWhisper Cloud, you can read every line of code that runs between your app and the upstream AI providers we proxy to. The goal is for a technically curious user to be able to verify, in roughly an afternoon, exactly what happens to their audio, their text, and their license key.

## Why use HyperWhisper Cloud

Think of it like **OpenRouter, but for cloud transcription models** — one account that fronts many providers, so you don't integrate and bill each one separately.

Local transcription is free and runs entirely on your machine — you never need Cloud. Cloud exists for people who want a **smoother billing experience**: most of the hosted cloud STT and LLM API models you'd otherwise sign up for one by one — Deepgram, Groq, ElevenLabs, Grok, Azure, Google, and more — are available through a single HyperWhisper Cloud subscription. Instead of juggling a separate API key, dashboard, and invoice for each provider, you get **one license key, one balance of credits, and one centralized invoice**, and you can **swap freely between models** without re-plumbing anything. You pay for the managed, hosted convenience — the source that powers it is all right here.

> If you only want to use the desktop app, you do not need anything in this repo. This is the server-side component.

## What this service does

It is a thin proxy in front of third-party speech-to-text and LLM providers, with three jobs layered on top:

1. **Routing & fallback** — pick a provider per request, retry against alternates if one is unavailable.
2. **License / credit accounting** — verify license keys against the HyperWhisper licensing API, deduct credits after each request, enforce per-IP daily quotas for trial users.
3. **Operational hygiene** — request IDs, timing logs, IP blocks for abusive traffic, prompt-leakage detection in post-processing output.

It does **not** store audio, transcripts, prompts, or LLM output. Requests are processed in memory and the response is returned to the client.

## Endpoints

All endpoints are served from `transcribe-prod-v2.hyperwhisper.com` (production), `transcribe-staging-v2.hyperwhisper.com` (staging), and `transcribe-dev-v2.hyperwhisper.com` (development).

| Method | Path | Purpose | Source |
|---|---|---|---|
| `GET` | `/health` | Fly.io health probe | `src/index.ts` |
| `GET` | `/warmup` | Pre-warm TLS/HTTP2 on hotkey-down (no-op 204) | `src/index.ts` |
| `POST` | `/transcribe` | Audio → text via Deepgram / Groq / ElevenLabs / xAI Grok | `src/routes/transcribe.ts` |
| `POST` | `/post-process` | Text cleanup via Cerebras / Groq / Anthropic / xAI | `src/routes/post-process.ts` |
| `POST` | `/assistant` | Screen-aware vision chat (SSE stream) | `src/routes/assistant.ts` |
| `GET` | `/usage` | Query credit balance and daily quota | `src/routes/usage.ts` |
| `GET` | `/ws/streaming-deepgram` | WebSocket streaming transcription (Deepgram passthrough) | `src/routes/ws-streaming-deepgram.ts` |

## Architecture

```
HyperWhisper app
      │
      ▼
Fly.io Anycast ──► Nearest of 17 regions (production)
      │
      ├─► Deepgram / Groq / ElevenLabs / xAI         (speech-to-text)
      ├─► Cerebras / Groq / Anthropic / xAI          (post-process LLM)
      ├─► Anthropic Claude                           (Assistant vision)
      │
      ├─► Upstash Redis      (license cache, trial credits, IP quotas, IP blocks)
      └─► hyperwhisper.com   (license validation + credit deduction)
```

The service is stateless. All persistent state lives in Upstash Redis (caching + counters) or in the licensing database behind `hyperwhisper.com/api/license/*`.

## Data handling

This is the part most users come here to check. The relevant code paths are linked.

- **Audio.** Received as a streaming HTTP body, buffered into memory, forwarded to the chosen STT provider over HTTPS. The buffer is discarded when the function returns. Audio is never written to disk and never sent anywhere except the upstream STT provider you selected. See `src/routes/transcribe.ts`.
- **Transcript text.** Returned to the client. For `/post-process`, the input text is forwarded to the chosen LLM provider, the corrected output is returned to the client, and both are discarded. See `src/routes/post-process.ts`.
- **Screenshots (Assistant mode).** Base64-encoded and forwarded to Anthropic Claude as a vision message; the request and response are streamed back to the client and discarded. See `src/routes/assistant.ts`.
- **License keys.** Sent over HTTPS as a query parameter or form field. The validation result (`{ isValid, credits }`) is cached in Upstash Redis for up to 1 hour to avoid hammering the licensing API on every request. Logs mask license keys to the first/last 4 characters. See `src/middleware/auth.ts`.
- **Device IDs (trial mode).** Used as an opaque key into a per-device credit counter in Redis. See `src/lib/redis.ts`.
- **Client IPs.** Used for daily per-IP free-tier quota tracking and for the IP block list. Stored in Redis under a date-scoped key that expires after the day rolls over. See `src/middleware/rate-limit.ts`.
- **Logs.** Structured JSON, emitted via `console.log` / `console.warn` and shipped to Axiom. They contain request IDs, byte counts, timing, provider names, masked license keys, and IPs — they do **not** contain audio, transcripts, prompts, or LLM output. See `src/lib/logging.ts`.

If you find a code path that contradicts the description above, please open an issue.

## Upstream providers

The service is a proxy. Once a request reaches the upstream provider, that provider's own privacy policy and data handling apply.

**Speech-to-text** (`src/providers/`)

- `deepgram.ts` — Deepgram Nova-3 (default). `mip_opt_out=true` is set on every request.
- `groq.ts` — Groq Whisper Large v3.
- `elevenlabs.ts` — ElevenLabs Scribe v2.
- `xai-stt.ts` — xAI Grok STT.

**Post-processing LLMs** (`src/providers/`)

- `cerebras.ts` — Cerebras `gpt-oss-120b` (default).
- `groq-llm.ts` — Groq `gpt-oss-120b`.
- `anthropic.ts` — Anthropic Claude Haiku 4.5.
- `xai-llm.ts` — xAI Grok.

**Assistant mode**

- `anthropic.ts` — Anthropic Claude (vision-capable model).

Provider selection is per-request via the `X-STT-Provider` / `X-LLM-Provider` header. Fallback chains are defined in `src/routes/transcribe.ts` and `src/lib/llm-provider.ts`.

## Credits and rate limiting

- **Licensed users.** Credits are read from the HyperWhisper licensing API and cached in Redis for up to 1 hour. After each request, actual cost is computed from the upstream provider's response (audio duration × per-minute price for STT; token counts × per-token price for LLMs) and deducted via `POST /api/license/credits` on the licensing server.
- **Trial users.** Credits are tracked per device ID in Redis with a fixed allocation (`TRIAL_CREDIT_ALLOCATION` in `src/lib/constants.ts`).
- **Anonymous / IP-level.** Trial users are additionally capped by a per-IP daily quota that resets at UTC midnight (`src/middleware/rate-limit.ts`).
- **Abuse.** IPs added to the Redis block list (`isIPBlocked` in `src/lib/redis.ts`) get a `403` on every endpoint.

Conversion constants — `CREDITS_PER_MINUTE`, `TRIAL_CREDIT_ALLOCATION`, `MAX_AUDIO_SIZE_BYTES`, cache TTLs — are all in `src/lib/constants.ts`.

## Deployment

The service runs on [Fly.io](https://fly.io) under three apps:

| Environment | URL | Fly app | Regions | Deployed by |
|---|---|---|---|---|
| Production | `transcribe-prod-v2.hyperwhisper.com` | `hyperwhisper-transcribe` | 17 regions | GitHub Actions |
| Staging | `transcribe-staging-v2.hyperwhisper.com` | `hyperwhisper-transcribe-staging` | 1 region (iad) | GitHub Actions pre-prod gate |
| Development | `transcribe-dev-v2.hyperwhisper.com` | `hyperwhisper-transcribe-dev` | 1 region (nrt) | manual / local |

VM size, region list, and HTTPS settings are in `fly.prod.toml`, `fly.staging.toml`, and `fly.dev.toml`. The container is defined in `Dockerfile` and runs Bun.

## Tech stack

- **Runtime:** [Bun](https://bun.sh)
- **Framework:** [Hono](https://hono.dev)
- **Language:** TypeScript
- **State:** [Upstash Redis](https://upstash.com)
- **Host:** [Fly.io](https://fly.io)
- **Logs:** Axiom (via Fly log shipper)

## Running locally

```bash
cp .env.example .env   # fill in provider API keys + Upstash + license API URL
bun install
bun run dev            # starts on :8080
bun run typecheck      # tsc --noEmit
```

You will need API keys for the providers you want to exercise and an Upstash Redis instance. Without a real licensing API, only the trial-credit path is meaningful end-to-end.

## Repository layout

```
src/
  index.ts              Hono app entry, route registration
  routes/               One file per HTTP endpoint
  providers/            One file per upstream provider (STT + LLM)
  middleware/           Auth, credits, rate limit
  lib/                  Constants, Redis client, logging, cost calculation,
                        text processing, response helpers
```

## Reporting issues

If you spot something that looks wrong — a privacy concern, a data path that contradicts this README, a security bug — please open an issue on this repo or email r@rayamjad.com.

## License

Apache License 2.0 — see [`LICENSE`](./LICENSE), the same license as the rest of HyperWhisper. The hosted **HyperWhisper Cloud** service (our managed instance, credits, and license keys) is a separate paid offering, even though the backend source that powers it is published here under this license.
