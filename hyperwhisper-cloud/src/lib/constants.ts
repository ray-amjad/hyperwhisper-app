// SHARED CONSTANTS

// Credit system
export const CREDITS_PER_MINUTE = 6.3; // Derived from production usage logs

// License cache TTL (seconds)
export const LICENSE_CACHE_TTL_SECONDS = 60 * 60; // 1 hour for valid and invalid keys

// Audio limits
export const MAX_AUDIO_SIZE_BYTES = 2 * 1024 * 1024 * 1024; // 2GB
export const BYTES_PER_MINUTE_ESTIMATE = 480_000; // 64kbps encoded audio

// Google Speech V2 sync `recognize` inline cap (also re-exported from
// `providers/google-chirp.ts` for back-compat with existing call sites).
export const GOOGLE_CHIRP_INLINE_MAX_BYTES = 9_500_000;

// Gemini inline-audio cap: total request (incl. base64) must stay under 20 MB
// and base64 inflates ~33%, so raw audio is capped at ~14 MB. Checked against
// Content-Length BEFORE buffering so an oversized upload is rejected early
// rather than after buffering up to MAX_AUDIO_SIZE_BYTES on the Fly machine.
export const GEMINI_INLINE_MAX_BYTES = 14 * 1024 * 1024;

// OpenAI hard-rejects audio over 25 MB with a 400. Gate on Content-Length
// BEFORE buffering so an oversized upload is rejected early as a 413 rather
// than after allocating the buffer. Shared with the adapter (defense-in-depth).
export const OPENAI_INLINE_MAX_BYTES = 25 * 1024 * 1024;

// Azure MAI-Transcribe accepts payloads up to ~300 MB across its multipart
// upload — beyond that the gateway 413s with a generic "Request entity too
// large" before any model code runs.
export const AZURE_MAI_MAX_BYTES = 300 * 1024 * 1024;

// `/assistant` coarse total-body guard, checked against Content-Length BEFORE
// buffering so a multi-hundred-MB upload is rejected early (the body is fully
// buffered by formData() on a 1 GB Fly machine). Finer per-image / per-messages
// caps below bound Anthropic vision spend after buffering.
export const MAX_ASSISTANT_BODY_BYTES = 25 * 1024 * 1024; // 25 MB
// Multipart `image` file cap.
export const MAX_ASSISTANT_IMAGE_BYTES = 10 * 1024 * 1024; // 10 MB

// Fly.io's dynamic request routing (`fly-replay`) only honours the replay
// header for requests with bodies ≤ 1 MB. Larger requests are silently
// executed in the original region instead of being replayed. Use 900 KB as
// the gate to leave headroom for any Fly-side accounting.
// Ref: https://fly.io/docs/networking/dynamic-request-routing/
export const FLY_REPLAY_MAX_BODY_BYTES = 900_000;

// Assistant (vision LLM) limits — guard against unbounded Anthropic spend from
// oversized or numerous client-supplied images. Clients embed images either as
// a multipart `image` file or inline as data:image/...;base64,... blocks inside
// the `messages` JSON; both paths must be capped.
//
// Total serialized `messages` payload. Sized to admit ASSISTANT_MAX_IMAGES inline
// images at ASSISTANT_MAX_INLINE_IMAGE_BYTES decoded (base64 ~4/3 expansion ≈
// 5.3 MB) plus text headroom, while still bounding inline-base64 abuse.
export const ASSISTANT_MAX_MESSAGES_BYTES = 6 * 1024 * 1024;
// Decoded size of any single inline base64 image forwarded to Anthropic.
export const ASSISTANT_MAX_INLINE_IMAGE_BYTES = 2 * 1024 * 1024;
// Maximum number of images forwarded to Anthropic per request (multipart +
// inline combined). Screen-aware assistant requests use one screenshot.
export const ASSISTANT_MAX_IMAGES = 2;

// API base
export const DEFAULT_API_BASE_URL = 'https://www.hyperwhisper.com';

// Hard timeout for every call to the Next.js license API (validate / credits).
// Without it a hung upstream (Vercel cold start, downstream DB stall, edge
// retry storm) leaves an unresolved `fetch` Promise pinning the request scope,
// which bloats the Bun worker until Fly OOM-kills it. 10s tolerates a cold
// start while still bounding the hang well under client/edge timeouts.
export const LICENSE_API_TIMEOUT_MS = 10_000;
