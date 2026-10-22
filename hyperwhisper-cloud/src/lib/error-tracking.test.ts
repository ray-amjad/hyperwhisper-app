// SERVER ERROR TRACKING (#2013)
//
// One forced error in a real route, through the real `app.onError` in
// src/index.ts, must reach Sentry as exactly ONE event, and that event must
// carry no Account Key, email address, transcript or request data. Sentry runs
// for real here; only its transport is swapped for one that records envelopes.
//
// The lever is the one src/index.test.ts pulls: `/usage` calls `isIPBlocked`
// first and does not catch it, so a throw there is an unhandled route error.

import { afterAll, beforeEach, describe, expect, mock, test } from 'bun:test';
import * as Sentry from '@sentry/bun';
import * as realRedis from './redis';

let ipBlockedError: Error | null = null;

mock.module('./redis', () => ({
  ...realRedis,
  isIPBlocked: async () => {
    if (ipBlockedError) throw ipBlockedError;
    return false;
  },
}));

const {
  ALLOWED_TAGS,
  captureServerError,
  initErrorTracking,
  scrubEvent,
  scrubMessage,
} = await import('./error-tracking');

// Account-Key-shaped, so the format redaction must catch it.
const LEAKY = ['HW', 'TEST', 'LEAK', 'TEST', 'LEAK'].join('-');
const EMAIL = 'someone.private@example.com';
const TRANSCRIPT = 'my secret dictated sentence about the merger';

type Envelope = { header: Record<string, unknown>; items: { header: Record<string, unknown>; payload: unknown }[] };
const envelopes: Envelope[] = [];

function parseEnvelope(body: string | Uint8Array): Envelope {
  const text = typeof body === 'string' ? body : new TextDecoder().decode(body);
  const lines = text.split('\n').filter((line) => line.length > 0);
  const header = JSON.parse(lines[0]!);
  const items: Envelope['items'] = [];
  for (let i = 1; i + 1 < lines.length; i += 2) {
    items.push({ header: JSON.parse(lines[i]!), payload: JSON.parse(lines[i + 1]!) });
  }
  return { header, items };
}

function capturedEvents(): Record<string, unknown>[] {
  return envelopes
    .flatMap((envelope) => envelope.items)
    .filter((item) => item.header.type === 'event')
    .map((item) => item.payload as Record<string, unknown>);
}

// With no DSN: measured before anything starts Sentry in this process.
const savedDsn = process.env.SENTRY_DSN;
delete process.env.SENTRY_DSN;
const startedWithoutDsn = initErrorTracking();
const initializedWithoutDsn = Sentry.isInitialized();
captureServerError(new Error('boom'), { errorId: 'x' });

const listenersBefore = {
  uncaughtException: process.listeners('uncaughtException'),
  unhandledRejection: process.listeners('unhandledRejection'),
};
process.env.SENTRY_DSN = 'https://publickey@o0.ingest.sentry.io/0';
const started = initErrorTracking({
  transport: (options) =>
    Sentry.createTransport(options, async (request) => {
      envelopes.push(parseEnvelope(request.body));
      return { statusCode: 200 };
    }),
});

const { default: server } = await import('../index');

describe('with no SENTRY_DSN', () => {
  test('init is a no-op and a capture sends nothing', () => {
    expect(startedWithoutDsn).toBe(false);
    expect(initializedWithoutDsn).toBe(false);
  });
});

describe('with SENTRY_DSN set', () => {
  beforeEach(() => {
    envelopes.length = 0;
    ipBlockedError = null;
  });

  afterAll(async () => {
    await Sentry.close(2_000);
    // Leave the process as the other suites expect it: no Sentry handlers.
    for (const name of ['uncaughtException', 'unhandledRejection'] as const) {
      for (const listener of process.listeners(name)) {
        if (!listenersBefore[name].includes(listener)) process.removeListener(name, listener);
      }
    }
    if (savedDsn === undefined) delete process.env.SENTRY_DSN;
    else process.env.SENTRY_DSN = savedDsn;
  });

  test('a forced route error sends exactly one scrubbed event', async () => {
    expect(started).toBe(true);
    ipBlockedError = new Error(
      `upstream failed for ${LEAKY} (${EMAIL}): {"text":"${TRANSCRIPT}"}`,
    );

    const res = await server.fetch(
      new Request(`http://localhost/usage?account_key=${LEAKY}&email=${encodeURIComponent(EMAIL)}`, {
        headers: {
          Authorization: `Bearer ${LEAKY}`,
          'X-Forwarded-For': '203.0.113.7',
          'Fly-Client-IP': '203.0.113.7',
        },
      }),
    );
    expect(res.status).toBe(500);
    const body = (await res.json()) as { error_id: string };
    await Sentry.flush(2_000);

    const events = capturedEvents();
    expect(events).toHaveLength(1);
    const event = events[0]!;
    const serialized = JSON.stringify(envelopes);

    // Nothing private, anywhere in what left.
    expect(serialized).not.toContain(LEAKY);
    expect(serialized.toLowerCase()).not.toContain(LEAKY.toLowerCase());
    expect(serialized).not.toContain(EMAIL);
    expect(serialized).not.toContain(encodeURIComponent(EMAIL));
    expect(serialized).not.toContain(TRANSCRIPT);
    expect(serialized).not.toContain('203.0.113.7');

    // Only the allow-listed fields.
    expect(event.request).toBeUndefined();
    expect(event.user).toBeUndefined();
    expect(event.breadcrumbs).toBeUndefined();
    expect(event.extra).toBeUndefined();
    expect(event.server_name).toBeUndefined();
    const tags = event.tags as Record<string, string>;
    expect(Object.keys(tags).every((name) => (ALLOWED_TAGS as readonly string[]).includes(name))).toBe(true);
    expect(tags.errorId).toBe(body.error_id);
    expect(tags.route).toBe('/usage');
    expect(tags.method).toBe('GET');

    // Still useful: the type, a redacted message and the stack.
    const exception = (event.exception as { values: { type: string; value: string; stacktrace?: { frames: unknown[] } }[] }).values[0]!;
    expect(exception.type).toBe('Error');
    expect(exception.value).toContain('upstream failed for <redacted-license-key>');
    expect(exception.value).toContain('<redacted-email>');
    expect(exception.stacktrace?.frames.length).toBeGreaterThan(0);
  });

  test('the same route error twice sends two events, one per request', async () => {
    const fail = () =>
      server.fetch(new Request('http://localhost/usage', { headers: { 'X-Forwarded-For': '203.0.113.7' } }));
    // Built side by side, so both carry the same message AND the same stack.
    for (const error of [1, 2].map(() => new Error('upstream failed'))) {
      ipBlockedError = error;
      expect((await fail()).status).toBe(500);
    }
    await Sentry.flush(2_000);
    expect(capturedEvents()).toHaveLength(2);
  });

  test('a request that succeeds sends nothing', async () => {
    const res = await server.fetch(new Request('http://localhost/health'));
    expect(res.status).toBe(200);
    await Sentry.flush(2_000);
    expect(capturedEvents()).toHaveLength(0);
  });
});

describe('scrubMessage', () => {
  test('cuts keys, emails and bearer tokens by format', () => {
    const out = scrubMessage(`key hw-ab12-cd34-ef56-gh78 for ${EMAIL} with Bearer abc.def-123`);
    expect(out).toBe('key <redacted-license-key> for <redacted-email> with Bearer <redacted>');
  });

  test('cuts a double-quoted value, such as a transcript a provider echoed', () => {
    const out = scrubMessage(`provider said "${TRANSCRIPT}"`);
    expect(out).toBe('provider said "<redacted>"');
  });

  test('cuts a serialized provider body that could quote a transcript', () => {
    expect(scrubMessage(`Deepgram 500: {"results":"${TRANSCRIPT}"}`)).not.toContain(TRANSCRIPT);
  });
});

describe('scrubEvent', () => {
  test('keeps only the allow-list', () => {
    const out = scrubEvent({
      type: undefined,
      event_id: 'e1',
      server_name: 'fly-machine-host',
      user: { ip_address: '203.0.113.7', email: EMAIL },
      request: { url: `http://x/usage?account_key=${LEAKY}`, headers: { authorization: 'x' } },
      breadcrumbs: [{ message: TRANSCRIPT }],
      extra: { body: TRANSCRIPT },
      contexts: { runtime: { name: 'bun' }, os: { name: 'linux' }, app: { app_memory: 1 } },
      tags: { region: 'nrt', licenseKey: LEAKY },
      exception: {
        values: [{
          type: 'Error',
          value: `bad key ${LEAKY}`,
          stacktrace: { frames: [{ filename: '/app/src/x.ts', function: 'f', lineno: 1, vars: { transcript: TRANSCRIPT }, context_line: TRANSCRIPT }] },
        }],
      },
    });
    const serialized = JSON.stringify(out);
    expect(serialized).not.toContain(LEAKY);
    expect(serialized).not.toContain(EMAIL);
    expect(serialized).not.toContain(TRANSCRIPT);
    expect(serialized).not.toContain('fly-machine-host');
    expect(out.tags).toEqual({ region: 'nrt' });
    expect(out.contexts).toEqual({ runtime: { name: 'bun' } });
    expect(out.exception?.values?.[0]?.stacktrace?.frames?.[0]).toEqual({
      filename: '/app/src/x.ts', module: undefined, function: 'f', lineno: 1, colno: undefined, in_app: undefined,
    });
  });
});
