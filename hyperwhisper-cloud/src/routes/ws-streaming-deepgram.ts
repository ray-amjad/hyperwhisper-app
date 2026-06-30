// WEBSOCKET STREAMING ROUTE
// GET /ws/streaming-deepgram - Deepgram Live proxy

import type { Context, Next } from 'hono';
import { upgradeWebSocket } from 'hono/bun';
import { generateRequestId, getClientIP } from '../lib/request-id';
import { computeDeepgramTranscriptionCost, creditsForCost } from '../lib/cost-calculator';
import { validateAuth, type AuthContext } from '../middleware/auth';
import { deductCredits, validateCredits } from '../middleware/credits';
import { isIPBlocked } from '../lib/redis';

interface DeepgramLiveResponse {
  type: string;
  duration?: number;
  is_final?: boolean;
  speech_final?: boolean;
  channel?: {
    alternatives?: Array<{ transcript?: string }>;
  };
}

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

type ServerMessage = ReadyMessage | TranscriptMessage | SessionCompleteMessage | ErrorMessage;

const STREAMING_SAMPLE_RATE = 16000;
const STREAMING_CHANNELS = 1;
const LINEAR16_BYTES_PER_SAMPLE = 2;

// Minimum balance required to open a streaming session (~30s of Deepgram Nova-3 audio).
const STREAMING_MIN_BALANCE_SECONDS = 30;

export function minimumStreamingCredits(): number {
  return creditsForCost(computeDeepgramTranscriptionCost(STREAMING_MIN_BALANCE_SECONDS));
}

// Inbound audio limits — guard the Deepgram proxy against a misbehaving or
// malicious client that pushes binary far faster (or larger) than the natural
// 32 KB/s rate of 16 kHz mono linear16. Without these caps the client can grow
// the outbound socket's buffer unbounded until the Fly machine OOMs.
//
// A single audio frame above 1 MB (~32 s of audio) is abnormal for streaming.
const MAX_AUDIO_MESSAGE_BYTES = 1 * 1024 * 1024;
// Cumulative per-session cap (~52 min of 16 kHz mono linear16), well above any
// real dictation session. Exceeding it closes the socket with 1009.
const MAX_SESSION_AUDIO_BYTES = 100 * 1024 * 1024;
// If the upstream socket has more than this still buffered, the client is
// outrunning Deepgram — drop the chunk instead of queueing more memory.
const MAX_DEEPGRAM_BUFFERED_BYTES = 1 * 1024 * 1024;

interface WSContext {
  readyState: number;
  send(data: string | ArrayBuffer | Uint8Array): void;
  close(code?: number, reason?: string): void;
}

declare module 'hono' {
  interface ContextVariableMap {
    wsAuth: AuthContext;
    wsClientIP: string;
  }
}

function sendToClient(socket: WSContext, message: ServerMessage): void {
  if (socket.readyState === 1) {
    socket.send(JSON.stringify(message));
  }
}

export function durationSecondsForLinear16AudioBytes(byteLength: number): number {
  return byteLength / (STREAMING_SAMPLE_RATE * STREAMING_CHANNELS * LINEAR16_BYTES_PER_SAMPLE);
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
  if (normalizedLanguage && normalizedLanguage !== 'auto') {
    params.set('language', normalizedLanguage);
    if (vocabulary) {
      // Nova-3 `keyterm` takes ONE repeated query value per term — `keyterm=a&
      // keyterm=b` — and does NOT support a `:boost` suffix (that's the legacy
      // nova-2 `keywords` syntax). A single comma-joined `a:1.5,b:1.5` value
      // boosts one literal phrase and does nothing. Split/clean to match the
      // REST adapter's convertToKeyterms, then append each term individually.
      const terms = vocabulary
        .split(/[,\n;]+/)
        .map(t => t.trim().replace(/^[-*]\s*/, ''))
        .filter(t => t.length > 0 && t.length <= 50)
        .slice(0, 100);
      for (const term of terms) {
        params.append('keyterm', term);
      }
    }
  }

  return `wss://api.deepgram.com/v1/listen?${params.toString()}`;
}

export async function wsStreamingPreflight(c: Context, next: Next) {
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

  const creditCheck = await validateCredits(authResult.value, minimumStreamingCredits(), clientIP);
  if (!creditCheck.ok) {
    return creditCheck.response;
  }

  c.set('wsAuth', authResult.value);
  c.set('wsClientIP', clientIP);

  return next();
}

export const wsStreamingRoute = upgradeWebSocket((c) => {
  const requestId = generateRequestId();
  const auth = c.get('wsAuth');
  const clientIP = c.get('wsClientIP');
  const url = new URL(c.req.url);
  const language = url.searchParams.get('language') || undefined;
  const vocabulary = url.searchParams.get('vocabulary') || undefined;
  const apiKey = process.env.DEEPGRAM_API_KEY || '';

  let totalDurationSeconds = 0;
  let bytesReceived = 0;
  let deepgramWs: WebSocket | null = null;
  let sessionEnded = false;
  let clientSocket: WSContext | null = null;
  let pingInterval: ReturnType<typeof setInterval> | null = null;

  const dgUrl = buildDeepgramUrl(language, vocabulary);

  async function endSession(): Promise<void> {
    if (sessionEnded) return;
    sessionEnded = true;

    if (pingInterval) {
      clearInterval(pingInterval);
      pingInterval = null;
    }

    const costUsd = computeDeepgramTranscriptionCost(totalDurationSeconds);
    const creditsUsed = creditsForCost(costUsd);

    if (clientSocket) {
      sendToClient(clientSocket, {
        type: 'session_complete',
        duration_seconds: totalDurationSeconds,
        credits_used: creditsUsed,
      });
    }

    if (creditsUsed > 0) {
      deductCredits(
        auth,
        costUsd,
        {
          audio_duration_seconds: totalDurationSeconds,
          transcription_cost_usd: costUsd,
          language: language || 'auto',
          endpoint: '/ws/streaming-deepgram',
          stt_provider: 'deepgram-nova3-live',
        },
        clientIP
      ).catch(console.error);
    }
  }

  function closeUpstream(): void {
    // readyState 0 = CONNECTING, 1 = OPEN — close both so a client that
    // disconnects mid-handshake doesn't leave the upstream socket open until
    // Deepgram's idle timeout. close() during CONNECTING aborts the handshake
    // once it completes; CLOSING/CLOSED need no action.
    if (deepgramWs && deepgramWs.readyState <= WebSocket.OPEN) {
      deepgramWs.close(1000, 'Client disconnected');
    }
  }

  return {
    onOpen: (_evt, ws) => {
      clientSocket = ws;

      if (!apiKey) {
        sendToClient(ws, { type: 'error', message: 'Deepgram API key not configured' });
        ws.close(1011, 'Configuration error');
        return;
      }

      deepgramWs = new WebSocket(dgUrl, ['token', apiKey]);

      deepgramWs.addEventListener('open', () => {
        // If the client already disconnected while we were still handshaking,
        // tear down the upstream socket instead of leaving it orphaned.
        if (ws.readyState !== 1) {
          closeUpstream();
          return;
        }
        sendToClient(ws, { type: 'ready', sessionId: requestId });
      });

      deepgramWs.addEventListener('message', (event) => {
        try {
          const data = JSON.parse(event.data as string) as DeepgramLiveResponse;
          if (data.type === 'Results') {
            const transcript = data.channel?.alternatives?.[0]?.transcript || '';
            if (transcript || data.is_final) {
              sendToClient(ws, {
                type: 'transcript',
                text: transcript,
                is_final: data.is_final ?? false,
                speech_final: data.speech_final ?? false,
              });
            }
          }
        } catch (error) {
          console.warn('Failed to parse Deepgram message', error);
        }
      });

      deepgramWs.addEventListener('error', () => {
        sendToClient(ws, { type: 'error', message: 'Transcription service error' });
      });

      deepgramWs.addEventListener('close', async () => {
        await endSession();
        if (ws.readyState === 1) {
          ws.close(1000, 'Session ended');
        }
      });

      // Send ping every 30s to prevent Fly.io's 60s idle timeout from killing the connection
      pingInterval = setInterval(() => {
        if (clientSocket && clientSocket.readyState === 1) {
          clientSocket.send(JSON.stringify({ type: 'ping' }));
        }
      }, 30000);
    },
    onMessage: (event) => {
      if (!deepgramWs || deepgramWs.readyState !== WebSocket.OPEN) {
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

        // Backpressure: if the upstream socket is already congested, drop this
        // chunk instead of queueing more memory into the outbound buffer.
        if (deepgramWs.bufferedAmount > MAX_DEEPGRAM_BUFFERED_BYTES) {
          if (clientSocket) {
            sendToClient(clientSocket, { type: 'error', message: 'Transcription service busy, audio dropped' });
          }
          return;
        }

        deepgramWs.send(data);
        totalDurationSeconds += durationSecondsForLinear16AudioBytes(data.byteLength);

        // End the session once the running cost reaches the balance seen at auth,
        // so a low-balance user can't stream indefinitely on end-of-session billing.
        const creditsUsed = creditsForCost(computeDeepgramTranscriptionCost(totalDurationSeconds));
        if (creditsUsed >= auth.credits) {
          if (clientSocket) {
            sendToClient(clientSocket, { type: 'error', message: 'Credit balance exhausted' });
          }
          deepgramWs.close(1000, 'Credits exhausted');
        }
        return;
      }

      if (typeof data === 'string') {
        try {
          const msg = JSON.parse(data) as { type?: string };
          if (msg.type === 'stop') {
            deepgramWs.close(1000, 'Client requested stop');
            return;
          }
          if (msg.type === 'pong') {
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
});
