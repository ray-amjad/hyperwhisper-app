import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import type { Context } from 'hono';
import type { WSMessageReceive } from 'hono/ws';
import { computeDeepgramTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
import { drainPendingDeductions } from '../middleware/credits';
import type { AuthContext } from '../middleware/auth';
import { captureRejectionLogs, captureStreamingLogs } from './ws-streaming-test-logs';

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };

// The streaming entry point reaches redis twice — the IP block list in the
// preflight and the license cache behind validateAuth. Mocking only redis keeps
// the real auth + credit middleware in the test, so what is asserted below is
// the actual entitlement chain, not a stub of it.
let cachedLicenseValue: CachedLicense | null = null;
const blockedIPs = new Set<string>();

mock.module('../lib/redis', () => ({
  // Satisfies the static `import { redis }` reached from other modules in this
  // suite — a redis mock that omits it makes those files fail to load.
  redis: {},
  isIPBlocked: async (ip: string) => blockedIPs.has(ip),
  getCachedLicense: async (_licenseKey: string) => cachedLicenseValue,
  cacheLicense: async () => {},
}));

const {
  createStreamingEvents,
  durationSecondsForLinear16AudioBytes,
  minimumStreamingCredits,
  wsStreamingPreflight,
} = await import('./ws-streaming-deepgram');
const { endActiveStreamingSessions, resetStreamingShutdownForTests } = await import('./ws-streaming-shared');

const originalFetch = globalThis.fetch;
const originalWebSocket = globalThis.WebSocket;
const originalApiKey = process.env.DEEPGRAM_API_KEY;

const BYTES_PER_SECOND = 16000 * 1 * 2;

function neverFetch() {
  return mock(async () => {
    throw new Error('no license API call was expected here');
  }) as unknown as typeof fetch;
}

describe('durationSecondsForLinear16AudioBytes', () => {
  test('calculates duration from the mono 16 kHz linear16 audio forwarded to Deepgram', () => {
    const bytesPerSecond = 16000 * 1 * 2;

    expect(durationSecondsForLinear16AudioBytes(bytesPerSecond * 5)).toBe(5);
  });

  test('does not depend on overlapping interim or final transcript result durations', () => {
    const overlappingDeepgramResultDurations = [3, 3, 3, 2, 2];
    const bytesActuallyForwarded = 5 * 16000 * 1 * 2;

    expect(overlappingDeepgramResultDurations.reduce((sum, duration) => sum + duration, 0)).toBe(13);
    expect(durationSecondsForLinear16AudioBytes(bytesActuallyForwarded)).toBe(5);
  });
});

describe('minimumStreamingCredits', () => {
  test('requires the credits for 30 seconds of Nova-3 audio before a session opens', () => {
    expect(minimumStreamingCredits()).toBe(2.8);
    expect(minimumStreamingCredits()).toBe(creditsForCost(computeDeepgramTranscriptionCost(30)));
  });

  test('is a partial-minute floor, not a whole-minute one', () => {
    expect(minimumStreamingCredits()).toBeLessThan(creditsForCost(computeDeepgramTranscriptionCost(60)));
  });
});

describe('wsStreamingPreflight', () => {
  function buildApp(): Hono {
    const app = new Hono();
    app.get('/ws/streaming-deepgram', wsStreamingPreflight, (c) =>
      c.json({
        credits: c.get('wsAuth').credits,
        licenseKey: c.get('wsAuth').licenseKey,
        clientIP: c.get('wsClientIP'),
      })
    );
    return app;
  }

  const UPGRADE_HEADERS = { Upgrade: 'websocket', 'Fly-Client-IP': '203.0.113.7' } as const;


  // The preflight's console.log lines, captured per test. Each refused upgrade
  // must leave exactly one `ws_streaming.request_rejected` line, and no line may
  // carry the key.
  const { lines: loggedLines, expectOneRejection } = captureRejectionLogs();

  beforeEach(() => {
    cachedLicenseValue = { isValid: true, credits: 100, cachedAt: 'cached' };
    blockedIPs.clear();
    globalThis.fetch = neverFetch();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    cachedLicenseValue = null;
    blockedIPs.clear();
  });

  test('rejects a plain HTTP request that is not a WebSocket upgrade', async () => {
    const res = await buildApp().request('/ws/streaming-deepgram?account_key=key-1234-abcd', {
      headers: { 'Fly-Client-IP': '203.0.113.7' },
    });

    expect(res.status).toBe(426);
    expect(await res.text()).toBe('Expected WebSocket upgrade');
    expect(expectOneRejection('not_websocket', 426).endpoint).toBe('/ws/streaming-deepgram');
  });

  test('rejects a blocked IP before any license work happens', async () => {
    blockedIPs.add('203.0.113.7');
    // A cache miss forces validateAuth to the license API, so `neverFetch`
    // proves the IP gate short-circuits ahead of it.
    cachedLicenseValue = null;

    const res = await buildApp().request('/ws/streaming-deepgram?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(403);
    expect(await res.text()).toBe('Access denied');
    const entry = expectOneRejection('ip_blocked', 403);
    expect(JSON.stringify(entry)).not.toContain('203.0.113.7');
  });

  test('rejects an upgrade that carries no key at all', async () => {
    const res = await buildApp().request('/ws/streaming-deepgram', { headers: UPGRADE_HEADERS });

    expect(res.status).toBe(401);
    expect(await res.text()).toBe('Missing account_key');
    expectOneRejection('missing_account_key', 401);
  });

  test('accepts the legacy license_key alias that installed apps still send', async () => {
    const res = await buildApp().request('/ws/streaming-deepgram?license_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({
      credits: 100,
      licenseKey: 'key-1234-abcd',
      clientIP: '203.0.113.7',
    });
  });

  test('account_key wins over the legacy license_key when both are sent', async () => {
    const res = await buildApp().request(
      '/ws/streaming-deepgram?account_key=canonical-credential&license_key=legacy-credential',
      { headers: UPGRADE_HEADERS }
    );

    expect(res.status).toBe(200);
    expect((await res.json() as { licenseKey: string }).licenseKey).toBe('canonical-credential');
  });

  test('rejects a revoked license without reaching the socket handler', async () => {
    cachedLicenseValue = { isValid: false, credits: 500, cachedAt: 'cached' };

    const res = await buildApp().request('/ws/streaming-deepgram?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(401);
    expect(await res.text()).toBe('Unauthorized');
  });

  test('refuses a balance below the 30 second floor and reports what is required', async () => {
    cachedLicenseValue = { isValid: true, credits: minimumStreamingCredits() - 0.1, cachedAt: 'cached' };

    const res = await buildApp().request('/ws/streaming-deepgram?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(402);
    const body = await res.json() as { error: string; credits_remaining: number; minutes_required: number };
    expect(body.error).toBe('Insufficient credits');
    expect(body.credits_remaining).toBe(2.7);
    // ceil(2.8-credit floor / CREDITS_PER_MINUTE 1.67) = ceil(1.68) = 2 (#1657).
    expect(body.minutes_required).toBe(2);
    const entry = expectOneRejection('insufficient_credits', 402);
    // The rounded balance the credit check compared, which the 402 body reports too.
    expect(entry.credits).toBe(2.7);
    expect(entry.minimumCredits).toBe(minimumStreamingCredits());
  });

  test('admits a balance exactly at the floor', async () => {
    cachedLicenseValue = { isValid: true, credits: minimumStreamingCredits(), cachedAt: 'cached' };

    const res = await buildApp().request('/ws/streaming-deepgram?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(200);
    expect((await res.json() as { credits: number }).credits).toBe(2.8);
    expect(loggedLines().some((line) => line.includes('ws_streaming.request_rejected'))).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Socket lifecycle
// ---------------------------------------------------------------------------

interface CloseCall {
  code?: number;
  reason?: string;
}

const upstreamSockets: FakeUpstreamSocket[] = [];

class FakeUpstreamSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  readyState = 0;
  bufferedAmount = 0;
  readonly sent: Array<string | ArrayBuffer> = [];
  readonly closes: CloseCall[] = [];
  private readonly listeners = new Map<string, Array<(evt: unknown) => void>>();

  constructor(readonly url: string, readonly protocols?: string | string[]) {
    upstreamSockets.push(this);
  }

  addEventListener(type: string, handler: (evt: unknown) => void): void {
    const existing = this.listeners.get(type) ?? [];
    existing.push(handler);
    this.listeners.set(type, existing);
  }

  send(data: string | ArrayBuffer): void {
    this.sent.push(data);
  }

  close(code?: number, reason?: string): void {
    this.readyState = FakeUpstreamSocket.CLOSED;
    this.closes.push({ code, reason });
  }

  emit(type: string, evt: unknown): void {
    for (const handler of this.listeners.get(type) ?? []) handler(evt);
  }

  /** Simulate Deepgram accepting the handshake. */
  handshake(): void {
    this.readyState = FakeUpstreamSocket.OPEN;
    this.emit('open', {});
  }

  /** Simulate one Deepgram Live JSON frame arriving. */
  deliver(payload: unknown): void {
    this.emit('message', { data: typeof payload === 'string' ? payload : JSON.stringify(payload) });
  }

  get audioFramesForwarded(): number {
    return this.sent.filter((frame) => frame instanceof ArrayBuffer).length;
  }
}

class FakeClientSocket {
  readyState = 1;
  readonly sent: string[] = [];
  readonly closes: CloseCall[] = [];

  send(data: string | ArrayBuffer | Uint8Array): void {
    this.sent.push(typeof data === 'string' ? data : '<binary>');
  }

  close(code?: number, reason?: string): void {
    this.readyState = 3;
    this.closes.push({ code, reason });
  }

  messages(): Array<Record<string, unknown>> {
    return this.sent.map((raw) => JSON.parse(raw) as Record<string, unknown>);
  }

  messagesOfType(type: string): Array<Record<string, unknown>> {
    return this.messages().filter((message) => message.type === type);
  }
}

function fakeContext(auth: AuthContext, url: string): Context {
  const vars: Record<string, unknown> = { wsAuth: auth, wsClientIP: '203.0.113.7' };
  return {
    get: (key: string) => vars[key],
    req: { url },
  } as unknown as Context;
}

function audioFrame(seconds: number): ArrayBuffer {
  return new ArrayBuffer(Math.round(seconds * BYTES_PER_SECOND));
}

function binaryMessage(data: ArrayBuffer): MessageEvent<WSMessageReceive> {
  return { data } as MessageEvent<WSMessageReceive>;
}

function textMessage(data: string): MessageEvent<WSMessageReceive> {
  return { data } as MessageEvent<WSMessageReceive>;
}

interface Harness {
  events: ReturnType<typeof createStreamingEvents>;
  client: FakeClientSocket;
  upstream: FakeUpstreamSocket;
  /** Client sees `session_complete`, then the license API is charged. */
  endSession(): Promise<number>;
}

/** Open a session that has already completed the Deepgram handshake. */
function openSession(options: { credits?: number; query?: string } = {}): Harness {
  const auth: AuthContext = {
    identifier: 'key-1234-abcd',
    licenseKey: 'key-1234-abcd',
    credits: options.credits ?? 1000,
  };
  const url = `https://transcribe.example/ws/streaming-deepgram?account_key=key-1234-abcd${options.query ?? ''}`;
  const events = createStreamingEvents(fakeContext(auth, url));
  const client = new FakeClientSocket();

  events.onOpen(new Event('open'), client);
  const upstream = upstreamSockets[upstreamSockets.length - 1];
  if (!upstream) throw new Error('no upstream socket was constructed');
  upstream.handshake();

  return {
    events,
    client,
    upstream,
    async endSession() {
      await events.onClose();
      return drainPendingDeductions(2000);
    },
  };
}

interface LicenseCharge {
  license_key: string;
  amount: number;
  metadata: Record<string, unknown>;
}

const licenseCharges: LicenseCharge[] = [];

describe('streaming socket lifecycle', () => {
  beforeEach(() => {
    licenseCharges.length = 0;
    upstreamSockets.length = 0;
    process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
    globalThis.WebSocket = FakeUpstreamSocket as unknown as typeof WebSocket;
    globalThis.fetch = mock(async (input: string | URL | Request, init?: RequestInit) => {
      const target = typeof input === 'string' ? input : input.toString();
      if (target.includes('/api/license/credits')) {
        licenseCharges.push(JSON.parse(String(init?.body)) as LicenseCharge);
        return new Response(JSON.stringify({ credits_remaining: 10 }), { status: 200 });
      }
      throw new Error(`unexpected fetch to ${target}`);
    }) as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    globalThis.WebSocket = originalWebSocket;
    if (originalApiKey === undefined) delete process.env.DEEPGRAM_API_KEY;
    else process.env.DEEPGRAM_API_KEY = originalApiKey;
  });

  test('closes the session when the Deepgram key is not configured, without dialling upstream', () => {
    delete process.env.DEEPGRAM_API_KEY;
    const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
    const events = createStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
    const client = new FakeClientSocket();

    events.onOpen(new Event('open'), client);

    expect(upstreamSockets).toHaveLength(0);
    expect(client.messages()).toEqual([{ type: 'error', message: 'Deepgram API key not configured' }]);
    expect(client.closes).toEqual([{ code: 1011, reason: 'Configuration error' }]);
  });

  test('logs config_missing_api_key with a requestId when the Deepgram key is not configured', () => {
    delete process.env.DEEPGRAM_API_KEY;
    const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
    const events = createStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
    const client = new FakeClientSocket();

    const { entries } = captureStreamingLogs(() => events.onOpen(new Event('open'), client));

    const line = entries.find((entry) => entry.event === 'ws_streaming.config_missing_api_key');
    expect(line).toBeDefined();
    expect(line!.details).toMatchObject({ provider: 'deepgram', endpoint: '/ws/streaming-deepgram' });
    expect(typeof line!.details.requestId).toBe('string');
    expect(line!.details.requestId).not.toBe('');
    // The client still sees exactly what it saw before (#953 is log-only).
    expect(client.closes).toEqual([{ code: 1011, reason: 'Configuration error' }]);
  });

  test('dials Nova-3 with the retention opt-out and the linear16 shape the client sends', () => {
    const { upstream } = openSession();
    const dialled = new URL(upstream.url);

    expect(dialled.origin + dialled.pathname).toBe('wss://api.deepgram.com/v1/listen');
    expect(dialled.searchParams.get('model')).toBe('nova-3');
    expect(dialled.searchParams.get('mip_opt_out')).toBe('true');
    expect(dialled.searchParams.get('encoding')).toBe('linear16');
    expect(dialled.searchParams.get('sample_rate')).toBe('16000');
    expect(dialled.searchParams.get('channels')).toBe('1');
    expect(upstream.protocols).toEqual(['token', 'test-deepgram-key']);
  });

  test('sends ready with a session id once Deepgram accepts the handshake', () => {
    const { client } = openSession();
    const ready = client.messagesOfType('ready');

    expect(ready).toHaveLength(1);
    expect(typeof ready[0]!.sessionId).toBe('string');
    expect((ready[0]!.sessionId as string).length).toBeGreaterThan(0);
  });

  test('tears down the upstream socket when the client left during the handshake', () => {
    const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
    const events = createStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
    const client = new FakeClientSocket();

    events.onOpen(new Event('open'), client);
    client.readyState = 3; // client gave up while Deepgram was still connecting
    upstreamSockets[0]!.handshake();

    expect(client.messagesOfType('ready')).toHaveLength(0);
    expect(upstreamSockets[0]!.closes).toEqual([{ code: 1000, reason: 'Client disconnected' }]);
  });

  describe('vocabulary boosting', () => {
    test('sends one repeated keyterm per term in monolingual mode', () => {
      const { upstream } = openSession({
        query: '&language=en&vocabulary=HyperWhisper,%20Fly.io%3B%20Deepgram',
      });
      const params = new URL(upstream.url).searchParams;

      expect(params.get('language')).toBe('en');
      expect(params.getAll('keyterm')).toEqual(['HyperWhisper', 'Fly.io', 'Deepgram']);
      // The legacy nova-2 syntax must not reappear: no `keywords`, no `:boost`.
      expect(params.getAll('keywords')).toEqual([]);
      expect(upstream.url).not.toContain('%3A1.5');
    });

    test('drops keyterms in auto-detect mode, where Nova-3 ignores them', () => {
      const { upstream } = openSession({ query: '&language=auto&vocabulary=HyperWhisper,Fly.io' });
      const params = new URL(upstream.url).searchParams;

      expect(params.has('language')).toBe(false);
      expect(params.getAll('keyterm')).toEqual([]);
    });

    test('omits the language param when the client sends none', () => {
      const { upstream } = openSession();

      expect(new URL(upstream.url).searchParams.has('language')).toBe(false);
    });

    test('applies the same 100 term and 50 character caps as the REST adapter', () => {
      const tooLong = 'x'.repeat(51);
      const terms = [tooLong, ...Array.from({ length: 101 }, (_, index) => `term${index}`)];
      const { upstream } = openSession({
        query: `&language=en&vocabulary=${encodeURIComponent(terms.join(','))}`,
      });
      const keyterms = new URL(upstream.url).searchParams.getAll('keyterm');

      expect(keyterms).toHaveLength(100);
      expect(keyterms).not.toContain(tooLong);
      expect(keyterms[0]).toBe('term0');
    });
  });

  describe('transcript relay', () => {
    test('forwards a Results frame with its finality flags', async () => {
      const harness = openSession();

      harness.upstream.deliver({
        type: 'Results',
        is_final: true,
        speech_final: true,
        channel: { alternatives: [{ transcript: 'hello world' }] },
      });

      expect(harness.client.messagesOfType('transcript')).toEqual([
        { type: 'transcript', text: 'hello world', is_final: true, speech_final: true },
      ]);
      await harness.endSession();
    });

    test('defaults the finality flags Deepgram omits to false', async () => {
      const harness = openSession();

      harness.upstream.deliver({
        type: 'Results',
        channel: { alternatives: [{ transcript: 'interim text' }] },
      });

      expect(harness.client.messagesOfType('transcript')[0]).toEqual({
        type: 'transcript',
        text: 'interim text',
        is_final: false,
        speech_final: false,
      });
      await harness.endSession();
    });

    test('ignores an empty interim result but relays an empty final one', async () => {
      const harness = openSession();

      harness.upstream.deliver({ type: 'Results', is_final: false, channel: { alternatives: [{ transcript: '' }] } });
      expect(harness.client.messagesOfType('transcript')).toHaveLength(0);

      harness.upstream.deliver({ type: 'Results', is_final: true, channel: { alternatives: [{ transcript: '' }] } });
      expect(harness.client.messagesOfType('transcript')).toHaveLength(1);
      await harness.endSession();
    });

    test('ignores non-Results frames and malformed JSON without failing the session', async () => {
      const harness = openSession();

      harness.upstream.deliver({ type: 'Metadata', duration: 12 });
      harness.upstream.deliver({ type: 'UtteranceEnd' });
      harness.upstream.deliver('not json at all');

      expect(harness.client.messagesOfType('transcript')).toHaveLength(0);
      expect(harness.client.messagesOfType('error')).toHaveLength(0);
      expect(harness.client.closes).toHaveLength(0);
      await harness.endSession();
    });

    test('reports an upstream error to the client', async () => {
      const harness = openSession();

      harness.upstream.emit('error', {});

      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service error' },
      ]);
      await harness.endSession();
    });

    test('logs upstream_socket_error when the upstream socket fires an error event', async () => {
      const harness = openSession();

      const { entries } = captureStreamingLogs(() => harness.upstream.emit('error', {}));

      const line = entries.find((entry) => entry.event === 'ws_streaming.upstream_socket_error');
      expect(line).toBeDefined();
      expect(line!.details).toMatchObject({ provider: 'deepgram', eventType: null, errorName: null });
      expect(line!.details).not.toHaveProperty('message');
      expect(typeof line!.details.requestId).toBe('string');
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service error' },
      ]);
      await harness.endSession();
    });

    test('logs upstream_parse_failed for a non-JSON frame, and no line carries the frame text', async () => {
      const harness = openSession();
      // A frame that fails to parse can still hold the user's words. It opens
      // with a bare word because Bun's SyntaxError message quotes that token
      // ("Unexpected identifier \"tangerine\""), so logging error.message fails.
      const frame = 'tangerine otter is my secret phrase';

      const { entries, serialised } = captureStreamingLogs(() => harness.upstream.deliver(frame));

      const line = entries.find((entry) => entry.event === 'ws_streaming.upstream_parse_failed');
      expect(line).toBeDefined();
      expect(line!.details).toMatchObject({ provider: 'deepgram', errorName: 'SyntaxError', frameLength: frame.length });
      expect(typeof line!.details.requestId).toBe('string');
      expect(entries.some((entry) => entry.event === 'ws_streaming.upstream_event_failed')).toBe(false);
      expect(serialised.length).toBeGreaterThan(0);
      for (const text of serialised) {
        expect(text).not.toContain('tangerine');
      }
      await harness.endSession();
    });

    test('logs upstream_event_failed, not a parse failure, when our client send throws on a valid Results frame', async () => {
      const harness = openSession();
      const send = harness.client.send.bind(harness.client);
      harness.client.send = () => { throw new Error('socket write failed'); };

      const { entries, serialised } = captureStreamingLogs(() => harness.upstream.deliver({
        type: 'Results',
        is_final: true,
        channel: { alternatives: [{ transcript: 'hello there' }] },
      }));

      const line = entries.find((entry) => entry.event === 'ws_streaming.upstream_event_failed');
      expect(line).toBeDefined();
      expect(line!.details).toMatchObject({ provider: 'deepgram', errorName: 'Error' });
      expect(line!.details).not.toHaveProperty('message');
      // An error's free text is never logged: it is not guaranteed transcript-free.
      for (const text of serialised) expect(text).not.toContain('socket write failed');
      expect(typeof line!.details.requestId).toBe('string');
      expect(entries.some((entry) => entry.event === 'ws_streaming.upstream_parse_failed')).toBe(false);
      harness.client.send = send;
      await harness.endSession();
    });
  });

  describe('inbound audio limits', () => {
    test('rejects a single frame above 1 MB and never forwards or bills it', async () => {
      const harness = openSession();

      harness.events.onMessage(binaryMessage(new ArrayBuffer(1024 * 1024 + 1)));

      expect(harness.upstream.audioFramesForwarded).toBe(0);
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Audio chunk too large' },
      ]);
      expect(harness.client.closes).toHaveLength(0);

      await harness.endSession();
      expect(harness.client.messagesOfType('session_complete')[0]!.duration_seconds).toBe(0);
    });

    test('closes the socket with 1009 once the session passes 100 MB of audio', async () => {
      const harness = openSession({ credits: 1_000_000 });
      const oneMegabyte = new ArrayBuffer(1024 * 1024);

      for (let frame = 0; frame < 100; frame += 1) {
        harness.events.onMessage(binaryMessage(oneMegabyte));
      }
      expect(harness.upstream.audioFramesForwarded).toBe(100);
      expect(harness.client.messagesOfType('error')).toHaveLength(0);

      harness.events.onMessage(binaryMessage(oneMegabyte));

      expect(harness.upstream.audioFramesForwarded).toBe(100);
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Audio stream too large' },
      ]);
      expect(harness.client.closes).toEqual([{ code: 1009, reason: 'Message too big' }]);
      await harness.endSession();
    });

    test('counts rejected oversized frames toward the session cap', async () => {
      const harness = openSession({ credits: 1_000_000 });
      const oversized = new ArrayBuffer(2 * 1024 * 1024);

      // 50 frames of 2 MB each are all rejected per-frame, yet still add up to
      // the 100 MB session cap, so the 51st closes the socket.
      for (let frame = 0; frame < 51; frame += 1) {
        harness.events.onMessage(binaryMessage(oversized));
      }

      expect(harness.upstream.audioFramesForwarded).toBe(0);
      expect(harness.client.closes).toEqual([{ code: 1009, reason: 'Message too big' }]);
      await harness.endSession();
    });

    test('drops a chunk instead of queueing it when Deepgram is congested', async () => {
      const harness = openSession();
      harness.upstream.bufferedAmount = 2 * 1024 * 1024;

      harness.events.onMessage(binaryMessage(audioFrame(1)));

      expect(harness.upstream.audioFramesForwarded).toBe(0);
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service busy, audio dropped' },
      ]);

      harness.upstream.bufferedAmount = 0;
      harness.events.onMessage(binaryMessage(audioFrame(2)));
      expect(harness.upstream.audioFramesForwarded).toBe(1);

      await harness.endSession();
      // Only the forwarded 2 seconds are billable — the dropped chunk is not.
      expect(harness.client.messagesOfType('session_complete')[0]!.duration_seconds).toBe(2);
    });

    test('ignores audio that arrives before the upstream socket is open', async () => {
      const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
      const events = createStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
      const client = new FakeClientSocket();
      events.onOpen(new Event('open'), client);

      events.onMessage(binaryMessage(audioFrame(3)));

      expect(upstreamSockets[0]!.audioFramesForwarded).toBe(0);
      await events.onClose();
      await drainPendingDeductions(2000);
    });
  });

  describe('mid-session credit enforcement', () => {
    test('cuts the session off at the balance seen at auth, not at the end of it', async () => {
      // 1.0 credit buys 10 seconds of Nova-3 audio.
      const harness = openSession({ credits: 1 });

      for (let second = 0; second < 9; second += 1) {
        harness.events.onMessage(binaryMessage(audioFrame(1)));
      }
      expect(creditsForCost(computeDeepgramTranscriptionCost(9))).toBe(0.9);
      expect(harness.upstream.closes).toHaveLength(0);
      expect(harness.client.messagesOfType('error')).toHaveLength(0);

      harness.events.onMessage(binaryMessage(audioFrame(1)));

      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Credit balance exhausted' },
      ]);
      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Credits exhausted' }]);
      await harness.endSession();
    });

    test('stops forwarding audio after the credit cutoff closes the upstream socket', async () => {
      const harness = openSession({ credits: 1 });

      for (let second = 0; second < 12; second += 1) {
        harness.events.onMessage(binaryMessage(audioFrame(1)));
      }

      expect(harness.upstream.audioFramesForwarded).toBe(10);
      await harness.endSession();
      expect(harness.client.messagesOfType('session_complete')[0]!.duration_seconds).toBe(10);
    });
  });

  describe('client control frames', () => {
    test('closes the upstream socket on a stop message', async () => {
      const harness = openSession();

      harness.events.onMessage(textMessage(JSON.stringify({ type: 'stop' })));

      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Client requested stop' }]);
      await harness.endSession();
    });

    test('ignores a pong and any non-JSON text frame', async () => {
      const harness = openSession();

      harness.events.onMessage(textMessage(JSON.stringify({ type: 'pong' })));
      harness.events.onMessage(textMessage('hello'));
      harness.events.onMessage(textMessage(JSON.stringify({ type: 'something_else' })));

      expect(harness.upstream.closes).toHaveLength(0);
      expect(harness.client.messagesOfType('error')).toHaveLength(0);
      await harness.endSession();
    });
  });

  describe('end of session billing', () => {
    test('charges the license API for the audio actually forwarded', async () => {
      const harness = openSession();

      harness.events.onMessage(binaryMessage(audioFrame(20)));
      const pending = await harness.endSession();

      expect(pending).toBe(1);
      expect(harness.client.messagesOfType('session_complete')).toEqual([
        {
          type: 'session_complete',
          duration_seconds: 20,
          credits_used: creditsForCost(computeDeepgramTranscriptionCost(20)),
        },
      ]);
      expect(licenseCharges).toHaveLength(1);
      expect(licenseCharges[0]!.license_key).toBe('key-1234-abcd');
      expect(licenseCharges[0]!.amount).toBe(creditsForCost(computeDeepgramTranscriptionCost(20)));
      expect(licenseCharges[0]!.metadata).toMatchObject({
        audio_duration_seconds: 20,
        endpoint: '/ws/streaming-deepgram',
        stt_provider: 'deepgram-nova3-live',
        language: 'auto',
      });
    });

    test('records the explicit language on the charge', async () => {
      const harness = openSession({ query: '&language=de' });

      harness.events.onMessage(binaryMessage(audioFrame(10)));
      await harness.endSession();

      expect(licenseCharges[0]!.metadata.language).toBe('de');
    });

    test('bills once when Deepgram closes and the client close follows', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(5)));

      harness.upstream.emit('close', {});
      await harness.endSession();
      await harness.events.onError();
      await drainPendingDeductions(2000);

      expect(licenseCharges).toHaveLength(1);
      expect(harness.client.messagesOfType('session_complete')).toHaveLength(1);
    });

    test('logs the upstream close code, though this vendor defines no parseUpstreamClose', async () => {
      // The log used to be nested inside `if (vendor.parseUpstreamClose)`, a
      // hook only Gemini defines — so the route carrying most of the revenue
      // never recorded why Deepgram hung up.
      const harness = openSession();
      const logged: unknown[][] = [];
      const originalLog = console.log;
      console.log = (...args: unknown[]) => { logged.push(args); };
      try {
        harness.upstream.emit('close', { code: 1011, reason: 'upstream exploded' });
        await drainPendingDeductions(2000);
      } finally {
        console.log = originalLog;
      }

      const closeLine = logged.find(([event]) => event === 'ws_streaming.upstream_close');
      expect(closeLine).toBeDefined();
      expect(closeLine![1]).toMatchObject({
        provider: 'deepgram',
        code: 1011,
        reason: 'upstream exploded',
      });
    });

    test('closes the client socket when Deepgram ends the session', async () => {
      const harness = openSession();

      harness.upstream.emit('close', {});
      await drainPendingDeductions(2000);

      expect(harness.client.closes).toEqual([{ code: 1000, reason: 'Session ended' }]);
    });

    test('reports a WebSocket error to the client and still settles the session', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(3)));

      await harness.events.onError();
      await drainPendingDeductions(2000);

      expect(harness.client.messagesOfType('error')).toEqual([{ type: 'error', message: 'WebSocket error' }]);
      expect(harness.client.messagesOfType('session_complete')).toHaveLength(1);
      expect(licenseCharges).toHaveLength(1);
      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Client disconnected' }]);
    });
  });

  // #1191: the 0.1-credit minimum applies ONLY to a session that reached the
  // vendor (Ray, 2026-10-07). "Reached" = the upstream handshake was accepted.
  describe('a session that never reached the vendor bills nothing (#1191)', () => {
    test('no DEEPGRAM_API_KEY: the session deducts 0 credits and logs creditsUsed 0', async () => {
      delete process.env.DEEPGRAM_API_KEY;
      const account = 'key-1234-abcd';
      const auth: AuthContext = { identifier: account, licenseKey: account, credits: 100 };
      const events = createStreamingEvents(
        fakeContext(auth, `https://transcribe.example/ws/streaming-deepgram?account_key=${auth.licenseKey}`),
      );
      const client = new FakeClientSocket();

      let closing: Promise<void> = Promise.resolve();
      const { entries } = captureStreamingLogs(() => {
        events.onOpen(new Event('open'), client);
        // The 1011 close is followed by hono's onClose, which settles the session.
        closing = events.onClose();
      });
      await closing;

      expect(await drainPendingDeductions(2000)).toBe(0);
      expect(licenseCharges).toEqual([]);
      expect(upstreamSockets).toHaveLength(0);
      const sessionEnds = entries.filter((entry) => entry.event === 'ws_streaming.session_end');
      expect(sessionEnds).toHaveLength(1);
      expect(sessionEnds[0]!.details).toMatchObject({
        durationSeconds: 0,
        costUsd: 0,
        creditsUsed: 0,
        upstreamReached: false,
      });
    });

    test('Deepgram refuses the handshake: the client is told credits_used 0 and nothing is deducted', async () => {
      const account = 'key-1234-abcd';
      const auth: AuthContext = { identifier: account, licenseKey: account, credits: 100 };
      const events = createStreamingEvents(
        fakeContext(auth, `https://transcribe.example/ws/streaming-deepgram?account_key=${auth.licenseKey}`),
      );
      const client = new FakeClientSocket();
      events.onOpen(new Event('open'), client);
      const upstream = upstreamSockets[upstreamSockets.length - 1]!;

      // The upgrade is answered with a non-101 status: error, then close, no open.
      upstream.emit('error', {});
      upstream.emit('close', { code: 1006, reason: '' });
      await drainPendingDeductions(2000);

      expect(client.messagesOfType('session_complete')).toEqual([
        { type: 'session_complete', duration_seconds: 0, credits_used: 0 },
      ]);
      expect(licenseCharges).toEqual([]);
    });

    test('a session that DID reach Deepgram still pays the 0.1 minimum with 0 audio', async () => {
      const harness = openSession();

      expect(await harness.endSession()).toBe(1);

      expect(harness.client.messagesOfType('session_complete')).toEqual([
        { type: 'session_complete', duration_seconds: 0, credits_used: 0.1 },
      ]);
      expect(licenseCharges).toHaveLength(1);
      expect(licenseCharges[0]!.amount).toBe(0.1);
    });
  });

  describe('machine shutdown (#1235)', () => {
    // A session opened by an earlier test and never closed is still registered.
    // Settle those first, so each test counts only the sessions it opens.
    async function settleLeftoverSessions(): Promise<void> {
      await endActiveStreamingSessions();
      // Ending latches the machine as shutting down; reopen it for this test.
      resetStreamingShutdownForTests();
      await drainPendingDeductions(2000);
      licenseCharges.length = 0;
    }

    // The latch is module state and bun runs every test file in one process.
    afterEach(() => resetStreamingShutdownForTests());

    test('bills an open session, logs session_end, and closes the client with 1012 without finishing it', async () => {
      await settleLeftoverSessions();
      const harness = openSession();
      for (let second = 0; second < 60; second += 1) {
        harness.events.onMessage(binaryMessage(audioFrame(1)));
      }
      expect(harness.upstream.audioFramesForwarded).toBe(60);

      let ending: Promise<number> = Promise.resolve(-1);
      const { entries, serialised } = captureStreamingLogs(() => {
        ending = endActiveStreamingSessions();
      });
      expect(await ending).toBe(1);
      expect(await drainPendingDeductions(2000)).toBe(1);

      expect(licenseCharges).toHaveLength(1);
      expect(licenseCharges[0]!.metadata.audio_duration_seconds).toBe(60);
      expect(licenseCharges[0]!.amount).toBe(creditsForCost(computeDeepgramTranscriptionCost(60)));
      const sessionEnds = entries.filter((entry) => entry.event === 'ws_streaming.session_end');
      expect(sessionEnds).toHaveLength(1);
      expect(sessionEnds[0]!.details).toMatchObject({ durationSeconds: 60 });
      for (const line of serialised) expect(line).not.toContain('key-1234-abcd');

      // 1012 Service Restart is non-terminal in all three clients, so they
      // reconnect. session_complete would make them finish the dictation.
      expect(harness.client.closes).toEqual([{ code: 1012, reason: 'Service restart' }]);
      expect(harness.client.messagesOfType('session_complete')).toEqual([]);
      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Server shutting down' }]);

      // The socket handlers that follow must not post a second charge.
      harness.upstream.emit('close', { code: 1000, reason: '' });
      await harness.events.onClose();
      await drainPendingDeductions(2000);
      expect(licenseCharges).toHaveLength(1);
      expect(harness.client.closes).toHaveLength(1);
      expect(harness.client.messagesOfType('session_complete')).toEqual([]);

      // ...and the session left the set.
      expect(await endActiveStreamingSessions()).toBe(0);
    });

    test('a session that opens after shutdown began gets 1012, no upstream, and no charge', async () => {
      await settleLeftoverSessions();
      upstreamSockets.length = 0;
      expect(await endActiveStreamingSessions()).toBe(0);

      // A preflight that was awaiting auth when the snapshot was taken.
      const auth: AuthContext = { identifier: 'late', licenseKey: 'late', credits: 1000 };
      const events = createStreamingEvents(
        fakeContext(auth, 'https://transcribe.example/ws/streaming-deepgram?account_key=late'),
      );
      const client = new FakeClientSocket();
      const { entries } = captureStreamingLogs(() => {
        events.onOpen(new Event('open'), client);
      });

      expect(client.closes).toEqual([{ code: 1012, reason: 'Service restart' }]);
      expect(upstreamSockets).toHaveLength(0);
      expect(entries.map((entry) => entry.event)).not.toContain('ws_streaming.session_start');

      // Audio sent before the close lands, and the close itself, meter nothing.
      for (let second = 0; second < 30; second += 1) {
        events.onMessage(binaryMessage(audioFrame(1)));
      }
      await events.onClose();
      await drainPendingDeductions(2000);
      expect(licenseCharges).toEqual([]);
      expect(client.messagesOfType('session_complete')).toEqual([]);
      expect(await endActiveStreamingSessions()).toBe(0);
    });

    test('a session whose shutdown throws does not stop the others being ended', async () => {
      await settleLeftoverSessions();
      const broken = openSession();
      broken.client.close = () => { throw new Error('socket already torn down'); };
      broken.events.onMessage(binaryMessage(audioFrame(10)));
      const healthy = openSession();
      healthy.events.onMessage(binaryMessage(audioFrame(20)));

      let ending: Promise<number> = Promise.resolve(-1);
      const errors: unknown[][] = [];
      const realError = console.error;
      console.error = (...args: unknown[]) => { errors.push(args); };
      try {
        captureStreamingLogs(() => {
          ending = endActiveStreamingSessions();
        });
        expect(await ending).toBe(1);
      } finally {
        console.error = realError;
      }
      await drainPendingDeductions(2000);

      expect(healthy.client.closes).toEqual([{ code: 1012, reason: 'Service restart' }]);
      // Both were billed: the throw came after the broken session's endSession.
      expect(licenseCharges.map((charge) => charge.metadata.audio_duration_seconds).sort()).toEqual([10, 20]);
      expect(errors).toEqual([['ws_streaming.shutdown_session_failed', { errorName: 'Error' }]]);
      expect(await endActiveStreamingSessions()).toBe(0);
    });

    test('a session the client already closed is not ended again at shutdown', async () => {
      await settleLeftoverSessions();
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(5)));
      await harness.endSession();
      expect(harness.client.messagesOfType('session_complete')).toHaveLength(1);

      expect(await endActiveStreamingSessions()).toBe(0);
      await drainPendingDeductions(2000);
      expect(licenseCharges).toHaveLength(1);
    });
  });
});
