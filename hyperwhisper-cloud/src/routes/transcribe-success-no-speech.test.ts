// Issue #2634: a no_speech result from a TOKEN-billed upstream (Gemini
// :generateContent, OpenAI gpt-4o*-transcribe) carries the cost the upstream
// billed, and the real /transcribe route charges it. A per-minute model's
// no_speech stays free. Every request goes through the real route — real auth,
// real `validateCredits`, real `deductCredits` — with only the edges faked: the
// licence cache (`../lib/redis`), the upstreams and licence API (`fetch`), and
// the hold store (same harness as transcribe-credit-holds.test.ts).

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import { pcm16Wav } from '../test-support/wav';
import { createMemoryCreditHoldStore } from '../lib/credit-hold-store-memory';
import { stubEnv } from '../test-support/env';

mock.module('../lib/redis', () => ({
  // Full export surface: a partial redis mock is process-wide in bun and breaks
  // every suite that loads after this one.
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => ({ isValid: true, credits: 1000, cachedAt: 'cached' }),
  cacheLicense: async () => {},
}));

const { transcribeRoute } = await import('./transcribe');
const { drainPendingDeductions } = await import('../middleware/credits');
const { drainPendingHoldReleases, setCreditHoldStoreForTests } = await import('../middleware/credit-holds');

const originalFetch = globalThis.fetch;
const restores: Array<() => void> = [];

interface Charge { license_key: string; amount: number }
let charges: Charge[];
let upstreamBody: unknown;

function silentRequest(provider: string, model: string): Request {
  const audio = new Uint8Array(pcm16Wav(60));
  return new Request('http://localhost/transcribe?account_key=test-license&language=en', {
    method: 'POST',
    headers: {
      'Content-Type': 'audio/wav',
      'Content-Length': String(audio.byteLength),
      'X-STT-Provider': provider,
      'X-STT-Model': model,
    },
    body: audio,
  });
}

async function send(provider: string, model: string) {
  const app = new Hono();
  app.post('/transcribe', transcribeRoute);
  const res = await app.request(silentRequest(provider, model));
  const body = await res.json() as { text: string; no_speech_detected?: boolean; cost: { usd: number; credits: number } };
  await drainPendingDeductions(2000);
  await drainPendingHoldReleases(2000);
  return { status: res.status, body };
}

beforeEach(() => {
  charges = [];
  setCreditHoldStoreForTests(createMemoryCreditHoldStore());
  restores.push(stubEnv('GEMINI_API_KEY', 'test-gemini-key'), stubEnv('OPENAI_API_KEY', 'test-openai-key'));
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.startsWith('https://generativelanguage.googleapis.com/') || url.startsWith('https://api.openai.com/')) {
      return Response.json(upstreamBody);
    }
    if (url.includes('/api/license/credits')) {
      const charge = JSON.parse(String(init?.body)) as Charge;
      charges.push(charge);
      return Response.json({ credits_remaining: 1000 - charge.amount });
    }
    throw new Error(`Unexpected fetch: ${url}`);
  }) as unknown as typeof fetch;
});

afterEach(() => {
  setCreditHoldStoreForTests(null);
  globalThis.fetch = originalFetch;
  while (restores.length > 0) restores.pop()!();
});

describe('/transcribe charges a token-billed no_speech (#2634)', () => {
  test('gemini: silent audio is charged the tokens Google billed', async () => {
    upstreamBody = {
      candidates: [{ content: { parts: [{ text: '' }] } }],
      usageMetadata: {
        promptTokenCount: 2_020, // 60 s at 32 tokens/s + 100 prompt tokens
        promptTokensDetails: [{ modality: 'TEXT', tokenCount: 100 }, { modality: 'AUDIO', tokenCount: 1_920 }],
        candidatesTokenCount: 0,
        thoughtsTokenCount: 2_000,
      },
    };
    const { status, body } = await send('gemini', 'gemini-3.8-flash');
    expect(status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.cost.usd).toBeGreaterThan(0);
    expect(body.cost.credits).toBeGreaterThan(0);
    expect(charges).toHaveLength(1);
    expect(charges[0]).toMatchObject({ license_key: 'test-license', amount: body.cost.credits });
  });

  test('openai gpt-4o-mini-transcribe: silent audio is charged the tokens OpenAI billed', async () => {
    upstreamBody = { text: '', language: 'en', usage: { type: 'tokens', input_tokens: 6_000, output_tokens: 0 } };
    const { status, body } = await send('openai', 'gpt-4o-mini-transcribe-2025-12-15');
    expect(status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.cost.usd).toBeGreaterThan(0);
    expect(charges).toHaveLength(1);
    expect(charges[0]).toMatchObject({ license_key: 'test-license', amount: body.cost.credits });
  });

  test('openai gpt-transcribe (per minute): silent audio stays free', async () => {
    upstreamBody = { text: '', language: 'en', usage: { type: 'duration', seconds: 60 } };
    const { status, body } = await send('openai', 'gpt-transcribe');
    expect(status).toBe(200);
    expect(body.no_speech_detected).toBe(true);
    expect(body.cost.credits).toBe(0);
    expect(charges).toEqual([]);
  });
});
