// HYPERWHISPER FLY.IO TRANSCRIPTION SERVICE
// Edge-based transcription proxy replacing Cloudflare Workers
// Eliminates R2 upload path complexity - can buffer larger audio files in memory

import { Hono } from 'hono';
import { cors } from 'hono/cors';
import { websocket } from 'hono/bun';
import { transcribeRoute } from './routes/transcribe';
import { postProcessRoute } from './routes/post-process';
import { assistantRoute } from './routes/assistant';
import { usageRoute } from './routes/usage';
import { wsStreamingPreflight, wsStreamingRoute } from './routes/ws-streaming-deepgram';
import { drainPendingDeductions } from './middleware/credits';

const app = new Hono();

// CORS — allow the custom STT selection headers so browser callers can set
// provider/model/domain. Without these in allowHeaders the preflight blocks the
// POST before it reaches the route (X-STT-* are non-simple request headers).
app.use('*', cors({
  origin: '*',
  allowHeaders: ['Content-Type', 'X-STT-Provider', 'X-STT-Model', 'X-STT-Domain'],
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

// Main transcription endpoint
app.post('/transcribe', transcribeRoute);

// Standalone post-processing endpoint
app.post('/post-process', postProcessRoute);

// Assistant mode endpoint (vision LLM)
app.post('/assistant', assistantRoute);

// Usage endpoint
app.get('/usage', usageRoute);

// WebSocket streaming endpoint
app.get('/ws/streaming-deepgram', wsStreamingPreflight, wsStreamingRoute);

// Fallback - match Cloudflare (405, plain text)
app.notFound((c) => {
  return c.text('Method not allowed', 405);
});

// Error handler — never echo raw err.message to the client (it can contain
// env-var names, upstream provider bodies, request IDs). Log the full error
// server-side with an error_id the client can quote to support.
app.onError((err, c) => {
  const errorId = crypto.randomUUID();
  console.error(`Unhandled error [${errorId}]:`, err);
  return c.json({
    error: 'Internal server error',
    message: 'An unexpected error occurred. Please try again.',
    error_id: errorId,
  }, 500);
});

// Graceful shutdown: Fly sends SIGTERM on deploy/scale-down with a grace
// period before SIGKILL (kill_timeout, default 5s). Credit deductions are
// fired without awaiting after the response is flushed, so drain any that are
// still in flight before exiting — otherwise the user keeps the transcript
// but the charge is silently dropped.
const SHUTDOWN_DRAIN_MS = 4_000;
let shuttingDown = false;

async function gracefulShutdown(signal: string): Promise<void> {
  if (shuttingDown) return;
  shuttingDown = true;

  console.log('machine.shutdown', {
    signal,
    machineId: process.env.FLY_MACHINE_ID || 'local',
    shutdownAt: new Date().toISOString(),
  });

  const drained = await drainPendingDeductions(SHUTDOWN_DRAIN_MS);
  if (drained > 0) {
    console.log('machine.shutdown_drained_deductions', { count: drained });
  }
  process.exit(0);
}

process.on('SIGTERM', () => { void gracefulShutdown('SIGTERM'); });
process.on('SIGINT', () => { void gracefulShutdown('SIGINT'); });

// Export for Bun
export default {
  port: Number(process.env.PORT) || 8080,
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
