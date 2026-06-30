// Smoke test for HyperWhisper Cloud.
//
// Verifies a freshly-deployed Fly app is actually serving traffic before we
// promote it. Run by CI (.github/workflows/_deploy.yml) after every deploy,
// and locally against either environment:
//
//   SMOKE_BASE_URL=https://transcribe-dev-v2.hyperwhisper.com bun run scripts/smoke-test.ts
//
// Two tiers:
//   1. Unauthenticated liveness — /health + /warmup. Always run.
//   2. Authenticated end-to-end — /transcribe once per STT provider, plus
//      /post-process once per LLM provider. Only run when SMOKE_LICENSE_KEY is
//      set, since these need a valid license + spend a little credit.
//
// Provider coverage is NOT hand-maintained: the required STT set is derived from
// the server-side registry (lib/stt-models.ts → ALL_STT_PROVIDER_IDS), and a
// coverage guard (checkProviderCoverage) fails the run if any registered
// provider has no /transcribe check. Adding a provider to the registry therefore
// forces a smoke fixture here before it can ship.
//
// Any failed check is collected (we don't bail early, so one deploy run reports
// every dead provider at once) and the process exits non-zero — which fails the
// CI job and, for a prod push, blocks promotion past the preview smoke.

import { readFile } from 'node:fs/promises';
import { ALL_STT_PROVIDER_IDS, getProviderDef, type SttProviderId } from '../src/lib/stt-models';
import { LLM_PROVIDER_NAMES } from '../src/lib/llm-provider';

const BASE_URL = (process.env.SMOKE_BASE_URL || '').replace(/\/$/, '');
const LICENSE_KEY = process.env.SMOKE_LICENSE_KEY || '';
const DEVICE_ID = process.env.SMOKE_DEVICE_ID || 'ci-smoke-test';
const TIMEOUT_MS = Number(process.env.SMOKE_TIMEOUT_MS) || 60_000;

const FIXTURES_DIR = new URL('./fixtures/', import.meta.url).pathname;

interface SttCheck {
  provider: SttProviderId; // X-STT-Provider value
  file: string; // fixture in scripts/fixtures/
  language: string;
}

// Every STT provider in the registry gets exercised at least once (enforced by
// checkProviderCoverage below). The multilingual voice fixtures are spread
// across providers so nothing goes untested and the language coverage stays
// broad. All fixtures are mp3 — azure-mai accepts it (WAV/MP3/FLAC only); the
// async providers (google-chirp, assemblyai, soniox) take tiny clips so they
// finish well inside TIMEOUT_MS.
//
// NOTE: google-chirp requires region-qualified BCP-47 codes (`ja-JP`, not `ja`)
// — it 400s on a bare language subtag. Every other provider accepts the short
// ISO-639-1 form, so only the chirp rows are region-qualified.
const STT_CHECKS: SttCheck[] = [
  // Original cross-fallback trio + single-model providers.
  { provider: 'deepgram',     file: 'en-us-sarah.mp3',  language: 'en' },
  { provider: 'deepgram',     file: 'zh-roger.mp3',     language: 'zh' },
  { provider: 'groq',         file: 'en-gb-george.mp3', language: 'en' },
  { provider: 'groq',         file: 'it-alice.mp3',     language: 'it' },
  { provider: 'elevenlabs',   file: 'es-m.mp3',         language: 'es' },
  { provider: 'elevenlabs',   file: 'pt-eric.mp3',      language: 'pt' },
  { provider: 'grok',         file: 'fr-lily.mp3',      language: 'fr' },
  { provider: 'azure-mai',    file: 'de-brian.mp3',     language: 'de' },
  { provider: 'google-chirp', file: 'ja-yuki.mp3',      language: 'ja-JP' },
  { provider: 'google-chirp', file: 'hi-rashmi.mp3',    language: 'hi-IN' },
  // Proxy providers added in the multi-provider rollout. One run each so the
  // coverage guard is satisfied and a dead key/route fails the deploy.
  { provider: 'openai',       file: 'es-m.mp3',         language: 'es' },
  { provider: 'gemini',       file: 'fr-lily.mp3',      language: 'fr' },
  { provider: 'assemblyai',   file: 'de-brian.mp3',     language: 'de' },
  { provider: 'mistral',      file: 'it-alice.mp3',     language: 'it' },
  { provider: 'soniox',       file: 'pt-eric.mp3',      language: 'pt' },
];

// Every post-process LLM provider gets exercised once — like the STT set, this
// is NOT hand-maintained: it's derived from the server-side registry
// (lib/llm-provider.ts → LLM_PROVIDER_NAMES). Each entry maps a provider id to
// the served name the backend echoes in the X-LLM-Provider response header for
// that provider's DEFAULT model, used to detect a silent fallback to a
// different provider. Adding a provider to the registry therefore exercises it
// here automatically, and checkLLMProviderCoverage asserts the link can't drift.
const LLM_CHECKS: Record<string, string> = LLM_PROVIDER_NAMES;

// The X-STT-Provider response header echoes the served name as `base/model`
// (e.g. "deepgram/nova-3-general", "openai/gpt-4o-transcribe"). The base equals
// the provider id for every provider except grok, whose base label is
// "xai-grok". We check the served name starts with the requested provider's base
// so we can tell a model-name echo apart from a genuine fallback to another
// provider.
function expectedServedPrefix(provider: SttProviderId): string {
  return provider === 'grok' ? 'xai-grok' : provider;
}

// Self-only providers never fall back to a sibling, so the served provider MUST
// match what we requested — sourced from the registry so it stays in sync.
function isSelfOnly(provider: SttProviderId): boolean {
  return getProviderDef(provider).selfOnly;
}

if (!BASE_URL) {
  console.error('✗ SMOKE_BASE_URL is required (e.g. https://transcribe-dev-v2.hyperwhisper.com)');
  process.exit(2);
}

let failures = 0;

function pass(name: string, detail = ''): void {
  console.log(`✓ ${name}${detail ? ` — ${detail}` : ''}`);
}

function fail(name: string, detail: string): void {
  failures++;
  console.error(`✗ ${name} — ${detail}`);
}

async function fetchWithTimeout(url: string, init: RequestInit = {}): Promise<Response> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

function warn(name: string, detail: string): void {
  console.warn(`! ${name} — ${detail}`);
}

// Coverage guard — what makes a new provider impossible to ship untested.
// Derives the required set from the registry (ALL_STT_PROVIDER_IDS) and asserts
// every provider has a /transcribe check above. Adding a provider to
// lib/stt-models.ts fails this until a fixture row is added. Runs
// unconditionally (even without a license) so the gap is caught on every CI run.
function checkProviderCoverage(): void {
  const name = 'provider coverage';
  const covered = new Set<string>(STT_CHECKS.map((c) => c.provider));
  const missing = ALL_STT_PROVIDER_IDS.filter((p) => !covered.has(p));
  if (missing.length) {
    return fail(name, `STT providers in the registry with no /transcribe check: ${missing.join(', ')}`);
  }
  pass(name, `${ALL_STT_PROVIDER_IDS.length} registered STT providers all have a check`);
}

// LLM analogue of checkProviderCoverage. LLM_CHECKS is the registry map itself,
// so a gap is only possible if the map is overridden by hand — this asserts the
// post-process check set still equals the registry, and reports the count.
// Runs unconditionally so a registry/smoke drift is caught on every CI run.
function checkLLMProviderCoverage(): void {
  const name = 'LLM provider coverage';
  const registered = Object.keys(LLM_PROVIDER_NAMES);
  const covered = new Set(Object.keys(LLM_CHECKS));
  const missing = registered.filter((p) => !covered.has(p));
  if (missing.length) {
    return fail(name, `LLM providers in the registry with no /post-process check: ${missing.join(', ')}`);
  }
  pass(name, `${registered.length} registered post-process providers all have a check`);
}

async function checkHealth(): Promise<void> {
  const name = 'GET /health';
  try {
    const res = await fetchWithTimeout(`${BASE_URL}/health`);
    if (res.status !== 200) return fail(name, `expected 200, got ${res.status}`);
    const body = (await res.json()) as { status?: string; region?: string };
    if (body.status !== 'ok') return fail(name, `status !== "ok" (${JSON.stringify(body)})`);
    pass(name, `region=${body.region ?? '?'}`);
  } catch (err) {
    fail(name, String(err));
  }
}

async function checkWarmup(): Promise<void> {
  const name = 'GET /warmup';
  try {
    const res = await fetchWithTimeout(`${BASE_URL}/warmup`);
    if (res.status !== 204) return fail(name, `expected 204, got ${res.status}`);
    pass(name);
  } catch (err) {
    fail(name, String(err));
  }
}

async function checkTranscribe(check: SttCheck): Promise<void> {
  const name = `POST /transcribe ${check.provider} [${check.language}]`;
  try {
    const audio = await readFile(`${FIXTURES_DIR}${check.file}`);
    const qs = new URLSearchParams({
      license_key: LICENSE_KEY,
      device_id: DEVICE_ID,
      language: check.language,
    });
    const res = await fetchWithTimeout(`${BASE_URL}/transcribe?${qs}`, {
      method: 'POST',
      headers: { 'Content-Type': 'audio/mpeg', 'X-STT-Provider': check.provider },
      body: audio,
    });
    if (res.status !== 200) {
      const text = await res.text().catch(() => '');
      return fail(name, `expected 200, got ${res.status} ${text.slice(0, 200)}`);
    }
    const body = (await res.json().catch(() => ({}))) as { text?: string };
    const transcript = (body.text ?? '').trim();
    if (!transcript) return fail(name, 'empty transcript');

    // The response echoes the served model name; check it starts with the
    // requested provider's prefix. A mismatch means a genuine fallback to a
    // different provider — impossible (and a real bug) for self-only providers,
    // legitimate but worth surfacing for the rest.
    const served = res.headers.get('X-STT-Provider') || '?';
    const prefix = expectedServedPrefix(check.provider);
    if (!served.startsWith(prefix)) {
      const msg = `requested ${check.provider}, served by ${served}`;
      if (isSelfOnly(check.provider)) return fail(name, msg);
      warn(name, `fell back — ${msg}`);
    }
    pass(name, `via ${served}: ${JSON.stringify(transcript.slice(0, 50))}`);
  } catch (err) {
    fail(name, String(err));
  }
}

async function checkPostProcess(provider: string, expectedModel: string): Promise<void> {
  const name = `POST /post-process ${provider}`;
  try {
    const res = await fetchWithTimeout(`${BASE_URL}/post-process`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-LLM-Provider': provider },
      body: JSON.stringify({
        license_key: LICENSE_KEY,
        device_id: DEVICE_ID,
        text: 'hello world this is a ci smoke test',
        prompt: 'Fix capitalization and punctuation. Return only the corrected text.',
      }),
    });
    if (res.status !== 200) {
      const text = await res.text().catch(() => '');
      return fail(name, `expected 200, got ${res.status} ${text.slice(0, 200)}`);
    }
    // X-LLM-Provider echoes the model name actually used; a mismatch means the
    // requested provider was unhealthy and the chain fell back.
    const served = res.headers.get('X-LLM-Provider') || '?';
    if (served !== expectedModel) {
      warn(name, `fell back — requested ${provider} (${expectedModel}), served ${served}`);
    }
    pass(name, `via ${served}`);
  } catch (err) {
    fail(name, String(err));
  }
}

console.log(`→ Smoke testing ${BASE_URL}`);
checkProviderCoverage();
checkLLMProviderCoverage();
await checkHealth();
await checkWarmup();

if (LICENSE_KEY) {
  console.log(`→ /transcribe across ${STT_CHECKS.length} runs covering every STT provider`);
  for (const check of STT_CHECKS) {
    await checkTranscribe(check);
  }
  console.log(`→ /post-process across every LLM provider`);
  for (const [provider, expectedModel] of Object.entries(LLM_CHECKS)) {
    await checkPostProcess(provider, expectedModel);
  }
} else {
  console.log('• SMOKE_LICENSE_KEY not set — skipping authenticated /transcribe + /post-process checks');
}

if (failures > 0) {
  console.error(`\n✗ Smoke test FAILED (${failures} check${failures === 1 ? '' : 's'})`);
  process.exit(1);
}
console.log('\n✓ Smoke test passed');
