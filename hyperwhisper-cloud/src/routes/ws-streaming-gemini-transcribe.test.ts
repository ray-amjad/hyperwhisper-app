import { afterEach, beforeEach, describe, expect, mock, test } from 'bun:test';
import { Hono } from 'hono';
import type { Context } from 'hono';
import type { WSMessageReceive } from 'hono/ws';
import { computeGeminiTranscribeLiveCost, creditsForCost } from '../lib/cost-calculator';
import { drainPendingDeductions } from '../middleware/credits';
import type { AuthContext } from '../middleware/auth';

type CachedLicense = { isValid: boolean; credits: number; cachedAt: string };

// Same discipline as ws-streaming-deepgram.test.ts: mock ONLY redis, so the real
// auth + credit middleware stays in the test and what is asserted below is the
// actual entitlement chain rather than a stub of it. `mock.module` is banned for
// anything else in this repo (process-wide leak).
let cachedLicenseValue: CachedLicense | null = null;
const blockedIPs = new Set<string>();

mock.module('../lib/redis', () => ({
  redis: {},
  isIPBlocked: async (ip: string) => blockedIPs.has(ip),
  getCachedLicense: async (_licenseKey: string) => cachedLicenseValue,
  cacheLicense: async () => {},
}));

const {
  GEMINI_TRANSCRIBE_VENDOR,
  buildAudioFrame,
  buildAudioStreamEndFrame,
  buildLiveWsUrl,
  buildSetupFrame,
  createGeminiTranscribeStreamingEvents,
  minimumGeminiTranscribeStreamingCredits,
  parseGeminiLiveClose,
  parseGeminiLiveFrame,
  wsStreamingGeminiTranscribePreflight,
} = await import('./ws-streaming-gemini-transcribe');
const { routePathFor } = await import('./ws-streaming-shared');

const originalFetch = globalThis.fetch;
const originalWebSocket = globalThis.WebSocket;
const originalApiKey = process.env.GEMINI_API_KEY;
const originalAltApiKey = process.env.GOOGLE_GEMINI_API_KEY;

const BYTES_PER_SECOND = 16000 * 1 * 2;

function neverFetch() {
  return mock(async () => {
    throw new Error('no license API call was expected here');
  }) as unknown as typeof fetch;
}

// ---------------------------------------------------------------------------
// The wire contract, held to the cross-platform conformance vectors
// ---------------------------------------------------------------------------

interface LiveFrameVectors {
  cases: Array<{
    name: string;
    kind: 'setup' | 'audio' | 'audioStreamEnd';
    language?: string | null;
    vocabulary?: string[];
    pcmBase64?: string;
    expect: Record<string, unknown>;
  }>;
  serverMessages: Array<{
    name: string;
    frame: Record<string, unknown>;
    expect: { kind: string; text?: string };
  }>;
}

// The same file the Rust core and the three native streaming strategies are held
// to. Reading it here rather than restating the shapes is the point: if TRAP 3's
// `setup.input_audio_transcription` position ever moves, every consumer moves
// with it or goes red — including this route.
const vectors: LiveFrameVectors = await Bun.file(
  new URL('../../../shared-conformance/live-frame-vectors.json', import.meta.url),
).json();

describe('live frame vectors (shared-conformance/live-frame-vectors.json)', () => {
  test('the vector file still describes the frames this route builds', () => {
    expect(vectors.cases.length).toBeGreaterThan(0);
    expect(vectors.serverMessages.length).toBeGreaterThan(0);
  });

  for (const vector of vectors.cases.filter((c) => c.kind === 'setup')) {
    test(`setup frame matches vector: ${vector.name}`, () => {
      const built = JSON.parse(buildSetupFrame(
        vector.language ?? undefined,
        (vector.vocabulary ?? []).join(','),
      ));

      expect(built).toEqual(vector.expect);
    });
  }

  for (const vector of vectors.cases.filter((c) => c.kind === 'audio')) {
    test(`audio frame matches vector: ${vector.name}`, () => {
      const pcm = Buffer.from(vector.pcmBase64!, 'base64');
      const arrayBuffer = pcm.buffer.slice(pcm.byteOffset, pcm.byteOffset + pcm.byteLength) as ArrayBuffer;

      expect(JSON.parse(buildAudioFrame(arrayBuffer))).toEqual(vector.expect);
    });
  }

  for (const vector of vectors.cases.filter((c) => c.kind === 'audioStreamEnd')) {
    test(`stop frame matches vector: ${vector.name}`, () => {
      expect(JSON.parse(buildAudioStreamEndFrame())).toEqual(vector.expect);
    });
  }

  for (const vector of vectors.serverMessages) {
    test(`server frame decodes as the vector says: ${vector.name}`, () => {
      const events = parseGeminiLiveFrame(JSON.stringify(vector.frame));

      switch (vector.expect.kind) {
        case 'setupComplete':
          expect(events).toEqual([{ kind: 'ready' }]);
          break;
        case 'partialTranscript':
          expect(events).toEqual([
            { kind: 'transcript', text: vector.expect.text!, isFinal: false, speechFinal: false },
          ]);
          break;
        case 'finalTranscript':
          expect(events).toEqual([
            { kind: 'transcript', text: vector.expect.text!, isFinal: true, speechFinal: true },
          ]);
          break;
        case 'complete':
          expect(events).toEqual([{ kind: 'complete' }]);
          break;
        case 'error':
          expect(events).toEqual([{ kind: 'error', message: vector.expect.text!, terminal: true }]);
          break;
        case 'unhandled':
          expect(events).toEqual([]);
          break;
        default:
          throw new Error(`unhandled vector kind ${vector.expect.kind}`);
      }
    });
  }

  test('the setup frame never carries the pre-recorded config path (TRAP 3)', () => {
    const frame = buildSetupFrame('en-US', 'HyperWhisper');

    expect(frame).not.toContain('generation_config');
    expect(frame).not.toContain('transcription_config');
    expect(JSON.parse(frame).setup.input_audio_transcription).toEqual({
      language_codes: ['en-US'],
      custom_vocabulary: ['HyperWhisper'],
    });
  });

  test('vocabulary is sent in auto-detect mode too, unlike Deepgram keyterms', () => {
    // Nova-3 silently drops `keyterm` outside monolingual mode, so the Deepgram
    // route withholds it. Gemini has no such rule — the vector file's
    // `setup-vocabulary-only` case is language-less and still carries terms.
    const config = JSON.parse(buildSetupFrame('auto', 'Kalamazoo,HyperWhisper')).setup.input_audio_transcription;

    expect(config).toEqual({ custom_vocabulary: ['Kalamazoo', 'HyperWhisper'] });
  });

  test('the URL is the BidiGenerateContent socket with the key as a query param', () => {
    const url = new URL(buildLiveWsUrl('AIza-test-key'));

    expect(url.protocol).toBe('wss:');
    expect(url.host).toBe('generativelanguage.googleapis.com');
    expect(url.pathname).toBe('/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent');
    expect(url.searchParams.get('key')).toBe('AIza-test-key');
  });
});

describe('route naming', () => {
  test('derives /ws/streaming-gemini-transcribe from the catalog sttProvider id', () => {
    // Load-bearing: Phase 5's clients build the path from the selected catalog
    // entry. `gemini` is a DIFFERENT provider (the :generateContent LLMs).
    expect(GEMINI_TRANSCRIBE_VENDOR.id).toBe('gemini-transcribe');
    expect(routePathFor(GEMINI_TRANSCRIBE_VENDOR)).toBe('/ws/streaming-gemini-transcribe');
  });
});

describe('minimumGeminiTranscribeStreamingCredits', () => {
  test('requires 30 seconds of live-model audio before a session opens', () => {
    expect(minimumGeminiTranscribeStreamingCredits())
      .toBe(creditsForCost(computeGeminiTranscribeLiveCost(30)));
    expect(minimumGeminiTranscribeStreamingCredits()).toBe(4.6);
  });

  test('is a higher floor than Deepgram, because the audio costs more', async () => {
    const { minimumStreamingCredits } = await import('./ws-streaming-deepgram');

    expect(minimumGeminiTranscribeStreamingCredits()).toBeGreaterThan(minimumStreamingCredits());
  });
});

describe('preflight', () => {
  function buildApp(): Hono {
    const app = new Hono();
    app.get('/ws/streaming-gemini-transcribe', wsStreamingGeminiTranscribePreflight, (c) =>
      c.json({ credits: c.get('wsAuth').credits, clientIP: c.get('wsClientIP') })
    );
    return app;
  }

  const UPGRADE_HEADERS = { Upgrade: 'websocket', 'Fly-Client-IP': '203.0.113.7' } as const;

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
    const res = await buildApp().request('/ws/streaming-gemini-transcribe?account_key=key-1234-abcd');

    expect(res.status).toBe(426);
  });

  test('rejects a blocked IP before any license work happens', async () => {
    blockedIPs.add('203.0.113.7');
    cachedLicenseValue = null;

    const res = await buildApp().request('/ws/streaming-gemini-transcribe?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(403);
  });

  test('accepts the legacy license_key alias installed apps still send', async () => {
    const res = await buildApp().request('/ws/streaming-gemini-transcribe?license_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ credits: 100, clientIP: '203.0.113.7' });
  });

  test('refuses a balance below the Gemini-priced 30 second floor', async () => {
    cachedLicenseValue = {
      isValid: true,
      credits: minimumGeminiTranscribeStreamingCredits() - 0.1,
      cachedAt: 'cached',
    };

    const res = await buildApp().request('/ws/streaming-gemini-transcribe?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });

    expect(res.status).toBe(402);
  });

  test('admits a balance that clears the Gemini floor but not one that only clears Deepgram\'s', async () => {
    const { minimumStreamingCredits } = await import('./ws-streaming-deepgram');
    cachedLicenseValue = { isValid: true, credits: minimumStreamingCredits(), cachedAt: 'cached' };

    const belowGemini = await buildApp().request('/ws/streaming-gemini-transcribe?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });
    expect(belowGemini.status).toBe(402);

    cachedLicenseValue = { isValid: true, credits: minimumGeminiTranscribeStreamingCredits(), cachedAt: 'cached' };
    const atFloor = await buildApp().request('/ws/streaming-gemini-transcribe?account_key=key-1234-abcd', {
      headers: UPGRADE_HEADERS,
    });
    expect(atFloor.status).toBe(200);
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

  /** Simulate Google accepting the WebSocket handshake (NOT the setup frame). */
  handshake(): void {
    this.readyState = FakeUpstreamSocket.OPEN;
    this.emit('open', {});
  }

  /** Simulate `{"setupComplete":{}}` — the frame that actually opens the stream. */
  setupComplete(): void {
    this.deliver({ setupComplete: {} });
  }

  deliver(payload: unknown): void {
    this.emit('message', { data: typeof payload === 'string' ? payload : JSON.stringify(payload) });
  }

  get jsonFrames(): Array<Record<string, unknown>> {
    return this.sent
      .filter((frame): frame is string => typeof frame === 'string')
      .map((frame) => JSON.parse(frame) as Record<string, unknown>);
  }

  get audioFramesForwarded(): number {
    return this.jsonFrames.filter((frame) => {
      const input = frame.realtime_input as Record<string, unknown> | undefined;
      return input !== undefined && input.audio !== undefined;
    }).length;
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
  return { get: (key: string) => vars[key], req: { url } } as unknown as Context;
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
  events: ReturnType<typeof createGeminiTranscribeStreamingEvents>;
  client: FakeClientSocket;
  upstream: FakeUpstreamSocket;
  endSession(): Promise<number>;
}

/** Open a session and (unless told otherwise) complete the setup handshake. */
function openSession(options: { credits?: number; query?: string; skipSetup?: boolean } = {}): Harness {
  const auth: AuthContext = {
    identifier: 'key-1234-abcd',
    licenseKey: 'key-1234-abcd',
    credits: options.credits ?? 1000,
  };
  const url = `https://transcribe.example/ws/streaming-gemini-transcribe?account_key=key-1234-abcd${options.query ?? ''}`;
  const events = createGeminiTranscribeStreamingEvents(fakeContext(auth, url));
  const client = new FakeClientSocket();

  events.onOpen(new Event('open'), client);
  const upstream = upstreamSockets[upstreamSockets.length - 1];
  if (!upstream) throw new Error('no upstream socket was constructed');
  upstream.handshake();
  if (!options.skipSetup) upstream.setupComplete();

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

describe('gemini live socket lifecycle', () => {
  beforeEach(() => {
    licenseCharges.length = 0;
    upstreamSockets.length = 0;
    process.env.GEMINI_API_KEY = 'test-gemini-key';
    delete process.env.GOOGLE_GEMINI_API_KEY;
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
    if (originalApiKey === undefined) delete process.env.GEMINI_API_KEY;
    else process.env.GEMINI_API_KEY = originalApiKey;
    if (originalAltApiKey === undefined) delete process.env.GOOGLE_GEMINI_API_KEY;
    else process.env.GOOGLE_GEMINI_API_KEY = originalAltApiKey;
  });

  test('closes the session when no Gemini key is configured, without dialling upstream', () => {
    delete process.env.GEMINI_API_KEY;
    const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
    const events = createGeminiTranscribeStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
    const client = new FakeClientSocket();

    events.onOpen(new Event('open'), client);

    expect(upstreamSockets).toHaveLength(0);
    expect(client.messages()).toEqual([{ type: 'error', message: 'Gemini API key not configured' }]);
    expect(client.closes).toEqual([{ code: 1011, reason: 'Configuration error' }]);
  });

  test('falls back to GOOGLE_GEMINI_API_KEY, the same pair the HTTP adapter reads', () => {
    delete process.env.GEMINI_API_KEY;
    process.env.GOOGLE_GEMINI_API_KEY = 'alt-key';

    const { upstream } = openSession();

    expect(new URL(upstream.url).searchParams.get('key')).toBe('alt-key');
  });

  test('dials the live socket with no subprotocol and sends the setup frame on open', () => {
    const { upstream } = openSession({ query: '&language=en-US&vocabulary=HyperWhisper', skipSetup: true });

    expect(upstream.protocols).toBeUndefined();
    expect(upstream.jsonFrames).toEqual([
      {
        setup: {
          model: 'models/gemini-3.5-transcribe-live',
          input_audio_transcription: {
            language_codes: ['en-US'],
            custom_vocabulary: ['HyperWhisper'],
          },
        },
      },
    ]);
  });

  test('withholds ready until setupComplete, not merely until the socket opens', () => {
    const harness = openSession({ skipSetup: true });
    expect(harness.client.messagesOfType('ready')).toHaveLength(0);

    harness.upstream.setupComplete();

    const ready = harness.client.messagesOfType('ready');
    expect(ready).toHaveLength(1);
    expect(typeof ready[0]!.sessionId).toBe('string');
  });

  test('tears down the upstream socket when the client left during the handshake', () => {
    const auth: AuthContext = { identifier: 'k', licenseKey: 'k', credits: 100 };
    const events = createGeminiTranscribeStreamingEvents(fakeContext(auth, 'https://x/ws?account_key=k'));
    const client = new FakeClientSocket();

    events.onOpen(new Event('open'), client);
    client.readyState = 3;
    upstreamSockets[0]!.handshake();

    expect(upstreamSockets[0]!.jsonFrames).toHaveLength(0);
    expect(upstreamSockets[0]!.closes).toEqual([{ code: 1000, reason: 'Client disconnected' }]);
  });

  describe('audio framing', () => {
    test('wraps raw PCM as base64 realtime_input rather than relaying binary', async () => {
      const harness = openSession();
      const pcm = new Uint8Array([1, 2, 3, 4]).buffer;

      harness.events.onMessage(binaryMessage(pcm));

      const audio = harness.upstream.jsonFrames.at(-1)!.realtime_input as { audio: Record<string, string> };
      expect(audio.audio.mime_type).toBe('audio/pcm;rate=16000');
      expect(Buffer.from(audio.audio.data, 'base64')).toEqual(Buffer.from([1, 2, 3, 4]));
      expect(harness.upstream.sent.some((frame) => frame instanceof ArrayBuffer)).toBe(false);
      await harness.endSession();
    });

    test('buffers audio that beats setupComplete and flushes it in order', async () => {
      const harness = openSession({ skipSetup: true });

      harness.events.onMessage(binaryMessage(audioFrame(1)));
      harness.events.onMessage(binaryMessage(audioFrame(2)));
      expect(harness.upstream.audioFramesForwarded).toBe(0);

      harness.upstream.setupComplete();

      expect(harness.upstream.audioFramesForwarded).toBe(2);
      await harness.endSession();
      // Buffered audio is real audio: it bills once it reaches Google.
      expect(harness.client.messagesOfType('session_complete')[0]!.duration_seconds).toBe(3);
    });

    test('drops pre-setup audio past the buffer cap instead of growing without bound', async () => {
      const harness = openSession({ skipSetup: true });

      harness.events.onMessage(binaryMessage(new ArrayBuffer(1024 * 1024)));
      harness.events.onMessage(binaryMessage(audioFrame(1)));

      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service busy, audio dropped' },
      ]);
      harness.upstream.setupComplete();
      expect(harness.upstream.audioFramesForwarded).toBe(1);
      await harness.endSession();
    });

    test('measures backpressure against the base64-inflated buffer, not the raw one', async () => {
      const harness = openSession();

      // 1.2 MB buffered is congested for Deepgram's raw threshold but is only
      // ~900 KB of audio here, so this chunk must still go out.
      harness.upstream.bufferedAmount = 1.2 * 1024 * 1024;
      harness.events.onMessage(binaryMessage(audioFrame(1)));
      expect(harness.upstream.audioFramesForwarded).toBe(1);

      harness.upstream.bufferedAmount = 2 * 1024 * 1024;
      harness.events.onMessage(binaryMessage(audioFrame(1)));
      expect(harness.upstream.audioFramesForwarded).toBe(1);
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service busy, audio dropped' },
      ]);
      await harness.endSession();
    });

    test('still enforces the shared per-frame and per-session caps', async () => {
      const harness = openSession({ credits: 1_000_000 });

      harness.events.onMessage(binaryMessage(new ArrayBuffer(1024 * 1024 + 1)));
      expect(harness.upstream.audioFramesForwarded).toBe(0);
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Audio chunk too large' },
      ]);

      const twoMegabytes = new ArrayBuffer(2 * 1024 * 1024);
      for (let frame = 0; frame < 50; frame += 1) {
        harness.events.onMessage(binaryMessage(twoMegabytes));
      }

      expect(harness.upstream.audioFramesForwarded).toBe(0);
      expect(harness.client.closes).toEqual([{ code: 1009, reason: 'Message too big' }]);
      await harness.endSession();
    });
  });

  describe('transcript relay', () => {
    test('relays an interim as a non-final replacement preview', async () => {
      const harness = openSession();

      harness.upstream.deliver({ serverContent: { interimInputTranscription: { text: 'Hello, this is a' } } });

      expect(harness.client.messagesOfType('transcript')).toEqual([
        { type: 'transcript', text: 'Hello, this is a', is_final: false, speech_final: false },
      ]);
      await harness.endSession();
    });

    test('relays a turn transcription as a final delta', async () => {
      const harness = openSession();

      harness.upstream.deliver({ serverContent: { inputTranscription: { text: 'Hello, this is a test.' } } });

      expect(harness.client.messagesOfType('transcript')).toEqual([
        { type: 'transcript', text: 'Hello, this is a test.', is_final: true, speech_final: true },
      ]);
      await harness.endSession();
    });

    test('passes each turn through untouched — the finals are already per-turn deltas', async () => {
      // Verified live over a two-utterance session: the interim restarts at the
      // turn boundary and the second final holds only the second turn's words.
      // Prefix-diffing here would truncate, and re-sending the whole session on
      // every final would duplicate. Straight relay is the correct mapping.
      const harness = openSession();

      harness.upstream.deliver({ serverContent: { interimInputTranscription: { text: 'Hello.' } } });
      harness.upstream.deliver({ serverContent: { interimInputTranscription: { text: 'Hello, this is a test' } } });
      harness.upstream.deliver({ serverContent: { inputTranscription: { text: 'Hello, this is a test.' } } });
      harness.upstream.deliver({ serverContent: { generationComplete: true } });
      harness.upstream.deliver({ serverContent: { interimInputTranscription: { text: 'Let us' } } });
      harness.upstream.deliver({ serverContent: { inputTranscription: { text: 'Let us meet on Wednesday.' } } });

      expect(harness.client.messagesOfType('transcript')).toEqual([
        { type: 'transcript', text: 'Hello.', is_final: false, speech_final: false },
        { type: 'transcript', text: 'Hello, this is a test', is_final: false, speech_final: false },
        { type: 'transcript', text: 'Hello, this is a test.', is_final: true, speech_final: true },
        { type: 'transcript', text: 'Let us', is_final: false, speech_final: false },
        { type: 'transcript', text: 'Let us meet on Wednesday.', is_final: true, speech_final: true },
      ]);
      await harness.endSession();
    });

    test('a mid-session generationComplete is a turn boundary, not the end of the session', async () => {
      const harness = openSession();

      harness.upstream.deliver({ serverContent: { generationComplete: true } });

      expect(harness.upstream.closes).toHaveLength(0);
      expect(harness.client.closes).toHaveLength(0);
      await harness.endSession();
    });

    test('ignores keep-alives, usage frames and malformed JSON without failing the session', async () => {
      const harness = openSession();

      harness.upstream.deliver({ serverContent: {} });
      harness.upstream.deliver({ usageMetadata: { totalTokenCount: 3 } });
      harness.upstream.deliver('not json at all');

      expect(harness.client.messagesOfType('transcript')).toHaveLength(0);
      expect(harness.client.messagesOfType('error')).toHaveLength(0);
      expect(harness.client.closes).toHaveLength(0);
      await harness.endSession();
    });
  });

  describe('stop sequence', () => {
    test('sends audio_stream_end rather than closing, then closes on generationComplete', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(4)));

      harness.events.onMessage(textMessage(JSON.stringify({ type: 'stop' })));

      expect(harness.upstream.jsonFrames.at(-1)).toEqual({ realtime_input: { audio_stream_end: true } });
      expect(harness.upstream.closes).toHaveLength(0);

      // The trailing final still arrives — this is the whole reason the socket
      // is not closed outright on stop.
      harness.upstream.deliver({ serverContent: { inputTranscription: { text: 'trailing words' } } });
      harness.upstream.deliver({ serverContent: { generationComplete: true } });

      expect(harness.client.messagesOfType('transcript').at(-1)).toEqual({
        type: 'transcript', text: 'trailing words', is_final: true, speech_final: true,
      });
      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Session ended' }]);
      await harness.endSession();
    });

    test('closes anyway when the grace period expires with no trailing final', async () => {
      const harness = openSession();

      harness.events.onMessage(textMessage(JSON.stringify({ type: 'stop' })));
      expect(harness.upstream.closes).toHaveLength(0);

      // Verified live: Google leaves the socket open indefinitely after
      // audio_stream_end, so without this backstop the session never settles.
      await Bun.sleep(5_050);

      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Client requested stop' }]);
      await harness.endSession();
    }, 10_000);

    test('ignores a pong and any non-JSON text frame', async () => {
      const harness = openSession();

      harness.events.onMessage(textMessage(JSON.stringify({ type: 'pong' })));
      harness.events.onMessage(textMessage('hello'));

      expect(harness.upstream.jsonFrames).toHaveLength(1); // the setup frame only
      expect(harness.upstream.closes).toHaveLength(0);
      await harness.endSession();
    });
  });

  describe('upstream faults', () => {
    test('maps a 1007 close on a bad key to a terminal error the client will not retry', async () => {
      const harness = openSession({ skipSetup: true });

      harness.upstream.emit('close', { code: 1007, reason: 'API key not valid. Please pass a valid API key.' });
      await drainPendingDeductions(2000);

      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service rejected the credentials: API key not valid' },
      ]);
      // 1011 is in .NET's IsTerminalCloseCode set, and the message carries the
      // `api key not valid` marker macOS's StreamingProviderErrorPolicy reads.
      expect(harness.client.closes).toEqual([{ code: 1011, reason: 'Upstream error' }]);
    });

    test('maps a 1007 close on a bad setup frame to a terminal error, without echoing the reason', async () => {
      const harness = openSession({ skipSetup: true });

      harness.upstream.emit('close', {
        code: 1007,
        reason: 'Invalid JSON payload received. Unknown name "transcription_config" at \'setup.generation_config\'',
      });
      await drainPendingDeductions(2000);

      const errors = harness.client.messagesOfType('error');
      expect(errors).toEqual([
        { type: 'error', message: 'Transcription service rejected the session setup' },
      ]);
      expect(JSON.stringify(errors)).not.toContain('generation_config');
      expect(harness.client.closes).toEqual([{ code: 1011, reason: 'Upstream error' }]);
    });

    test('a clean close is not an error', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(5)));

      harness.upstream.emit('close', { code: 1000, reason: '' });
      await drainPendingDeductions(2000);

      expect(harness.client.messagesOfType('error')).toHaveLength(0);
      expect(harness.client.closes).toEqual([{ code: 1000, reason: 'Session ended' }]);
    });

    test('an unexpected close code stays transient so the client may reconnect', () => {
      expect(parseGeminiLiveClose(1006, '')).toEqual([
        { kind: 'error', message: 'Transcription service error', terminal: false },
      ]);
    });

    test('reports an upstream socket error to the client', async () => {
      const harness = openSession();

      harness.upstream.emit('error', {});

      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Transcription service error' },
      ]);
      await harness.endSession();
    });
  });

  describe('billing', () => {
    test('charges the live model rate for the audio forwarded, with the transcript length', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(20)));
      harness.upstream.deliver({ serverContent: { inputTranscription: { text: 'a'.repeat(40) } } });

      const pending = await harness.endSession();
      const expected = computeGeminiTranscribeLiveCost(20, 40);

      expect(pending).toBe(1);
      expect(harness.client.messagesOfType('session_complete')).toEqual([
        { type: 'session_complete', duration_seconds: 20, credits_used: creditsForCost(expected) },
      ]);
      expect(licenseCharges).toHaveLength(1);
      expect(licenseCharges[0]!.amount).toBe(creditsForCost(expected));
      expect(licenseCharges[0]!.metadata).toMatchObject({
        audio_duration_seconds: 20,
        endpoint: '/ws/streaming-gemini-transcribe',
        stt_provider: 'gemini-3.5-transcribe-live',
        language: 'auto',
      });
    });

    test('interim text never reaches the bill — only committed finals do', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(10)));
      harness.upstream.deliver({ serverContent: { interimInputTranscription: { text: 'x'.repeat(4000) } } });

      await harness.endSession();

      expect(licenseCharges[0]!.amount).toBe(creditsForCost(computeGeminiTranscribeLiveCost(10, 0)));
    });

    test('records the explicit language on the charge', async () => {
      const harness = openSession({ query: '&language=de' });
      harness.events.onMessage(binaryMessage(audioFrame(10)));

      await harness.endSession();

      expect(licenseCharges[0]!.metadata.language).toBe('de');
    });

    test('cuts the session off at the balance seen at auth', async () => {
      // 4.6 credits is the floor; give exactly that and stream past it.
      const harness = openSession({ credits: 4.6 });

      for (let second = 0; second < 30; second += 1) {
        harness.events.onMessage(binaryMessage(audioFrame(1)));
      }
      expect(harness.client.messagesOfType('error')).toEqual([
        { type: 'error', message: 'Credit balance exhausted' },
      ]);
      expect(harness.upstream.closes).toEqual([{ code: 1000, reason: 'Credits exhausted' }]);

      harness.events.onMessage(binaryMessage(audioFrame(5)));
      expect(harness.upstream.audioFramesForwarded).toBe(30);
      await harness.endSession();
    });

    test('bills once when the upstream close and the client close both land', async () => {
      const harness = openSession();
      harness.events.onMessage(binaryMessage(audioFrame(5)));

      harness.upstream.emit('close', { code: 1000, reason: '' });
      await harness.endSession();
      await harness.events.onError();
      await drainPendingDeductions(2000);

      expect(licenseCharges).toHaveLength(1);
      expect(harness.client.messagesOfType('session_complete')).toHaveLength(1);
    });
  });
});
