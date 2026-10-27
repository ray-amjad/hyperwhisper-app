// Runs the real deploy smoke (scripts/smoke-test.ts) against a fake cloud and
// pins how its /post-process rows read the response. #2143: when the requested
// provider's output is rejected and nothing serves a retry, the route returns
// the input unchanged with X-LLM-Provider still naming the requested model, so
// the header alone passed a tier that post-processed nothing.

import { afterEach, describe, expect, test } from 'bun:test';
import { join } from 'node:path';
import { servedNameFor, type SttProviderId } from './lib/stt-models';
import { LLM_PROVIDER_NAMES } from './lib/llm-provider';

const SCRIPT = join(import.meta.dir, '..', 'scripts', 'smoke-test.ts');
const SMOKE_INPUT = 'hello world this is a ci smoke test';
const FIXED = 'Hello world, this is a CI smoke test.';

interface PostProcessReply {
  corrected: string;
  served?: string; // X-LLM-Provider; defaults to the requested provider's model
}

// `reply(provider, attempt)` answers the attempt-th /post-process call for that provider.
type Reply = (provider: string, attempt: number) => PostProcessReply;

let server: ReturnType<typeof Bun.serve> | undefined;

afterEach(() => {
  server?.stop(true);
  server = undefined;
});

function startFakeCloud(reply: Reply): string {
  const calls = new Map<string, number>();
  server = Bun.serve({
    port: 0,
    async fetch(req) {
      const url = new URL(req.url);
      if (url.pathname === '/health') return Response.json({ status: 'ok', region: 'test' });
      if (url.pathname === '/warmup') return new Response(null, { status: 204 });
      if (url.pathname === '/transcribe') {
        await req.arrayBuffer();
        const provider = req.headers.get('X-STT-Provider') as SttProviderId;
        return Response.json({ text: 'hello' }, { headers: { 'X-STT-Provider': servedNameFor(provider) } });
      }
      if (url.pathname === '/post-process') {
        const body = (await req.json()) as { text: string };
        expect(body.text).toBe(SMOKE_INPUT);
        const provider = req.headers.get('X-LLM-Provider') ?? '';
        const attempt = (calls.get(provider) ?? 0) + 1;
        calls.set(provider, attempt);
        const r = reply(provider, attempt);
        const served = r.served ?? LLM_PROVIDER_NAMES[provider as keyof typeof LLM_PROVIDER_NAMES];
        return Response.json({ corrected: r.corrected }, { headers: { 'X-LLM-Provider': served } });
      }
      return new Response('not found', { status: 404 });
    },
  });
  return `http://127.0.0.1:${server.port}`;
}

async function runSmoke(reply: Reply): Promise<{ code: number; out: string }> {
  const base = startFakeCloud(reply);
  const proc = Bun.spawn(['bun', 'run', SCRIPT], {
    env: { ...process.env, SMOKE_BASE_URL: base, SMOKE_LICENSE_KEY: 'smoke-test-fake', SMOKE_TIMEOUT_MS: '10000' },
    stdout: 'pipe',
    stderr: 'pipe',
  });
  const [stdout, stderr, code] = await Promise.all([
    new Response(proc.stdout).text(),
    new Response(proc.stderr).text(),
    proc.exited,
  ]);
  return { code, out: stdout + stderr };
}

describe('deploy smoke /post-process rows', () => {
  test('pass when every provider corrects the input', async () => {
    const { code, out } = await runSmoke(() => ({ corrected: FIXED }));
    expect(out).toContain('✓ POST /post-process openai — via openai-gpt-5.6-luna');
    expect(out).toContain('✓ Smoke test passed');
    expect(code).toBe(0);
  }, 30_000);

  test('fail when a provider returns the raw input under its own model name, twice', async () => {
    const { code, out } = await runSmoke((p) => ({ corrected: p === 'openai' ? SMOKE_INPUT : FIXED }));
    expect(out).toContain('! POST /post-process openai — fell back to the raw input');
    expect(out).toContain('✗ POST /post-process openai — fell back to the raw input');
    expect(out).toContain('(twice)');
    expect(out).toContain('✗ Smoke test FAILED (1 check)');
    expect(code).toBe(1);
  }, 30_000);

  test('fail when a provider returns an empty correction, twice', async () => {
    const { code, out } = await runSmoke((p) => ({ corrected: p === 'gemini' ? '  ' : FIXED }));
    expect(out).toContain('✗ POST /post-process gemini — fell back to the raw input');
    expect(code).toBe(1);
  }, 30_000);

  test('a single raw fallback is asked once more and passes on a corrected retry', async () => {
    const { code, out } = await runSmoke((p, attempt) => ({
      corrected: p === 'grok' && attempt === 1 ? SMOKE_INPUT : FIXED,
    }));
    expect(out).toContain('! POST /post-process grok — fell back to the raw input');
    expect(out).toContain('✓ POST /post-process grok — via xai-grok-4.3');
    expect(code).toBe(0);
  }, 30_000);

  test('a served model other than the requested one still fails, twice', async () => {
    const { code, out } = await runSmoke((p) => ({
      corrected: FIXED,
      served: p === 'mistral' ? LLM_PROVIDER_NAMES.groq : undefined,
    }));
    expect(out).toContain('✗ POST /post-process mistral — fell back — requested mistral');
    expect(code).toBe(1);
  }, 30_000);
});
