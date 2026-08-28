// WEBSOCKET STREAMING ROUTE
// GET /ws/streaming-deepgram - Deepgram Live proxy
//
// The session lifecycle lives in `ws-streaming-shared.ts`; this file is the
// Deepgram vendor adapter plus the route's exports. The path is
// `/ws/streaming-{sttProvider}` with `sttProvider: 'deepgram'` — the path every
// installed client has hard-coded. Do not rename it.

import type { Context } from 'hono';
import { upgradeWebSocket } from 'hono/bun';
import { computeDeepgramTranscriptionCost } from '../lib/cost-calculator';
import { isExplicitLanguage, splitVocabularyTerms } from '../providers/utils';
import {
  MAX_UPSTREAM_BUFFERED_BYTES,
  createStreamingEventsFor,
  durationSecondsForLinear16AudioBytes,
  makeStreamingPreflight,
  minimumCreditsFor,
  type StreamingSession,
  type StreamingVendor,
  type UpstreamEvent,
} from './ws-streaming-shared';

// Re-exported because the suite (and the macOS strategy docs) reach for it here.
export { durationSecondsForLinear16AudioBytes };

// Deepgram `keyterm` limits — same values the REST adapter applies.
const MAX_KEYTERMS = 100;
const MAX_KEYTERM_CHARS = 50;

interface DeepgramLiveResponse {
  type: string;
  duration?: number;
  is_final?: boolean;
  speech_final?: boolean;
  channel?: {
    alternatives?: Array<{ transcript?: string }>;
  };
}

function isDeepgramLiveResponse(value: unknown): value is DeepgramLiveResponse {
  return typeof value === 'object' && value !== null
    && typeof (value as { type?: unknown }).type === 'string';
}

function buildDeepgramUrl(language?: string, vocabulary?: string): string {
  const params = new URLSearchParams({
    model: 'nova-3',
    smart_format: 'true',
    interim_results: 'true',
    punctuate: 'true',
    endpointing: '300',
    encoding: 'linear16',
    sample_rate: '16000',
    channels: '1',
    mip_opt_out: 'true',
  });

  const normalizedLanguage = language?.toLowerCase();
  if (isExplicitLanguage(normalizedLanguage)) {
    params.set('language', normalizedLanguage);
    if (vocabulary) {
      // Nova-3 `keyterm` takes ONE repeated query value per term — `keyterm=a&
      // keyterm=b` — and does NOT support a `:boost` suffix (that's the legacy
      // nova-2 `keywords` syntax). A single comma-joined `a:1.5,b:1.5` value
      // boosts one literal phrase and does nothing. Split/clean to match the
      // REST adapter's convertToKeyterms, then append each term individually.
      const terms = splitVocabularyTerms(vocabulary, {
        maxTerms: MAX_KEYTERMS,
        maxTermChars: MAX_KEYTERM_CHARS,
      });
      for (const term of terms) {
        params.append('keyterm', term);
      }
    }
  }

  return `wss://api.deepgram.com/v1/listen?${params.toString()}`;
}

/**
 * Deepgram Live.
 *
 * Raw linear16 in, JSON `Results` frames out, and no setup handshake: the socket
 * accepts audio the moment it opens, and Deepgram closes it itself when the
 * stream ends — so there are no open frames, no stop frames, and `ready` goes
 * out on `open`.
 *
 * Its interim results are cumulative WITHIN the current segment and its finals
 * are per-segment deltas, which is exactly the client contract documented in
 * `ws-streaming-shared.ts`, so the finality flags pass straight through.
 */
export const DEEPGRAM_VENDOR: StreamingVendor = {
  id: 'deepgram',
  label: 'Deepgram',
  billingProvider: 'deepgram-nova3-live',
  apiKey: () => process.env.DEEPGRAM_API_KEY || '',
  buildUpstreamUrl: (session: StreamingSession) =>
    buildDeepgramUrl(session.language, session.vocabulary),
  upstreamProtocols: (session: StreamingSession) => ['token', session.apiKey],
  readyOnOpen: true,
  encodeAudio: (pcm: ArrayBuffer) => pcm,
  maxUpstreamBufferedBytes: MAX_UPSTREAM_BUFFERED_BYTES,
  parseUpstream(raw: string): UpstreamEvent[] {
    const parsed: unknown = JSON.parse(raw);
    if (!isDeepgramLiveResponse(parsed) || parsed.type !== 'Results') return [];
    return [{
      kind: 'transcript',
      text: parsed.channel?.alternatives?.[0]?.transcript || '',
      isFinal: parsed.is_final ?? false,
      speechFinal: parsed.speech_final ?? false,
    }];
  },
  costForSeconds: (seconds: number) => computeDeepgramTranscriptionCost(seconds),
};

/** Minimum balance required to open a streaming session (~30s of Nova-3 audio). */
export function minimumStreamingCredits(): number {
  return minimumCreditsFor(DEEPGRAM_VENDOR);
}

export const wsStreamingPreflight = makeStreamingPreflight(minimumStreamingCredits);

// Exported so the socket lifecycle (audio caps, upstream backpressure, the
// mid-session credit cutoff, end-of-session billing) is unit-testable without
// standing up a real WebSocket upgrade. `wsStreamingRoute` below is the only
// production caller.
export function createStreamingEvents(c: Context) {
  return createStreamingEventsFor(DEEPGRAM_VENDOR, c);
}

export const wsStreamingRoute = upgradeWebSocket(createStreamingEvents);
