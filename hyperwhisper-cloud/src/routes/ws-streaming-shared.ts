// WEBSOCKET STREAMING — VENDOR-NEUTRAL SHELL
//
// Everything the live-transcription proxy does that is NOT specific to one
// upstream vendor: the upgrade preflight (IP block list → auth → credit floor),
// the client-facing message protocol, the inbound audio caps, upstream
// backpressure, the mid-session credit cutoff, end-of-session billing, and the
// Fly idle-timeout ping.
//
// A vendor supplies the rest through {@link StreamingVendor}: which socket to
// dial, what to say on it, how to encode a PCM chunk, how to read a server
// frame, and what a second of its audio costs.
//
// ROUTE NAMING IS LOAD-BEARING. The path is always `/ws/streaming-{vendor.id}`,
// where `id` is the `sttProvider` value from `cloud-stt-catalog.json`. Clients
// derive the path from the selected catalog entry rather than carrying a
// hand-written table on three platforms, and `id: 'deepgram'` reproduces the
// long-installed `/ws/streaming-deepgram` byte for byte. Never rename a route
// away from that derivation.
//
// CLIENT PROTOCOL (server → client) — identical for every vendor, because the
// native strategies (`HyperWhisperCloudStrategy.swift` /
// `HyperWhisperCloudStreamingStrategy.cs`) decode one shape:
//   {"type":"ready","sessionId":"…"}
//   {"type":"transcript","text":"…","is_final":bool,"speech_final":bool}
//   {"type":"session_complete","duration_seconds":N,"credits_used":N}
//   {"type":"error","message":"…"}
//   {"type":"ping"}                       (keep-alive; the client answers "pong")
//
// TRANSCRIPT SEMANTICS the vendor adapter must honour — the clients depend on
// them and getting it wrong duplicates or truncates dictated text:
//   is_final = false → a REPLACEMENT preview of the segment in progress. The
//     client overwrites whatever preview it is showing. Cumulative within the
//     current segment is correct; cumulative across the whole session is NOT.
//   is_final = true  → a DELTA. The client APPENDS it to the accumulated
//     transcript and types it. It must contain only text not already committed
//     by an earlier final.
// (`RecordingTranscriptionFlow+Streaming.swift` — "Streaming final delta
// received" — appends, and composes the preview as committed + interim.)

import type { Context, Next } from 'hono';
import type { WSMessageReceive } from 'hono/ws';
import { generateRequestId, getClientIP } from '../lib/request-id';
import { creditsForCost } from '../lib/cost-calculator';
import { validateAuth, type AuthContext } from '../middleware/auth';
import { deductCredits, validateCredits } from '../middleware/credits';
import { isIPBlocked } from '../lib/redis';
import { isRecord } from '../lib/utils';

// ---------------------------------------------------------------------------
// Client-facing protocol
// ---------------------------------------------------------------------------

interface ReadyMessage {
  type: 'ready';
  sessionId: string;
}

interface TranscriptMessage {
  type: 'transcript';
  text: string;
  is_final: boolean;
  speech_final: boolean;
}

interface SessionCompleteMessage {
  type: 'session_complete';
  duration_seconds: number;
  credits_used: number;
}

interface ErrorMessage {
  type: 'error';
  message: string;
}

export type ServerMessage = ReadyMessage | TranscriptMessage | SessionCompleteMessage | ErrorMessage;

export interface WSContext {
  readyState: number;
  send(data: string | ArrayBuffer | Uint8Array): void;
  close(code?: number, reason?: string): void;
}

export function sendToClient(socket: WSContext, message: ServerMessage): void {
  if (socket.readyState === 1) {
    socket.send(JSON.stringify(message));
  }
}

// ---------------------------------------------------------------------------
// Audio accounting
// ---------------------------------------------------------------------------

const STREAMING_SAMPLE_RATE = 16000;
const STREAMING_CHANNELS = 1;
const LINEAR16_BYTES_PER_SAMPLE = 2;

/**
 * Billable duration is derived from the audio bytes we actually FORWARDED, not
 * from any duration the vendor reports: vendor result durations overlap across
 * interim and final frames and would double-count.
 */
export function durationSecondsForLinear16AudioBytes(byteLength: number): number {
  return byteLength / (STREAMING_SAMPLE_RATE * STREAMING_CHANNELS * LINEAR16_BYTES_PER_SAMPLE);
}

/** Minimum balance required to open a session, expressed as seconds of audio. */
export const STREAMING_MIN_BALANCE_SECONDS = 30;

// Inbound audio limits — guard the proxy against a misbehaving or malicious
// client that pushes binary far faster (or larger) than the natural 32 KB/s
// rate of 16 kHz mono linear16. Without these caps the client can grow the
// outbound socket's buffer unbounded until the Fly machine OOMs.
//
// A single audio frame above 1 MB (~32 s of audio) is abnormal for streaming.
export const MAX_AUDIO_MESSAGE_BYTES = 1 * 1024 * 1024;
// Cumulative per-session cap (~52 min of 16 kHz mono linear16), well above any
// real dictation session. Exceeding it closes the socket with 1009.
export const MAX_SESSION_AUDIO_BYTES = 100 * 1024 * 1024;
// If the upstream socket has more than this still buffered, the client is
// outrunning the vendor — drop the chunk instead of queueing more memory.
// A vendor that wraps PCM in base64 JSON measures ~1.33x the raw byte count, so
// it raises this to keep the *audio-equivalent* congestion point the same.
export const MAX_UPSTREAM_BUFFERED_BYTES = 1 * 1024 * 1024;

// ---------------------------------------------------------------------------
// Vendor adapter
// ---------------------------------------------------------------------------

/** Everything a vendor adapter is told about the session it is building for. */
export interface StreamingSession {
  readonly requestId: string;
  /** `language` query param, verbatim. `undefined` / `auto` mean auto-detect. */
  readonly language?: string;
  /** `vocabulary` query param, verbatim (comma/newline separated). */
  readonly vocabulary?: string;
  readonly apiKey: string;
}

/** One decoded upstream frame, normalized. */
export type UpstreamEvent =
  /** The vendor is ready for audio. Emitted at most once; sends `ready`. */
  | { kind: 'ready' }
  /** See the TRANSCRIPT SEMANTICS block at the top of this file. */
  | { kind: 'transcript'; text: string; isFinal: boolean; speechFinal: boolean }
  /**
   * The vendor finished the stream it was asked to finish. Ends the session
   * only when the client already asked to stop — vendors that emit this at
   * every turn boundary must not tear a live session down mid-dictation.
   */
  | { kind: 'complete' }
  /** A vendor-side fault. `terminal` closes the client socket with 1011. */
  | { kind: 'error'; message: string; terminal?: boolean };

export interface StreamingVendor {
  /**
   * The catalog `sttProvider` id. The route path is `/ws/streaming-{id}` — see
   * the note at the top of this file before changing it.
   */
  readonly id: string;
  /** Human label used in log lines and the "<x> API key not configured" error. */
  readonly label: string;
  /** `stt_provider` recorded on the credit deduction. */
  readonly billingProvider: string;
  /** Reads the API key from the environment at session-open time. */
  apiKey(): string;
  buildUpstreamUrl(session: StreamingSession): string;
  /** WebSocket subprotocols for the upstream handshake, if the vendor uses them. */
  upstreamProtocols?(session: StreamingSession): string[] | undefined;
  /**
   * `true` when the vendor accepts audio the moment the socket opens (`ready`
   * goes out on `open`). `false` when it needs a setup handshake first — then
   * `ready` waits for the adapter to emit `{ kind: 'ready' }` and audio that
   * arrives early is buffered rather than dropped.
   */
  readonly readyOnOpen: boolean;
  /** Frames to send as soon as the upstream socket opens (a setup frame). */
  openFrames?(session: StreamingSession): string[];
  /** Wrap one raw PCM chunk in whatever the vendor's socket accepts. */
  encodeAudio(pcm: ArrayBuffer): string | ArrayBuffer;
  /** Upstream `bufferedAmount` ceiling. */
  readonly maxUpstreamBufferedBytes: number;
  /** Decode one upstream frame. Throwing is treated as an unparseable frame. */
  parseUpstream(raw: string): UpstreamEvent[];
  /**
   * Decode the upstream CLOSE frame, for vendors that report a fault by closing
   * rather than by sending an error frame. Runs before the session is settled,
   * so anything it returns still reaches the client. The raw code and reason are
   * logged either way — do not echo an upstream reason string to the client.
   */
  parseUpstreamClose?(code: number, reason: string): UpstreamEvent[];
  /**
   * Frames that ask the vendor to flush and finish. Omitted (or empty) means
   * "just close the socket", which is what a vendor with no end-of-stream frame
   * wants. When present, the socket stays open for `stopGraceMs` so the trailing
   * final transcript can still arrive.
   */
  stopFrames?(): string[];
  readonly stopGraceMs?: number;
  /**
   * Cost in USD of `seconds` of audio. `transcriptChars` is passed only at
   * end-of-session, where the full transcript is known; the mid-session cutoff
   * omits it deliberately so the safety valve never under-estimates on a
   * fast talker whose finals have not landed yet.
   */
  costForSeconds(seconds: number, transcriptChars?: number): number;
}

/** `/ws/streaming-{id}` — the one place the route path is derived. */
export function routePathFor(vendor: Pick<StreamingVendor, 'id'>): string {
  return `/ws/streaming-${vendor.id}`;
}

/** Credits needed to open a session with this vendor (30 s of its audio). */
export function minimumCreditsFor(vendor: Pick<StreamingVendor, 'costForSeconds'>): number {
  return creditsForCost(vendor.costForSeconds(STREAMING_MIN_BALANCE_SECONDS));
}

// ---------------------------------------------------------------------------
// Preflight
// ---------------------------------------------------------------------------

declare module 'hono' {
  interface ContextVariableMap {
    wsAuth: AuthContext;
    wsClientIP: string;
  }
}

/**
 * The upgrade gate, parameterised by the balance a session costs to open.
 *
 * Order matters and is asserted by the Deepgram suite: upgrade header → IP block
 * list → key presence → licence validity → credit floor. The IP gate sits ahead
 * of every licence lookup so a blocked address costs no upstream work.
 */
export function makeStreamingPreflight(minimumCredits: () => number) {
  return async function wsStreamingPreflight(c: Context, next: Next) {
    const upgradeHeader = c.req.header('Upgrade');
    if (!upgradeHeader || upgradeHeader.toLowerCase() !== 'websocket') {
      return c.text('Expected WebSocket upgrade', 426);
    }

    const clientIP = getClientIP(c);
    if (await isIPBlocked(clientIP)) {
      return c.text('Access denied', 403);
    }

    const url = new URL(c.req.url);
    // `account_key` is the canonical param; `license_key` is the legacy alias that
    // installed native apps still send, so we accept either.
    const licenseKey =
      url.searchParams.get('account_key') ||
      url.searchParams.get('license_key') ||
      undefined;

    if (!licenseKey) {
      return c.text('Missing account_key', 401);
    }

    const authResult = await validateAuth({ licenseKey });
    if (!authResult.ok) {
      return c.text('Unauthorized', 401);
    }

    const creditCheck = await validateCredits(authResult.value, minimumCredits(), clientIP);
    if (!creditCheck.ok) {
      return creditCheck.response;
    }

    c.set('wsAuth', authResult.value);
    c.set('wsClientIP', clientIP);

    return next();
  };
}

// ---------------------------------------------------------------------------
// Socket lifecycle
// ---------------------------------------------------------------------------

function decodeUpstreamFrame(raw: unknown): string {
  // `event.data` is typed `any` by the WebSocket lib and a vendor can deliver a
  // frame as a string or as binary. Coerce explicitly rather than asserting.
  if (typeof raw === 'string') return raw;
  if (raw instanceof ArrayBuffer || ArrayBuffer.isView(raw)) return new TextDecoder().decode(raw);
  return String(raw);
}

/**
 * Build the hono WebSocket event handlers for one session.
 *
 * Exported per vendor (see `ws-streaming-deepgram.ts`) so the socket lifecycle —
 * audio caps, upstream backpressure, the mid-session credit cutoff,
 * end-of-session billing — is unit-testable without standing up a real
 * WebSocket upgrade.
 */
export function createStreamingEventsFor(vendor: StreamingVendor, c: Context) {
  const requestId = generateRequestId();
  const auth = c.get('wsAuth');
  const clientIP = c.get('wsClientIP');
  const url = new URL(c.req.url);
  const language = url.searchParams.get('language') || undefined;
  const vocabulary = url.searchParams.get('vocabulary') || undefined;
  const apiKey = vendor.apiKey();
  const endpoint = routePathFor(vendor);

  const session: StreamingSession = { requestId, language, vocabulary, apiKey };

  let totalDurationSeconds = 0;
  let bytesReceived = 0;
  let transcriptChars = 0;
  let upstreamWs: WebSocket | null = null;
  let sessionEnded = false;
  let clientSocket: WSContext | null = null;
  let pingInterval: ReturnType<typeof setInterval> | null = null;
  // `true` once the vendor will accept audio. A vendor with a setup handshake
  // holds audio in `pendingAudio` until then.
  let upstreamAcceptsAudio = vendor.readyOnOpen;
  let readySent = false;
  let stopRequested = false;
  let stopTimer: ReturnType<typeof setTimeout> | null = null;
  /** Set when a terminal upstream fault should close the client with 1011. */
  let clientCloseCode: number | null = null;
  const pendingAudio: ArrayBuffer[] = [];
  let pendingAudioBytes = 0;

  const upstreamUrl = vendor.buildUpstreamUrl(session);
  const protocols = vendor.upstreamProtocols?.(session);

  function log(event: string, details: Record<string, unknown> = {}): void {
    console.log(`ws_streaming.${event}`, {
      provider: vendor.id,
      requestId,
      endpoint,
      ...details,
    });
  }

  async function endSession(): Promise<void> {
    if (sessionEnded) return;
    sessionEnded = true;

    if (pingInterval) {
      clearInterval(pingInterval);
      pingInterval = null;
    }
    if (stopTimer) {
      clearTimeout(stopTimer);
      stopTimer = null;
    }

    const costUsd = vendor.costForSeconds(totalDurationSeconds, transcriptChars);
    const creditsUsed = creditsForCost(costUsd);

    if (clientSocket) {
      sendToClient(clientSocket, {
        type: 'session_complete',
        duration_seconds: totalDurationSeconds,
        credits_used: creditsUsed,
      });
    }

    log('session_end', {
      durationSeconds: totalDurationSeconds,
      transcriptChars,
      costUsd,
      creditsUsed,
    });

    if (creditsUsed > 0) {
      deductCredits(
        auth,
        costUsd,
        {
          audio_duration_seconds: totalDurationSeconds,
          transcription_cost_usd: costUsd,
          language: language || 'auto',
          endpoint,
          stt_provider: vendor.billingProvider,
        },
        clientIP
      ).catch(console.error);
    }
  }

  function closeUpstream(reason: string = 'Client disconnected'): void {
    // readyState 0 = CONNECTING, 1 = OPEN — close both so a client that
    // disconnects mid-handshake doesn't leave the upstream socket open until
    // the vendor's idle timeout. close() during CONNECTING aborts the handshake
    // once it completes; CLOSING/CLOSED need no action.
    if (upstreamWs && upstreamWs.readyState <= WebSocket.OPEN) {
      upstreamWs.close(1000, reason);
    }
  }

  /** Forward one already-vetted PCM chunk and meter it. */
  function forwardAudio(data: ArrayBuffer): void {
    if (!upstreamWs) return;
    upstreamWs.send(vendor.encodeAudio(data) as string & ArrayBuffer);
    totalDurationSeconds += durationSecondsForLinear16AudioBytes(data.byteLength);
  }

  /**
   * End the session once the running cost reaches the balance seen at auth, so a
   * low-balance user can't stream indefinitely on end-of-session billing.
   * Duration-only on purpose — see `costForSeconds`.
   */
  function enforceCreditCutoff(): boolean {
    const creditsUsed = creditsForCost(vendor.costForSeconds(totalDurationSeconds));
    if (creditsUsed < auth.credits) return false;
    if (clientSocket) {
      sendToClient(clientSocket, { type: 'error', message: 'Credit balance exhausted' });
    }
    log('credit_cutoff', { durationSeconds: totalDurationSeconds, creditsUsed });
    closeUpstream('Credits exhausted');
    return true;
  }

  function flushPendingAudio(): void {
    const queued = pendingAudio.splice(0, pendingAudio.length);
    pendingAudioBytes = 0;
    if (queued.length === 0) return;
    for (const chunk of queued) {
      if (!upstreamWs || upstreamWs.readyState !== WebSocket.OPEN) break;
      forwardAudio(chunk);
    }
    enforceCreditCutoff();
  }

  function handleUpstreamEvent(event: UpstreamEvent, ws: WSContext): void {
    switch (event.kind) {
      case 'ready': {
        upstreamAcceptsAudio = true;
        if (!readySent && ws.readyState === 1) {
          readySent = true;
          sendToClient(ws, { type: 'ready', sessionId: requestId });
          log('ready');
        }
        flushPendingAudio();
        return;
      }
      case 'transcript': {
        if (event.text || event.isFinal) {
          if (event.isFinal) transcriptChars += event.text.length;
          sendToClient(ws, {
            type: 'transcript',
            text: event.text,
            is_final: event.isFinal,
            speech_final: event.speechFinal,
          });
        }
        return;
      }
      case 'complete': {
        // Only terminal once the client asked to stop: vendors that emit this at
        // every turn boundary would otherwise cut a live dictation short.
        if (stopRequested) {
          closeUpstream('Session ended');
        }
        return;
      }
      case 'error': {
        log('upstream_error', { message: event.message, terminal: event.terminal === true });
        sendToClient(ws, { type: 'error', message: event.message });
        if (event.terminal) {
          // 1011 so .NET's `IStreamingProviderStrategy.IsTerminalCloseCode`
          // treats the close as terminal and does not reconnect into the same
          // fault. The client socket is closed by the upstream-close handler
          // AFTER `session_complete`, so the user is still told what they were
          // billed. (macOS has no close-code policy at all — it classifies on
          // the message text, which is why these messages carry the wording
          // `StreamingProviderErrorPolicy.terminalMarkers` recognises.)
          clientCloseCode = 1011;
          closeUpstream('Upstream rejected the session');
        }
        return;
      }
    }
  }

  return {
    onOpen: (_evt: Event, ws: WSContext) => {
      clientSocket = ws;

      if (!apiKey) {
        sendToClient(ws, { type: 'error', message: `${vendor.label} API key not configured` });
        ws.close(1011, 'Configuration error');
        return;
      }

      log('session_start', { language: language || 'auto', hasVocabulary: Boolean(vocabulary) });

      upstreamWs = protocols === undefined
        ? new WebSocket(upstreamUrl)
        : new WebSocket(upstreamUrl, protocols);

      upstreamWs.addEventListener('open', () => {
        // If the client already disconnected while we were still handshaking,
        // tear down the upstream socket instead of leaving it orphaned.
        if (ws.readyState !== 1) {
          closeUpstream();
          return;
        }
        for (const frame of vendor.openFrames?.(session) ?? []) {
          upstreamWs?.send(frame);
        }
        if (vendor.readyOnOpen) {
          handleUpstreamEvent({ kind: 'ready' }, ws);
        }
      });

      upstreamWs.addEventListener('message', (event) => {
        try {
          const text = decodeUpstreamFrame((event as MessageEvent).data);
          // Validate the parsed shape instead of asserting it — an unexpected
          // frame is ignored, not trusted.
          for (const decoded of vendor.parseUpstream(text)) {
            handleUpstreamEvent(decoded, ws);
          }
        } catch (error) {
          console.warn(`Failed to parse ${vendor.label} message`, error);
        }
      });

      upstreamWs.addEventListener('error', () => {
        sendToClient(ws, { type: 'error', message: 'Transcription service error' });
      });

      upstreamWs.addEventListener('close', async (event) => {
        const { code, reason } = (event ?? {}) as { code?: number; reason?: string };
        if (vendor.parseUpstreamClose) {
          log('upstream_close', { code: code ?? null, reason: reason || null });
          for (const decoded of vendor.parseUpstreamClose(code ?? 1000, reason ?? '')) {
            handleUpstreamEvent(decoded, ws);
          }
        }
        await endSession();
        if (ws.readyState === 1) {
          ws.close(clientCloseCode ?? 1000, clientCloseCode ? 'Upstream error' : 'Session ended');
        }
      });

      // Send ping every 30s to prevent Fly.io's 60s idle timeout from killing the connection
      pingInterval = setInterval(() => {
        if (clientSocket && clientSocket.readyState === 1) {
          clientSocket.send(JSON.stringify({ type: 'ping' }));
        }
      }, 30000);
    },
    onMessage: (event: MessageEvent<WSMessageReceive>) => {
      if (!upstreamWs || upstreamWs.readyState !== WebSocket.OPEN) {
        return;
      }

      const data = event.data;
      if (data instanceof ArrayBuffer) {
        // Count every inbound frame toward the session total first — even ones
        // we reject below — so a flood of oversized frames still trips the
        // cumulative cap and closes the socket instead of looping forever.
        bytesReceived += data.byteLength;

        // Bound total inbound volume so a flood can't OOM the worker. Checked
        // before the per-frame size guard so oversized frames also count here
        // and a sustained flood reliably closes the connection.
        if (bytesReceived > MAX_SESSION_AUDIO_BYTES) {
          if (clientSocket) {
            sendToClient(clientSocket, { type: 'error', message: 'Audio stream too large' });
            clientSocket.close(1009, 'Message too big');
          }
          return;
        }

        // Reject an abnormally large single frame outright — never forward it.
        if (data.byteLength > MAX_AUDIO_MESSAGE_BYTES) {
          if (clientSocket) {
            sendToClient(clientSocket, { type: 'error', message: 'Audio chunk too large' });
          }
          return;
        }

        // The vendor's setup handshake has not completed yet. Hold the chunk
        // rather than dropping it — the socket is open, so the client has no
        // way to know it is too early — but bound the queue the same way a
        // forwarded frame is bounded.
        if (!upstreamAcceptsAudio) {
          if (pendingAudioBytes + data.byteLength > MAX_AUDIO_MESSAGE_BYTES) {
            if (clientSocket) {
              sendToClient(clientSocket, { type: 'error', message: 'Transcription service busy, audio dropped' });
            }
            return;
          }
          pendingAudio.push(data);
          pendingAudioBytes += data.byteLength;
          return;
        }

        // Backpressure: if the upstream socket is already congested, drop this
        // chunk instead of queueing more memory into the outbound buffer.
        if (upstreamWs.bufferedAmount > vendor.maxUpstreamBufferedBytes) {
          if (clientSocket) {
            sendToClient(clientSocket, { type: 'error', message: 'Transcription service busy, audio dropped' });
          }
          return;
        }

        forwardAudio(data);
        enforceCreditCutoff();
        return;
      }

      if (typeof data === 'string') {
        try {
          // Client-controlled frame: read `type` through a guard rather than
          // asserting a shape onto it.
          const msg: unknown = JSON.parse(data);
          const msgType = isRecord(msg) ? msg.type : undefined;
          if (msgType === 'stop') {
            const frames = vendor.stopFrames?.() ?? [];
            if (frames.length === 0) {
              closeUpstream('Client requested stop');
              return;
            }
            // The vendor has an end-of-stream frame: send it and keep the socket
            // open long enough for the trailing final transcript to arrive,
            // with a hard backstop because these vendors do not close by
            // themselves once the stream ends.
            stopRequested = true;
            for (const frame of frames) upstreamWs.send(frame);
            if (!stopTimer) {
              stopTimer = setTimeout(() => {
                log('stop_grace_expired');
                closeUpstream('Client requested stop');
              }, vendor.stopGraceMs ?? 5000);
            }
            return;
          }
          if (msgType === 'pong') {
            // Client pong response — ignore
            return;
          }
        } catch {
          // ignore non-JSON text messages
        }
      }
    },
    onClose: async () => {
      await endSession();
      closeUpstream();
    },
    onError: async () => {
      if (clientSocket) {
        sendToClient(clientSocket, { type: 'error', message: 'WebSocket error' });
      }
      await endSession();
      closeUpstream();
    },
  };
}
