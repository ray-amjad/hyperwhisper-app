// Tests for the `/assistant` request handler itself — the entitlement gate, the
// image-count-scaled credit pre-check, the image caps and the post-stream
// billing write. `assistant.test.ts` beside this file covers the pure helpers.
//
// Only `../lib/redis` is mocked, so the real `validateAuth` / `validateCredits`
// / `deductCredits` middleware runs and what is asserted here is the actual
// entitlement chain. Everything else is driven through global `fetch`, which is
// the boundary the license API and Anthropic both sit behind.
import { afterEach, beforeEach, describe, expect, mock, spyOn, test } from 'bun:test';
import { Hono } from 'hono';

import {
  ASSISTANT_MAX_IMAGES,
  ASSISTANT_MAX_MESSAGES_BYTES,
  MAX_ASSISTANT_BODY_BYTES,
  MAX_ASSISTANT_IMAGE_BYTES,
} from '../lib/constants';
import { computeAnthropicCost, creditsForCost } from '../lib/cost-calculator';
import * as creditsModule from '../middleware/credits';
import { drainPendingDeductions } from '../middleware/credits';

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };

let cachedLicenseValue: CachedLicense | null = null;
const blockedIPs = new Set<string>();

mock.module('../lib/redis', () => ({
  // Full export surface: a partial redis mock is process-wide in bun and breaks
  // every test file that loads after this one.
  redis: {
    get: () => {
      throw new Error('redis client should not be constructed in this test');
    },
  },
  isIPBlocked: async (ip: string) => blockedIPs.has(ip),
  getCachedLicense: async (_licenseKey: string) => cachedLicenseValue,
  cacheLicense: async () => {},
}));

const { assistantRoute } = await import('./assistant');

const ANTHROPIC_URL = 'https://api.anthropic.com/v1/messages';
const LICENSE_API_BASE = 'https://license.test.invalid';
const CLIENT_IP = '203.0.113.9';

const originalFetch = globalThis.fetch;
const originalAnthropicKey = process.env.ANTHROPIC_API_KEY;
const originalLicenseApiUrl = process.env.NEXTJS_LICENSE_API_URL;

interface AnthropicCall {
  system: string;
  messages: Array<{ role: string; content: unknown }>;
}

interface CreditCall {
  license_key: string;
  amount: number;
  metadata: Record<string, unknown>;
}

let anthropicCalls: AnthropicCall[] = [];
let creditCalls: CreditCall[] = [];
let licenseValidateCalls = 0;
let anthropicStreamEvents: unknown[] = [];
let anthropicStatus = 200;
/** Overrides the license API's answer to the billing write; null answers 200. */
let creditsResponder: (() => Response) | null = null;

function encodeAnthropicStream(events: unknown[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const event of events) {
        controller.enqueue(encoder.encode(`data: ${JSON.stringify(event)}\n\n`));
      }
      controller.close();
    },
  });
}

/** A minimal but realistic Anthropic streaming exchange with known token counts. */
function anthropicEvents(inputTokens: number, outputTokens: number, text: string): unknown[] {
  return [
    { type: 'message_start', message: { usage: { input_tokens: inputTokens } } },
    { type: 'content_block_delta', delta: { text } },
    { type: 'message_delta', usage: { output_tokens: outputTokens } },
  ];
}

function installFetch() {
  globalThis.fetch = mock(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : {};

    if (url.endsWith('/api/license/validate')) {
      licenseValidateCalls += 1;
      return Response.json({ valid: true, credits: 100 });
    }

    if (url.endsWith('/api/license/credits')) {
      creditCalls.push(body as CreditCall);
      if (creditsResponder) return creditsResponder();
      return Response.json({ credits_remaining: 42 });
    }

    if (url === ANTHROPIC_URL) {
      anthropicCalls.push(body as AnthropicCall);
      if (anthropicStatus !== 200) {
        return new Response('upstream boom', { status: anthropicStatus });
      }
      return new Response(encodeAnthropicStream(anthropicStreamEvents), { status: 200 });
    }

    throw new Error(`unexpected fetch to ${url}`);
  }) as unknown as typeof fetch;
}

function buildApp(): Hono {
  const app = new Hono();
  app.post('/assistant', assistantRoute);
  return app;
}

interface PostOptions {
  form: FormData;
  /** Explicit Content-Length; `undefined` omits the header entirely. */
  contentLength?: string;
  clientIP?: string;
}

function post({ form, contentLength = '4096', clientIP = CLIENT_IP }: PostOptions) {
  const headers: Record<string, string> = { 'Fly-Client-IP': clientIP };
  if (contentLength !== undefined) {
    headers['Content-Length'] = contentLength;
  }
  return buildApp().request('/assistant', { method: 'POST', body: form, headers });
}

function formWith(messages: unknown, extra: Record<string, string | Blob> = {}): FormData {
  const form = new FormData();
  form.append('messages', typeof messages === 'string' ? messages : JSON.stringify(messages));
  form.append('account_key', 'HW-TEST-0001-0002');
  for (const [key, value] of Object.entries(extra)) {
    form.append(key, value);
  }
  return form;
}

const TEXT_MESSAGES = [{ role: 'user', content: 'What is on my screen?' }];

function imageUrlMessage(count: number) {
  return [
    {
      role: 'user',
      content: [
        { type: 'text', text: 'What is on my screen?' },
        ...Array.from({ length: count }, () => ({
          type: 'image_url',
          image_url: { url: 'data:image/png;base64,aGVsbG8=' },
        })),
      ],
    },
  ];
}

/** Read the SSE body to completion, then let the fire-and-forget billing land. */
async function readAndSettle(res: Response): Promise<string> {
  const text = await res.text();
  await Promise.resolve();
  await drainPendingDeductions(2000);
  return text;
}

beforeEach(() => {
  cachedLicenseValue = { isValid: true, credits: 100, cachedAt: 'cached' };
  blockedIPs.clear();
  anthropicCalls = [];
  creditCalls = [];
  licenseValidateCalls = 0;
  anthropicStatus = 200;
  creditsResponder = null;
  anthropicStreamEvents = anthropicEvents(1500, 300, 'A settings window.');
  process.env.ANTHROPIC_API_KEY = 'test-key-not-a-real-credential';
  process.env.NEXTJS_LICENSE_API_URL = LICENSE_API_BASE;
  installFetch();
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  cachedLicenseValue = null;
  blockedIPs.clear();
  if (originalAnthropicKey === undefined) delete process.env.ANTHROPIC_API_KEY;
  else process.env.ANTHROPIC_API_KEY = originalAnthropicKey;
  if (originalLicenseApiUrl === undefined) delete process.env.NEXTJS_LICENSE_API_URL;
  else process.env.NEXTJS_LICENSE_API_URL = originalLicenseApiUrl;
});

describe('assistantRoute entitlement gate', () => {
  test('rejects a blocked IP with 403 before any license or Anthropic work', async () => {
    blockedIPs.add(CLIENT_IP);
    // A cache miss would force validateAuth to the license API, so zero
    // validate calls proves the IP gate short-circuits ahead of it.
    cachedLicenseValue = null;

    const res = await post({ form: formWith(TEXT_MESSAGES) });

    expect(res.status).toBe(403);
    expect(licenseValidateCalls).toBe(0);
    expect(anthropicCalls).toHaveLength(0);
  });

  test('rejects a request with no account key with 401 and never calls Anthropic', async () => {
    const form = new FormData();
    form.append('messages', JSON.stringify(TEXT_MESSAGES));

    const res = await post({ form });

    expect(res.status).toBe(401);
    expect(await res.json()).toMatchObject({ error: 'License required' });
    expect(anthropicCalls).toHaveLength(0);
  });

  test('rejects an invalid license with 401 and never calls Anthropic', async () => {
    cachedLicenseValue = { isValid: false, credits: 0, cachedAt: 'cached' };

    const res = await post({ form: formWith(TEXT_MESSAGES) });

    expect(res.status).toBe(401);
    expect(await res.json()).toMatchObject({ error: 'Invalid license' });
    expect(anthropicCalls).toHaveLength(0);
  });

  test('accepts the legacy license_key field name as an alias for account_key', async () => {
    const form = new FormData();
    form.append('messages', JSON.stringify(TEXT_MESSAGES));
    form.append('license_key', 'HW-LEGACY-0001');

    const res = await post({ form });
    await readAndSettle(res);

    expect(res.status).toBe(200);
    expect(anthropicCalls).toHaveLength(1);
    // The key reaches the billing write as the identifier, so the alias is a
    // rename at the edge only — not a second credential path.
    expect(creditCalls[0]?.license_key).toBe('HW-LEGACY-0001');
  });

  test('account_key wins over the legacy license_key when both are sent', async () => {
    const form = new FormData();
    form.append('messages', JSON.stringify(TEXT_MESSAGES));
    form.append('account_key', 'canonical-credential');
    form.append('license_key', 'legacy-credential');

    const res = await post({ form });
    await readAndSettle(res);

    expect(res.status).toBe(200);
    expect(anthropicCalls).toHaveLength(1);
    expect(creditCalls).toHaveLength(1);
    expect(creditCalls[0]?.license_key).toBe('canonical-credential');
  });
});

describe('assistantRoute credit pre-check', () => {
  test('rejects a balance below the flat one-image estimate with 402', async () => {
    cachedLicenseValue = { isValid: true, credits: 2, cachedAt: 'cached' };

    const res = await post({ form: formWith(TEXT_MESSAGES) });

    expect(res.status).toBe(402);
    expect(await res.json()).toMatchObject({ error: 'Insufficient credits', credits_remaining: 2 });
    expect(anthropicCalls).toHaveLength(0);
  });

  test('scales the estimate by image count, so a balance that passes one image fails two', async () => {
    // The estimate is 3 credits per forwarded image. A flat pre-check would let
    // both of these through and bill two images of vision spend to a 5-credit
    // balance; the scaled one must reject the second.
    cachedLicenseValue = { isValid: true, credits: 5, cachedAt: 'cached' };

    const oneImage = await post({ form: formWith(imageUrlMessage(1)) });
    await readAndSettle(oneImage);
    expect(oneImage.status).toBe(200);

    anthropicCalls = [];
    const twoImages = await post({ form: formWith(imageUrlMessage(2)) });

    expect(twoImages.status).toBe(402);
    expect(anthropicCalls).toHaveLength(0);
  });

  test('does not scale past the forwarded-image cap', async () => {
    // convertMessages drops images beyond ASSISTANT_MAX_IMAGES, so the estimate
    // must stop climbing there too — otherwise extra blocks lock out a balance
    // that can afford everything actually sent upstream.
    cachedLicenseValue = { isValid: true, credits: 3 * ASSISTANT_MAX_IMAGES, cachedAt: 'cached' };

    const res = await post({ form: formWith(imageUrlMessage(ASSISTANT_MAX_IMAGES + 4)) });
    await readAndSettle(res);

    expect(res.status).toBe(200);
  });
});

describe('assistantRoute request validation', () => {
  test('rejects an oversized Content-Length with 413 before parsing the body', async () => {
    const res = await post({
      form: formWith(TEXT_MESSAGES),
      contentLength: String(MAX_ASSISTANT_BODY_BYTES + 1),
    });

    expect(res.status).toBe(413);
    expect(anthropicCalls).toHaveLength(0);
  });

  test('rejects a missing messages field with 400', async () => {
    const form = new FormData();
    form.append('account_key', 'HW-TEST-0001-0002');

    const res = await post({ form });

    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: 'Missing field' });
  });

  test('rejects a messages field sent as a file part with 400, not 500', async () => {
    const form = new FormData();
    form.append('messages', new File([JSON.stringify(TEXT_MESSAGES)], 'messages.json', { type: 'application/json' }));
    form.append('account_key', 'HW-TEST-0001-0002');

    const res = await post({ form });

    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: 'Missing field' });
  });

  test('rejects messages that are valid JSON but not an array with 400', async () => {
    const res = await post({ form: formWith('{"role":"user"}') });

    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: 'Invalid messages' });
  });

  test('rejects a messages payload over the cap with 413, before auth', async () => {
    const filler = 'x'.repeat(ASSISTANT_MAX_MESSAGES_BYTES);
    const res = await post({ form: formWith([{ role: 'user', content: filler }]) });

    expect(res.status).toBe(413);
    expect(await res.json()).toMatchObject({ error: 'Messages too large' });
    expect(anthropicCalls).toHaveLength(0);
  });
});

describe('assistantRoute image handling', () => {
  const PNG_MAGIC = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

  test('rejects a multipart image over the cap with 413 and never calls Anthropic', async () => {
    const oversized = new File(
      [new Uint8Array(MAX_ASSISTANT_IMAGE_BYTES + 1)],
      'shot.png',
      { type: 'image/png' }
    );
    const res = await post({ form: formWith(imageUrlMessage(1), { image: oversized }) });

    expect(res.status).toBe(413);
    expect(await res.json()).toMatchObject({ error: 'Image too large' });
    expect(anthropicCalls).toHaveLength(0);
  });

  test('rejects an unsupported image format with 400', async () => {
    // BMP: neither the magic bytes nor the declared type are supported.
    const bmp = new File([new Uint8Array([0x42, 0x4d, 0x00, 0x00])], 'shot.bmp', { type: 'image/bmp' });

    const res = await post({ form: formWith(imageUrlMessage(1), { image: bmp }) });

    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: 'Unsupported image type' });
    expect(anthropicCalls).toHaveLength(0);
  });

  test('forwards the sniffed media type, not the mislabelled one the client declared', async () => {
    const bytes = new Uint8Array([...PNG_MAGIC, 0x01, 0x02]);
    const mislabelled = new File([bytes], 'shot.jpg', { type: 'image/jpeg' });

    const res = await post({ form: formWith(imageUrlMessage(1), { image: mislabelled }) });
    await readAndSettle(res);

    expect(res.status).toBe(200);
    const blocks = anthropicCalls[0]?.messages[0]?.content as Array<Record<string, any>>;
    const imageBlock = blocks.find((block) => block.type === 'image');
    expect(imageBlock?.source?.media_type).toBe('image/png');
    expect(imageBlock?.source?.data).toBe(Buffer.from(bytes).toString('base64'));
  });
});

describe('assistantRoute streaming response', () => {
  test('streams Anthropic deltas back as OpenAI-compatible SSE', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES) });
    const body = await readAndSettle(res);

    expect(res.status).toBe(200);
    expect(res.headers.get('Content-Type')).toBe('text/event-stream');
    expect(res.headers.get('X-Accel-Buffering')).toBe('no');
    expect(res.headers.get('X-Request-ID')).toMatch(/^[0-9a-f-]{36}$/);
    expect(body).toContain('"content":"A settings window."');
    expect(body.trimEnd().endsWith('data: [DONE]')).toBe(true);
  });

  test('an Anthropic 5xx still returns 200 SSE carrying an error chunk, and bills nothing', async () => {
    anthropicStatus = 502;

    const res = await post({ form: formWith(TEXT_MESSAGES) });
    const body = await readAndSettle(res);

    expect(res.status).toBe(200);
    expect(body).toContain('"finish_reason":"error"');
    expect(creditCalls).toHaveLength(0);
  });

  test('uses the prompt override in place of the system message', async () => {
    const form = formWith(
      [{ role: 'system', content: 'System from the conversation' }, ...TEXT_MESSAGES],
      { prompt: 'Override system prompt' }
    );

    const res = await post({ form });
    await readAndSettle(res);

    expect(anthropicCalls[0]?.system).toBe('Override system prompt');
  });

  test('falls back to the default system prompt when neither is supplied', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES) });
    await readAndSettle(res);

    expect(anthropicCalls[0]?.system).toBe('You are a helpful screen-aware assistant.');
  });
});

describe('assistantRoute billing', () => {
  test('bills the credits for the tokens the stream actually reported', async () => {
    anthropicStreamEvents = anthropicEvents(1500, 300, 'ok');

    const res = await post({ form: formWith(TEXT_MESSAGES) });
    await readAndSettle(res);

    const expectedCredits = creditsForCost(computeAnthropicCost(1500, 300));
    expect(expectedCredits).toBeGreaterThan(0);
    expect(creditCalls).toHaveLength(1);
    expect(creditCalls[0]?.amount).toBe(expectedCredits);
    expect(creditCalls[0]?.license_key).toBe('HW-TEST-0001-0002');
  });

  test('a longer response bills more than a shorter one', async () => {
    anthropicStreamEvents = anthropicEvents(1500, 300, 'short');
    await readAndSettle(await post({ form: formWith(TEXT_MESSAGES) }));
    const shortCharge = creditCalls[0]?.amount ?? 0;

    creditCalls = [];
    anthropicStreamEvents = anthropicEvents(1500, 30_000, 'long');
    await readAndSettle(await post({ form: formWith(TEXT_MESSAGES) }));
    const longCharge = creditCalls[0]?.amount ?? 0;

    expect(longCharge).toBeGreaterThan(shortCharge);
  });

  test('records the endpoint, provider and image flag on the usage metadata', async () => {
    const png = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])], 'shot.png', {
      type: 'image/png',
    });

    const res = await post({ form: formWith(imageUrlMessage(1), { image: png }) });
    await readAndSettle(res);

    expect(creditCalls[0]?.metadata).toMatchObject({
      endpoint: '/assistant',
      llm_provider: 'anthropic',
      has_image: true,
      message_count: 1,
    });
  });

  test('a text-only request records has_image false', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES) });
    await readAndSettle(res);

    expect(creditCalls[0]?.metadata).toMatchObject({ has_image: false });
  });
});

// Issue #900: every rejection writes one structured line, and the line never
// carries the client IP or the account key.
describe('assistantRoute rejection and billing logs', () => {
  const ACCOUNT_KEY = 'HW-TEST-0001-0002';
  let logSpy: ReturnType<typeof spyOn<Console, 'log'>>;

  beforeEach(() => {
    logSpy = spyOn(console, 'log').mockImplementation(() => {});
  });

  afterEach(() => {
    logSpy.mockRestore();
  });

  function loggedLines(): string[] {
    return logSpy.mock.calls.map((call: unknown[]) => String(call[0]));
  }

  function eventsNamed(event: string): Array<Record<string, unknown>> {
    return loggedLines()
      .flatMap((line) => {
        try {
          return [JSON.parse(line) as Record<string, unknown>];
        } catch {
          return [];
        }
      })
      .filter((entry) => entry.event === event);
  }

  /** Assert exactly one rejection line with this reason, and no IP or key in any line. */
  function expectOneRejection(reason: string): Record<string, unknown> {
    const rejected = eventsNamed('assistant.request_rejected');
    expect(rejected).toHaveLength(1);
    expect(rejected[0]).toMatchObject({ event: 'assistant.request_rejected', reason });
    expect(typeof rejected[0]?.requestId).toBe('string');
    for (const line of loggedLines()) {
      expect(line).not.toContain(CLIENT_IP);
      expect(line).not.toContain(ACCOUNT_KEY);
    }
    return rejected[0] as Record<string, unknown>;
  }

  test('a body that is not multipart logs not_multipart with its content type, and no key or IP', async () => {
    const body = JSON.stringify({ account_key: ACCOUNT_KEY, messages: TEXT_MESSAGES });
    const res = await buildApp().request('/assistant', {
      method: 'POST',
      body,
      headers: {
        'Fly-Client-IP': CLIENT_IP,
        'Content-Type': 'application/json',
        'Content-Length': String(Buffer.byteLength(body)),
      },
    });

    expect(res.status).toBe(400);
    expect(await res.json()).toMatchObject({ error: 'Invalid request' });
    expect(loggedLines()).toHaveLength(1);
    const entry = expectOneRejection('not_multipart');
    expect(entry.contentType).toBe('application/json');
  });

  test('a blocked IP logs ip_blocked without the IP', async () => {
    blockedIPs.add(CLIENT_IP);

    const res = await post({ form: formWith(TEXT_MESSAGES) });

    expect(res.status).toBe(403);
    expectOneRejection('ip_blocked');
  });

  test('an oversized Content-Length logs payload_too_large with the measured size', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES), contentLength: String(MAX_ASSISTANT_BODY_BYTES + 1) });

    expect(res.status).toBe(413);
    const entry = expectOneRejection('payload_too_large');
    expect(entry).toMatchObject({ status: 413, contentLength: MAX_ASSISTANT_BODY_BYTES + 1, maxBytes: MAX_ASSISTANT_BODY_BYTES });
  });

  test('an unparseable Content-Length logs invalid_content_length, not payload_too_large', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES), contentLength: 'not-a-number' });

    expect(res.status).toBe(400);
    const entry = expectOneRejection('invalid_content_length');
    expect(entry).toMatchObject({ status: 400, contentLength: null });
  });

  test('a missing messages field logs missing_messages', async () => {
    const form = new FormData();
    form.append('account_key', ACCOUNT_KEY);

    const res = await post({ form });

    expect(res.status).toBe(400);
    expect(expectOneRejection('missing_messages')).toMatchObject({ messagesField: 'absent' });
  });

  test('a messages field sent as a file part logs missing_messages with the field kind', async () => {
    const form = new FormData();
    form.append('messages', new File([JSON.stringify(TEXT_MESSAGES)], 'messages.json', { type: 'application/json' }));
    form.append('account_key', ACCOUNT_KEY);

    const res = await post({ form });

    expect(res.status).toBe(400);
    expect(expectOneRejection('missing_messages')).toMatchObject({ messagesField: 'file' });
  });

  test('an over-cap messages payload logs messages_too_large with its size, not its content', async () => {
    const filler = 'x'.repeat(ASSISTANT_MAX_MESSAGES_BYTES);
    const messagesRaw = JSON.stringify([{ role: 'user', content: filler }]);

    const res = await post({ form: formWith(messagesRaw) });

    expect(res.status).toBe(413);
    const entry = expectOneRejection('messages_too_large');
    expect(entry).toMatchObject({
      messagesBytes: Buffer.byteLength(messagesRaw, 'utf8'),
      maxBytes: ASSISTANT_MAX_MESSAGES_BYTES,
    });
    for (const line of loggedLines()) {
      expect(line).not.toContain('xxxxxxxxxx');
    }
  });

  test('messages that are not a JSON array log messages_not_array without the messages', async () => {
    const res = await post({ form: formWith('{"role":"user","content":"secret dictation"}') });

    expect(res.status).toBe(400);
    expectOneRejection('messages_not_array');
    for (const line of loggedLines()) {
      expect(line).not.toContain('secret dictation');
    }
  });

  test('a failed credit pre-check logs insufficient_credits', async () => {
    cachedLicenseValue = { isValid: true, credits: 2, cachedAt: 'cached' };

    const res = await post({ form: formWith(TEXT_MESSAGES) });

    expect(res.status).toBe(402);
    expect(expectOneRejection('insufficient_credits')).toMatchObject({ status: 402, imageCount: 1 });
  });

  test('an over-cap image logs image_too_large with its size', async () => {
    const oversized = new File([new Uint8Array(MAX_ASSISTANT_IMAGE_BYTES + 1)], 'shot.png', { type: 'image/png' });

    const res = await post({ form: formWith(imageUrlMessage(1), { image: oversized }) });

    expect(res.status).toBe(413);
    expect(expectOneRejection('image_too_large')).toMatchObject({
      imageBytes: MAX_ASSISTANT_IMAGE_BYTES + 1,
      maxBytes: MAX_ASSISTANT_IMAGE_BYTES,
    });
  });

  test('an unsupported image logs unsupported_image_type with its declared type and size', async () => {
    const bmp = new File([new Uint8Array([0x42, 0x4d, 0x00, 0x00])], 'shot.bmp', { type: 'image/bmp' });

    const res = await post({ form: formWith(imageUrlMessage(1), { image: bmp }) });

    expect(res.status).toBe(400);
    const entry = expectOneRejection('unsupported_image_type');
    // bun normalises the File type (image/bmp reads back as image/x-ms-bmp).
    expect(String(entry.declaredType)).toContain('bmp');
    expect(entry.imageBytes).toBe(4);
  });

  test('a successful request logs no rejection', async () => {
    const res = await post({ form: formWith(TEXT_MESSAGES) });
    await readAndSettle(res);

    expect(res.status).toBe(200);
    expect(eventsNamed('assistant.request_rejected')).toHaveLength(0);
    expect(eventsNamed('assistant.credit_deduction_failed')).toHaveLength(0);
  });

  /**
   * Post a text request, read the stream, and let the fire-and-forget billing
   * settle. Returns the response, its body and the one failure line it wrote.
   */
  async function postAndExpectOneDeductionFailure() {
    anthropicStreamEvents = anthropicEvents(1500, 300, 'ok');

    const res = await post({ form: formWith(TEXT_MESSAGES) });
    const body = await readAndSettle(res);

    // The real deductCredits ran and sent the billing write.
    expect(res.status).toBe(200);
    expect(body).toContain('"content":"ok"');
    expect(creditCalls).toHaveLength(1);
    expect(creditCalls[0]?.license_key).toBe(ACCOUNT_KEY);

    const failed = eventsNamed('assistant.credit_deduction_failed');
    expect(failed).toHaveLength(1);
    expect(failed[0]).toMatchObject({
      endpoint: '/assistant',
      costUsd: computeAnthropicCost(1500, 300),
      messageCount: 1,
      requestId: res.headers.get('X-Request-ID'),
    });
    const failedLines = loggedLines().filter((line) => line.includes('assistant.credit_deduction_failed'));
    expect(failedLines).toHaveLength(1);
    expect(failedLines[0]).not.toContain(CLIENT_IP);
    expect(failedLines[0]).not.toContain(ACCOUNT_KEY);
    expect(failedLines[0]).not.toContain('What is on my screen?');
    return failed[0] as Record<string, unknown>;
  }

  test('a license API that refuses the deduction logs credit_deduction_failed with the status, and the stream is unaffected', async () => {
    const warnSpy = spyOn(console, 'warn').mockImplementation(() => {});
    try {
      creditsResponder = () => Response.json({ error: 'service unavailable' }, { status: 503 });

      const entry = await postAndExpectOneDeductionFailure();

      expect(entry).toMatchObject({ failure: 'http', status: 503 });
    } finally {
      warnSpy.mockRestore();
    }
  });

  test('a license API that cannot be reached logs credit_deduction_failed with the error', async () => {
    const warnSpy = spyOn(console, 'warn').mockImplementation(() => {});
    try {
      creditsResponder = () => {
        throw new TypeError('fetch failed: connection reset');
      };

      const entry = await postAndExpectOneDeductionFailure();

      expect(entry).toMatchObject({ failure: 'network', error: 'fetch failed: connection reset' });
    } finally {
      warnSpy.mockRestore();
    }
  });

  test('a deduction that throws still logs one line, with an object cause reduced to its code', async () => {
    // deductCredits does not throw today; this pins the catch that guards a
    // future change, and that an object cause never logs as [object Object].
    const deductSpy = spyOn(creditsModule, 'deductCredits').mockImplementation(async () => {
      throw new Error('deduction exploded', { cause: { code: 'ECONNRESET', licenseKey: ACCOUNT_KEY } });
    });
    try {
      const res = await post({ form: formWith(TEXT_MESSAGES) });
      await readAndSettle(res);
      await new Promise((resolve) => setTimeout(resolve, 0));

      const failed = eventsNamed('assistant.credit_deduction_failed');
      expect(failed).toHaveLength(1);
      expect(failed[0]).toMatchObject({
        failure: 'exception',
        error: 'deduction exploded',
        cause: 'ECONNRESET',
        requestId: res.headers.get('X-Request-ID'),
      });
      const failedLine = loggedLines().find((line) => line.includes('assistant.credit_deduction_failed')) ?? '';
      expect(failedLine).not.toContain('[object Object]');
      expect(failedLine).not.toContain(ACCOUNT_KEY);
    } finally {
      deductSpy.mockRestore();
    }
  });
});
