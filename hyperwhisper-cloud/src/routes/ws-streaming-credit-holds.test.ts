// Live sessions share the balance through a credit hold (#2008): the upgrade
// holds five minutes of the vendor's rate, the session tops the hold up as its
// metering crosses it, and a refused top-up ends the session. Driven through
// the real Deepgram preflight and socket handlers; the store is the in-memory
// twin of the Lua script, and only `../lib/redis` and the sockets are faked.

import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import type { Context } from 'hono';
import type { WSMessageReceive } from 'hono/ws';
import { computeDeepgramTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
import { createMemoryCreditHoldStore, type MemoryCreditHoldStore } from '../lib/credit-hold-store-memory';
import type { AuthContext } from '../middleware/auth';

let cachedLicenseValue: { isValid: boolean; credits: number; cachedAt: string } | null = null;

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async () => false,
  getCachedLicense: async () => cachedLicenseValue,
  cacheLicense: async () => {},
}));

const { createStreamingEvents, wsStreamingPreflight } = await import('./ws-streaming-deepgram');
const { drainPendingDeductions } = await import('../middleware/credits');
const {
  drainPendingHoldReleases,
  reserveSessionHold,
  setCreditHoldStoreForTests,
} = await import('../middleware/credit-holds');
type CreditHold = Awaited<ReturnType<typeof reserveSessionHold>>['hold'];

const ACCOUNT = 'acct-live-holds';
const BYTES_PER_SECOND = 16000 * 2;
/** Five minutes of Nova-3, the block a session holds at open and per top-up. */
const BLOCK = creditsForCost(computeDeepgramTranscriptionCost(300));

const originalFetch = globalThis.fetch;
const originalWebSocket = globalThis.WebSocket;
const originalApiKey = process.env.DEEPGRAM_API_KEY;
const originalLog = console.log;
const originalWarn = console.warn;

let store: MemoryCreditHoldStore;
let charges: number[];
let logLines: Array<{ event: string; details: Record<string, unknown> }>;
let shortfalls: Array<Record<string, unknown>>;
const upstreamSockets: FakeUpstreamSocket[] = [];

class FakeUpstreamSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  readyState = 0;
  bufferedAmount = 0;
  readonly closes: Array<{ code?: number; reason?: string }> = [];
  private readonly listeners = new Map<string, Array<(evt: unknown) => void>>();

  constructor(readonly url: string) {
    upstreamSockets.push(this);
  }

  addEventListener(type: string, handler: (evt: unknown) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), handler]);
  }

  send(): void {}

  close(code?: number, reason?: string): void {
    this.readyState = FakeUpstreamSocket.CLOSED;
    this.closes.push({ code, reason });
  }

  handshake(): void {
    this.readyState = FakeUpstreamSocket.OPEN;
    for (const handler of this.listeners.get('open') ?? []) handler({});
  }
}

class FakeClientSocket {
  readyState = 1;
  readonly sent: string[] = [];

  send(data: string | ArrayBuffer | Uint8Array): void {
    this.sent.push(typeof data === 'string' ? data : '<binary>');
  }

  close(): void {
    this.readyState = 3;
  }

  errors(): unknown[] {
    return this.sent.map((raw) => JSON.parse(raw) as { type: string }).filter((message) => message.type === 'error');
  }
}

function auth(credits: number): AuthContext {
  return { identifier: ACCOUNT, licenseKey: ACCOUNT, credits };
}

function fakeContext(sessionAuth: AuthContext, hold: CreditHold | undefined): Context {
  const vars: Record<string, unknown> = { wsAuth: sessionAuth, wsClientIP: '203.0.113.7', wsCreditHold: hold };
  return {
    get: (key: string) => vars[key],
    req: { url: `https://transcribe.example/ws/streaming-deepgram?account_key=${ACCOUNT}` },
  } as unknown as Context;
}

function second(): MessageEvent<WSMessageReceive> {
  return { data: new ArrayBuffer(BYTES_PER_SECOND) } as MessageEvent<WSMessageReceive>;
}

/** Let a top-up's store round trip land. */
async function tick(): Promise<void> {
  for (let i = 0; i < 5; i += 1) await Promise.resolve();
  await Bun.sleep(0);
}

async function openSession(sessionAuth: AuthContext, hold: CreditHold) {
  const events = createStreamingEvents(fakeContext(sessionAuth, hold));
  const client = new FakeClientSocket();
  events.onOpen(new Event('open'), client);
  const upstream = upstreamSockets[upstreamSockets.length - 1]!;
  upstream.handshake();
  return { events, client, upstream };
}

async function settle(): Promise<void> {
  await drainPendingDeductions(2000);
  await drainPendingHoldReleases(2000);
}

/** Another session or request on the same key, holding `credits`. */
async function holdElsewhere(credits: number, balance: number): Promise<void> {
  await store.reserve({
    account: ACCOUNT, holdId: 'elsewhere', minMilli: credits * 1000, maxMilli: credits * 1000,
    balanceMilli: balance * 1000, nowMs: Date.now(), ttlMs: 60_000,
  });
}

beforeEach(() => {
  store = createMemoryCreditHoldStore();
  setCreditHoldStoreForTests(store);
  charges = [];
  logLines = [];
  shortfalls = [];
  upstreamSockets.length = 0;
  cachedLicenseValue = { isValid: true, credits: 100, cachedAt: 'cached' };
  process.env.DEEPGRAM_API_KEY = 'test-deepgram-key';
  globalThis.WebSocket = FakeUpstreamSocket as unknown as typeof WebSocket;
  globalThis.fetch = mock(async (_input: RequestInfo | URL, init?: RequestInit) => {
    charges.push((JSON.parse(String(init?.body)) as { amount: number }).amount);
    return Response.json({ credits_remaining: 0 });
  }) as unknown as typeof fetch;
  console.log = (event: unknown, details?: unknown) => {
    if (typeof event === 'string') logLines.push({ event, details: (details ?? {}) as Record<string, unknown> });
  };
  console.warn = (...args: unknown[]) => {
    const first = args[0];
    if (typeof first === 'string' && first.includes('credits_shortfall')) {
      shortfalls.push(JSON.parse(first) as Record<string, unknown>);
      return;
    }
    originalWarn(...args);
  };
});

afterEach(async () => {
  await settle();
  console.log = originalLog;
  console.warn = originalWarn;
  setCreditHoldStoreForTests(null);
  globalThis.fetch = originalFetch;
  globalThis.WebSocket = originalWebSocket;
  cachedLicenseValue = null;
  if (originalApiKey === undefined) delete process.env.DEEPGRAM_API_KEY;
  else process.env.DEEPGRAM_API_KEY = originalApiKey;
});

describe('the upgrade preflight holds the first block', () => {
  function buildApp(): Hono {
    const app = new Hono();
    app.get('/ws/streaming-deepgram', wsStreamingPreflight, (c) => {
      const hold = c.get('wsCreditHold');
      return c.json({ heldCredits: hold?.heldCredits ?? null });
    });
    return app;
  }

  function upgrade(app: Hono): Promise<Response> {
    return Promise.resolve(app.request(`/ws/streaming-deepgram?account_key=${ACCOUNT}`, {
      headers: { Upgrade: 'websocket', 'Fly-Client-IP': '203.0.113.7' },
    }));
  }

  test('holds five minutes of the vendor rate when the balance covers it', async () => {
    const res = await upgrade(buildApp());
    expect(res.status).toBe(200);
    expect(BLOCK).toBeGreaterThan(20);
    expect(await res.json()).toEqual({ heldCredits: BLOCK });
    expect(store.outstandingMilli(ACCOUNT)).toBe(BLOCK * 1000);
  });

  test('holds what is left when that is less than a block but above the 30 s floor', async () => {
    cachedLicenseValue = { isValid: true, credits: 10, cachedAt: 'cached' };
    const res = await upgrade(buildApp());
    expect(await res.json()).toEqual({ heldCredits: 10 });
  });

  test('of concurrent upgrades on a balance that covers one floor, one opens and the rest get 402', async () => {
    // The 30 s floor is 2.8 credits; 4 credits covers one session, not two.
    cachedLicenseValue = { isValid: true, credits: 4, cachedAt: 'cached' };
    const app = buildApp();
    const results = await Promise.all(Array.from({ length: 5 }, () => upgrade(app)));
    const statuses = results.map((res) => res.status).sort();
    expect(statuses).toEqual([200, 402, 402, 402, 402]);
    const refused = results.find((res) => res.status === 402)!;
    expect(await refused.json()).toMatchObject({ error: 'Insufficient credits', credits_remaining: 0 });
    expect(logLines.filter((line) => line.event === 'ws_streaming.request_rejected')
      .every((line) => line.details.heldElsewhere === true)).toBe(true);
  });
});

describe('a live session tops its hold up', () => {
  test('grants the next block when metering reaches the hold, and the session keeps streaming', async () => {
    const sessionAuth = auth(100);
    // A one-credit hold is ten seconds of Nova-3.
    const { hold } = await reserveSessionHold(sessionAuth, 1, 1);
    const { events, client, upstream } = await openSession(sessionAuth, hold);

    for (let s = 0; s < 10; s += 1) events.onMessage(second());
    await tick();

    expect(hold.heldCredits).toBe(1 + BLOCK);
    expect(logLines.some((line) => line.event === 'ws_streaming.credit_topup')).toBe(true);
    for (let s = 0; s < 5; s += 1) events.onMessage(second());
    expect(client.errors()).toEqual([]);
    expect(upstream.closes).toEqual([]);

    await events.onClose();
    await settle();
    expect(charges).toEqual([creditsForCost(computeDeepgramTranscriptionCost(15))]);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('a refused top-up ends the session with Credit balance exhausted', async () => {
    const sessionAuth = auth(100);
    await holdElsewhere(99, 100);
    const { hold } = await reserveSessionHold(sessionAuth, 1, 1);
    const { events, client, upstream } = await openSession(sessionAuth, hold);

    for (let s = 0; s < 10; s += 1) events.onMessage(second());
    await tick();

    expect(client.errors()).toEqual([{ type: 'error', message: 'Credit balance exhausted' }]);
    expect(upstream.closes).toEqual([{ code: 1000, reason: 'Credits exhausted' }]);
    const cutoff = logLines.find((line) => line.event === 'ws_streaming.credit_cutoff');
    expect(cutoff?.details).toMatchObject({ reason: 'topup_refused', heldCredits: 1 });

    await events.onClose();
    await settle();
    expect(charges).toEqual([1]);
    expect(shortfalls).toEqual([]);
    expect([...store.holds(ACCOUNT).keys()]).toEqual(['elsewhere']);
  });

  test('audio metered past the hold during the refused round trip is written off as credits_shortfall', async () => {
    const sessionAuth = auth(100);
    await holdElsewhere(99, 100);
    const { hold } = await reserveSessionHold(sessionAuth, 1, 1);
    const { events } = await openSession(sessionAuth, hold);

    // The eleventh second arrives before the top-up answer does.
    for (let s = 0; s < 11; s += 1) events.onMessage(second());
    await tick();
    await events.onClose();
    await settle();

    expect(charges).toEqual([1]);
    expect(shortfalls).toEqual([{
      event: 'credits_shortfall',
      source: 'cloud',
      reason: 'session_above_hold',
      endpoint: '/ws/streaming-deepgram',
      creditsUsed: 1.1,
      creditsCharged: 1,
      heldCredits: 1,
      shortfallCredits: 0.1,
    }]);
    const end = logLines.find((line) => line.event === 'ws_streaming.session_end');
    expect(end?.details).toMatchObject({ creditsUsed: 1, clampedToHeldCredits: 1 });
    expect([...store.holds(ACCOUNT).keys()]).toEqual(['elsewhere']);
  });

  test('a session that never reached the vendor bills nothing and releases its hold', async () => {
    const sessionAuth = auth(100);
    const { hold } = await reserveSessionHold(sessionAuth, 1, BLOCK);
    const events = createStreamingEvents(fakeContext(sessionAuth, hold));
    events.onOpen(new Event('open'), new FakeClientSocket());
    // No handshake.
    await events.onClose();
    await settle();

    expect(charges).toEqual([]);
    expect(store.outstandingMilli(ACCOUNT)).toBe(0);
  });

  test('without Redis (a degraded hold) the session falls back to the balance seen at auth', async () => {
    setCreditHoldStoreForTests({
      reserve: async () => { throw new Error('upstash down'); },
      release: async () => { throw new Error('upstash down'); },
    });
    const originalError = console.error;
    console.error = () => {};
    try {
      const sessionAuth = auth(1);
      const { hold, outcome } = await reserveSessionHold(sessionAuth, 0.5, BLOCK);
      expect(outcome.kind).toBe('unavailable');
      const { events, client } = await openSession(sessionAuth, hold);
      for (let s = 0; s < 10; s += 1) events.onMessage(second());
      expect(client.errors()).toEqual([{ type: 'error', message: 'Credit balance exhausted' }]);
      await events.onClose();
      await settle();
      expect(charges).toEqual([1]);
    } finally {
      console.error = originalError;
    }
  });
});
