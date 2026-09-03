// WEBSOCKET STREAMING ROUTE
// GET /ws/streaming-gemini-transcribe - Gemini 3.5 Transcribe Live proxy
//
// Second vendor behind the shell in `ws-streaming-shared.ts`. The path is
// `/ws/streaming-{sttProvider}` with `sttProvider: 'gemini-transcribe'` (the
// `geminiTranscribe` row in `shared-app-classification/cloud-stt-catalog.json`),
// so the clients derive it from the selected catalog entry. It is NOT
// `/ws/streaming-gemini`: `gemini` is a different provider id entirely (the
// `:generateContent` multimodal LLMs — TRAP 1).
//
//   wss://generativelanguage.googleapis.com/ws/
//     google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=<key>
//
//   → {"setup":{"model":"models/gemini-3.5-transcribe-live",
//               "input_audio_transcription":{"language_codes":["en-US"],
//                                            "custom_vocabulary":["HyperWhisper"]}}}
//   ← {"setupComplete":{}}
//   → {"realtime_input":{"audio":{"data":"<b64 pcm16 16k mono LE>",
//                                 "mime_type":"audio/pcm;rate=16000"}}}
//   ← {"serverContent":{"interimInputTranscription":{"text":"…"}}}   (repeatedly)
//   ← {"serverContent":{"inputTranscription":{"text":"…"}}}          (the turn's final)
//   ← {"serverContent":{"generationComplete":true}}
//   → {"realtime_input":{"audio_stream_end":true}}
//
// TRAP 3 — the live transcription config lives at `setup.input_audio_transcription`.
// The pre-recorded location (`setup.generation_config.transcription_config`, which
// is what `providers/gemini-transcribe.ts` correctly sends over HTTP) makes the
// server close the socket with code 1007: verified live, reason `Invalid JSON
// payload received. Unknown name "transcription_config" at 'setup.generation_config'`.
// One config object, two paths. `shared-conformance/live-frame-vectors.json` pins
// the frame shapes for every platform including this one; the vector test in
// `ws-streaming-gemini-transcribe.test.ts` reads that file rather than restating it.
//
// TRAP 2 still applies (`custom_vocabulary` cannot travel with `diarization_mode`
// or `timestamp_granularities`) and is unreachable here for the same reason it is
// on the HTTP path: this route never asks for the extras.
//
// Data retention: Google paid tier, no training on API traffic, and no
// per-request opt-out flag on the live socket either — the same posture as
// `providers/gemini-transcribe.ts`, same key, same project.
// See `mintlify-help/data-privacy.mdx`.

import type { Context } from 'hono';
import { upgradeWebSocket } from 'hono/bun';
import { computeGeminiTranscribeLiveCost } from '../lib/cost-calculator';
import {
  GEMINI_TRANSCRIBE_LIVE_MODEL,
  buildTranscriptionConfig,
  toVocabularyTerms,
} from '../providers/gemini-transcribe';
import { isRecord } from '../lib/utils';
import {
  MAX_UPSTREAM_BUFFERED_BYTES,
  createStreamingEventsFor,
  makeStreamingPreflight,
  minimumCreditsFor,
  type StreamingSession,
  type StreamingVendor,
  type UpstreamEvent,
} from './ws-streaming-shared';

const LIVE_WS_ROOT =
  'wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent';

/** Mime type of the raw PCM the clients already send us. */
const LIVE_AUDIO_MIME = 'audio/pcm;rate=16000';

// Base64 inflates the payload by 4/3, so the shared 1 MB raw ceiling has to be
// measured against ~1.33 MB of buffered JSON for the congestion point to stay
// at the same amount of AUDIO as Deepgram's.
const MAX_UPSTREAM_BUFFERED_JSON_BYTES = Math.ceil(MAX_UPSTREAM_BUFFERED_BYTES * 4 / 3);

// How long the socket stays open after the client's `stop`, waiting for the
// trailing final transcript. Verified live: the flush arrives ~0.5 s after
// `audio_stream_end`, and the server then leaves the socket open indefinitely
// (it did not close for 54 s), so this backstop is what ends the session.
const STOP_GRACE_MS = 5_000;

export function buildLiveWsUrl(apiKey: string): string {
  return `${LIVE_WS_ROOT}?key=${encodeURIComponent(apiKey)}`;
}

/**
 * The `setup` frame. Shares {@link buildTranscriptionConfig} with the HTTP
 * adapter so the two paths cannot disagree about what a language or a
 * vocabulary term looks like — only the *position* of the object differs
 * (TRAP 3).
 */
export function buildSetupFrame(language: string | undefined, vocabulary: string | undefined): string {
  const terms = vocabulary ? toVocabularyTerms(vocabulary) : [];
  return JSON.stringify({
    setup: {
      model: `models/${GEMINI_TRANSCRIBE_LIVE_MODEL}`,
      // NOT `generation_config.transcription_config` — see TRAP 3 above.
      input_audio_transcription: buildTranscriptionConfig(language, terms),
    },
  });
}

export function buildAudioFrame(pcm: ArrayBuffer): string {
  return JSON.stringify({
    realtime_input: {
      audio: {
        data: Buffer.from(pcm).toString('base64'),
        mime_type: LIVE_AUDIO_MIME,
      },
    },
  });
}

export function buildAudioStreamEndFrame(): string {
  return JSON.stringify({ realtime_input: { audio_stream_end: true } });
}

function readText(container: unknown, key: string): string | undefined {
  if (!isRecord(container)) return undefined;
  const node = container[key];
  if (!isRecord(node)) return undefined;
  return typeof node.text === 'string' ? node.text : undefined;
}

/**
 * Decode one server frame.
 *
 * PARTIAL / FINAL SEMANTICS — verified live, twice, over a two-utterance session:
 *
 * - `interimInputTranscription` repeats and is CUMULATIVE **within the current
 *   turn**, and it resets to the start of the next turn once that turn is
 *   finalized ("Hello." → "Hello, this is a" → … → final → "Hello." again).
 *   That is a replacement preview, i.e. exactly `is_final: false`.
 * - `inputTranscription` is the turn's committed text and covers only that
 *   turn's audio, not the session's ("utterance 2"'s final held utterance 2's
 *   words alone). That is a delta, i.e. exactly `is_final: true`.
 *
 * So the client contract in `ws-streaming-shared.ts` is satisfied by a straight
 * mapping and NO prefix-diffing is needed here. Diffing would have been the bug:
 * subtracting the committed prefix from an interim that already restarted at the
 * turn boundary truncates the preview. (The `.NET` `GrokLiveProtocol.Delta()`
 * exists because Grok's frames are cumulative across the whole *session* — a
 * different shape, do not copy it here.)
 *
 * `speech_final` mirrors `is_final`: there is no separate endpointing signal on
 * this API, and a final IS the end of speech for that turn.
 */
export function parseGeminiLiveFrame(raw: string): UpstreamEvent[] {
  const json: unknown = JSON.parse(raw);
  if (!isRecord(json)) return [];

  if (json.setupComplete !== undefined) {
    return [{ kind: 'ready' }];
  }

  // A live error is delivered EITHER as an `error` payload or, far more often,
  // as a 1007 close with the reason on the close frame (see the close handler
  // in `wsStreamingGeminiTranscribeRoute`'s vendor below). Both are terminal.
  if (isRecord(json.error)) {
    const message = typeof json.error.message === 'string' ? json.error.message : 'Transcription service error';
    return [{ kind: 'error', message, terminal: true }];
  }
  if (json.goAway !== undefined) {
    return [{ kind: 'error', message: 'Transcription service is going away', terminal: false }];
  }

  const content = json.serverContent;
  if (!isRecord(content)) return [];

  const complete = content.generationComplete === true || content.turnComplete === true;

  // FINAL FIRST. A frame that carries both fields is the turn closing, and the
  // committed text is the one the client must not lose: emitting the preview
  // instead leaves the turn permanently uncommitted, because the interim
  // restarts at the next turn boundary and no later frame repeats these words.
  // The three native heads (`GeminiLiveProtocol.cs`, the macOS strategy and the
  // Rust core's `gemini_transcribe.rs`) all read final-first for this reason —
  // an order this backend has to match, not choose.
  //
  // AND THE COMPLETION RIDES ALONG. Google answers the stop's
  // `audio_stream_end` with ONE frame carrying the last committed segment and
  // `generationComplete` together, so an early return on the final swallowed
  // the only completion this session would ever get and the socket was closed
  // by `armStopGrace`'s backstop instead — `session_complete` and its credit
  // report reached the client ~5 s late on every cloud-Gemini dictation. Both
  // halves go out, final first: `handleUpstreamEvent`'s `complete` arm is
  // already gated on `stopRequested`, so a mid-dictation turn boundary is a
  // no-op there and only the post-stop frame ends the session. This matches
  // `gemini_transcribe.rs`' `LiveServerMessage::FinalTranscriptAndComplete`
  // and the `finalTranscriptAndComplete` conformance vector.
  const final = readText(content, 'inputTranscription');
  if (final !== undefined) {
    const events: UpstreamEvent[] = [
      { kind: 'transcript', text: final, isFinal: true, speechFinal: true },
    ];
    if (complete) events.push({ kind: 'complete' });
    return events;
  }

  // An interim carries no committed text, so there is no half worth pairing a
  // completion with. The preview goes out alone and a completion riding on it
  // is dropped — deliberately the same answer `gemini_transcribe.rs` gives,
  // because the two decoders disagreeing about one frame shape is the fault
  // this block exists to remove.
  const interim = readText(content, 'interimInputTranscription');
  if (interim !== undefined) {
    return [{ kind: 'transcript', text: interim, isFinal: false, speechFinal: false }];
  }

  if (complete) {
    return [{ kind: 'complete' }];
  }

  // `{"serverContent":{}}` keep-alives, usage updates, anything unmodelled.
  return [];
}

/** Close reasons that mean the credential, not the frame, is the problem. */
const KEY_REJECTED = /api key not valid|api[_ ]key[_ ]invalid|unauthenticated|permission denied/i;

/**
 * Map the upstream CLOSE frame.
 *
 * This vendor reports setup faults ONLY by closing — verified live, twice: both
 * an invalid API key and the TRAP 3 config path close with **1007** and put the
 * explanation on the close frame, having sent no error frame at all. Without
 * this the client would see a 0-second `session_complete` and never learn why.
 *
 * The upstream reason is logged, never echoed: it is a Google protobuf
 * diagnostic, and the house rule is that clients get our wording.
 */
export function parseGeminiLiveClose(code: number, reason: string): UpstreamEvent[] {
  // 1000 = normal, 1005 = no status (what a clean end-of-session looks like).
  if (code === 1000 || code === 1005) return [];

  if (KEY_REJECTED.test(reason)) {
    // Wording chosen so `StreamingProviderErrorPolicy` on macOS matches its
    // `api key not valid` marker and treats the session as terminal instead of
    // reconnecting into the same rejection.
    return [{
      kind: 'error',
      message: 'Transcription service rejected the credentials: API key not valid',
      terminal: true,
    }];
  }

  if (code === 1007) {
    // A malformed setup frame is OUR bug and identical for every user, so a
    // reconnect is a retry storm. Terminal.
    return [{ kind: 'error', message: 'Transcription service rejected the session setup', terminal: true }];
  }

  return [{ kind: 'error', message: 'Transcription service error', terminal: false }];
}

/**
 * Gemini 3.5 Transcribe Live.
 *
 * Unlike Deepgram this vendor needs a setup handshake before it will take audio,
 * and it never closes the socket by itself — hence `readyOnOpen: false`,
 * `openFrames`, `stopFrames` and the stop grace period.
 */
export const GEMINI_TRANSCRIBE_VENDOR: StreamingVendor = {
  id: 'gemini-transcribe',
  label: 'Gemini',
  billingProvider: GEMINI_TRANSCRIBE_LIVE_MODEL,
  // No new secret: the same key the HTTP adapter and providers/gemini.ts use.
  apiKey: () => process.env.GEMINI_API_KEY || process.env.GOOGLE_GEMINI_API_KEY || '',
  buildUpstreamUrl: (session: StreamingSession) => buildLiveWsUrl(session.apiKey),
  // The live API authenticates with `?key=`; there is no subprotocol form.
  readyOnOpen: false,
  openFrames: (session: StreamingSession) => [buildSetupFrame(session.language, session.vocabulary)],
  encodeAudio: buildAudioFrame,
  maxUpstreamBufferedBytes: MAX_UPSTREAM_BUFFERED_JSON_BYTES,
  parseUpstream: parseGeminiLiveFrame,
  parseUpstreamClose: parseGeminiLiveClose,
  stopFrames: () => [buildAudioStreamEndFrame()],
  stopGraceMs: STOP_GRACE_MS,
  costForSeconds: (seconds: number, transcriptChars?: number) =>
    computeGeminiTranscribeLiveCost(seconds, transcriptChars),
};

/**
 * Minimum balance required to open a session — the same 30 seconds of audio
 * Deepgram requires, priced at the live model's rate. ~4.6 credits against
 * Deepgram's 2.8, because Gemini live is ~1.7x the cost per minute.
 */
export function minimumGeminiTranscribeStreamingCredits(): number {
  return minimumCreditsFor(GEMINI_TRANSCRIBE_VENDOR);
}

export const wsStreamingGeminiTranscribePreflight = makeStreamingPreflight(
  minimumGeminiTranscribeStreamingCredits,
);

/** See the note on `createStreamingEvents` in `ws-streaming-deepgram.ts`. */
export function createGeminiTranscribeStreamingEvents(c: Context) {
  return createStreamingEventsFor(GEMINI_TRANSCRIBE_VENDOR, c);
}

export const wsStreamingGeminiTranscribeRoute = upgradeWebSocket(
  createGeminiTranscribeStreamingEvents,
);
