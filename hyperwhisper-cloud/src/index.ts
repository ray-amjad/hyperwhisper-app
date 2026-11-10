// HYPERWHISPER FLY.IO TRANSCRIPTION SERVICE
// Edge-based transcription proxy replacing Cloudflare Workers
// Eliminates R2 upload path complexity - can buffer larger audio files in memory

import { Hono } from 'hono';
import { cors } from 'hono/cors';
import { websocket } from 'hono/bun';
import { transcribeRoute } from './routes/transcribe';
import { postProcessRoute } from './routes/post-process';
import { usageRoute } from './routes/usage';
import { licenseCacheInvalidateRoute } from './routes/internal-license-cache';
import { wsStreamingPreflight, wsStreamingRoute } from './routes/ws-streaming-deepgram';
import {
  wsStreamingGeminiTranscribePreflight,
  wsStreamingGeminiTranscribeRoute,
} from './routes/ws-streaming-gemini-transcribe';
import { CLIENT_PLATFORM_HEADER, CLIENT_VERSION_HEADER } from './lib/client-info';
import { ipRateLimit } from './middleware/rate-limit';
import { trackInFlightRequests } from './middleware/in-flight-requests';
import { runGracefulShutdown, SHUTDOWN_DRAIN_MS } from './lib/graceful-shutdown';
import { MAX_AUDIO_SIZE_BYTES } from './lib/constants';
import { toRedactedLogLine } from './lib/log-redaction';
import { captureServerError, initErrorTracking } from './lib/error-tracking';

// Server error tracking (#2013): a no-op unless SENTRY_DSN is set.
initErrorTracking();

const app = new Hono();

// CORS — allow the custom STT selection headers so browser callers can set
// provider/model/domain. Without these in allowHeaders the preflight blocks the
// POST before it reaches the route (X-STT-* are non-simple request headers).
// X-HyperWhisper-Platform / -Version are non-simple for the same reason.
app.use('*', cors({
  origin: '*',
  allowHeaders: [
    'Content-Type',
    'X-STT-Provider',
    'X-STT-Model',
    'X-STT-Domain',
    'X-Latency-Opt-Out',
    CLIENT_PLATFORM_HEADER,
    CLIENT_VERSION_HEADER,
  ],
  allowMethods: ['GET', 'POST', 'OPTIONS'],
}));

app.options('*', (c) => c.body(null, 204));

// Health check endpoint - Fly.io uses this for health monitoring
app.get('/health', (c) => {
  return c.json({
    status: 'ok',
    region: process.env.FLY_REGION || 'local',
    timestamp: new Date().toISOString(),
  });
});

// Warmup endpoint - clients hit this on hotkey-down to pre-establish
// the TLS/HTTP2 connection before the /transcribe POST.
app.get('/warmup', (c) => {
  c.header('Cache-Control', 'no-store');
  return c.body(null, 204);
});

// Every key-accepting route runs the per-IP rate limit (`ipRateLimit`, 120/min)
// before it parses a body or looks up a key. The per-key limits run inside each
// route, after auth (middleware/rate-limit.ts, #2010).
//
// Only `trackInFlightRequests` runs before it, on `/transcribe` and
// `/post-process`: it counts them so a deploy's SIGTERM waits for them (and
// refuses new ones) instead of exiting under them (#2031). It reads no body.

// Main transcription endpoint
app.post('/transcribe', trackInFlightRequests, ipRateLimit, transcribeRoute);

// Standalone post-processing endpoint
app.post('/post-process', trackInFlightRequests, ipRateLimit, postProcessRoute);

// Usage endpoint
app.get('/usage', ipRateLimit, usageRoute);

// WebSocket streaming endpoints. One route per live vendor, named
// `/ws/streaming-{sttProvider}` after the `cloud-stt-catalog.json` entry so the
// clients derive the path instead of carrying a table on three platforms.
app.get('/ws/streaming-deepgram', ipRateLimit, wsStreamingPreflight, wsStreamingRoute);
app.get(
  '/ws/streaming-gemini-transcribe',
  ipRateLimit,
  wsStreamingGeminiTranscribePreflight,
  wsStreamingGeminiTranscribeRoute,
);

// Site -> cloud: the site rotated an Account Key, so drop the old key's cached
// "valid" verdict now instead of when its hour runs out. Gated by the shared
// internal secret inside the route; the per-IP limit still runs first.
app.post('/internal/license-cache/invalidate', ipRateLimit, licenseCacheInvalidateRoute);

// Fallback - match Cloudflare (405, plain text)
app.notFound((c) => {
  return c.text('Method not allowed', 405);
});

// Error handler — never echo raw err.message to the client (it can contain
// env-var names, upstream provider bodies, request IDs). Log the error
// server-side with an error_id the client can quote to support.
//
// Not the raw error object either (#2163): its message can quote an upstream
// body and it prints a multi-line stack the log shipper splits into several
// records. Logged instead: the error's name, its message through
// `toRedactedLogLine` (secrets cut, payloads cut, capped at 200 characters),
// and up to 8 stack frames, which are code locations, not data.
const UNHANDLED_ERROR_STACK_FRAMES = 8;

export function unhandledErrorLogFields(err: unknown): { errorName: string; message: string; stack: string[] } {
  const errorName = err instanceof Error ? err.name : typeof err;
  const stack = err instanceof Error && typeof err.stack === 'string'
    ? err.stack.split('\n').map((line) => line.trim()).filter((line) => line.startsWith('at ')).slice(0, UNHANDLED_ERROR_STACK_FRAMES)
    : [];
  return { errorName, message: toRedactedLogLine(err), stack };
}

app.onError((err, c) => {
  const errorId = crypto.randomUUID();
  console.error(`Unhandled error [${errorId}]:`, JSON.stringify(unhandledErrorLogFields(err)));
  captureServerError(err, { errorId, route: c.req.routePath, method: c.req.method });
  return c.json({
    error: 'Internal server error',
    message: 'An unexpected error occurred. Please try again.',
    error_id: errorId,
  }, 500);
});

// Graceful shutdown on deploy/scale-down (#2031): the steps and their 100 s /
// 115 s deadlines live in `lib/graceful-shutdown.ts`. The one-shot latch stays
// here, in the signal handler: Fly can send SIGINT after SIGTERM, and a second
// run would exit mid-drain.
export { SHUTDOWN_DRAIN_MS };
let shuttingDown = false;

function onShutdownSignal(signal: string): void {
  if (shuttingDown) return;
  shuttingDown = true;
  void runGracefulShutdown(signal);
}

process.on('SIGTERM', () => { onShutdownSignal('SIGTERM'); });
process.on('SIGINT', () => { onShutdownSignal('SIGINT'); });

// Bun.serve's own idle limit, in SECONDS (#1252). Bun's default is 10 s, which
// cut every request silent for longer than that with an empty reply, before
// any vendor bound (15/20/45 s) could fire. 255 s is Bun's maximum: some single
// requests stay silent for over 60 s (google-chirp batch polling up to
// BATCH_POLL_DEADLINE_MS, callWithRetry chains on /post-process), so Bun must
// never be a second, tighter cutter. On Fly the proxy's fixed 60 s no-bytes
// limit (see the 30 s ping in routes/ws-streaming-shared.ts) is still the
// binding cut for silent requests.
export const SERVER_IDLE_TIMEOUT_SECONDS = 255;

// Export for Bun
export default {
  port: Number(process.env.PORT) || 8080,
  idleTimeout: SERVER_IDLE_TIMEOUT_SECONDS,
  // Bun's own body limit, the same constant the route's Content-Length gate
  // reads (#2011). Bun refuses a larger body itself, chunked uploads included.
  maxRequestBodySize: MAX_AUDIO_SIZE_BYTES,
  fetch: app.fetch,
  websocket,
};

console.log('machine.boot', {
  region: process.env.FLY_REGION || 'local',
  machineId: process.env.FLY_MACHINE_ID || 'local',
  allocId: process.env.FLY_ALLOC_ID || undefined,
  imageRef: process.env.FLY_IMAGE_REF || undefined,
  port: Number(process.env.PORT) || 8080,
  bootAt: new Date().toISOString(),
});
console.log(`HyperWhisper Fly.io service starting on port ${process.env.PORT || 8080}`);
console.log(`Region: ${process.env.FLY_REGION || 'local'}`);
console.log(`License API: ${process.env.NEXTJS_LICENSE_API_URL || 'https://www.hyperwhisper.com (default)'}`);
console.log('Started server: http://localhost:' + (process.env.PORT || 8080));
