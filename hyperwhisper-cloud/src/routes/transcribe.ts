// TRANSCRIPTION ROUTE
// POST /transcribe - Main transcription endpoint
// Supports multiple STT providers with automatic fallback

import type { Context } from 'hono';
import { transcribeWithDeepgram } from '../providers/deepgram';
import { transcribeWithGroq } from '../providers/groq';
import { transcribeWithElevenLabs } from '../providers/elevenlabs';
import { transcribeWithXaiGrok } from '../providers/xai-stt';
import { transcribeWithAzureMai } from '../providers/azure-mai';
import { transcribeWithGoogleChirp } from '../providers/google-chirp';
import { transcribeWithOpenAI } from '../providers/openai';
import { transcribeWithGemini } from '../providers/gemini';
import {
  transcribeWithAssemblyAI,
  hasExplicitLanguage as hasExplicitAssemblyAILanguage,
  SYNC_ELIGIBLE_ESTIMATED_SECONDS as ASSEMBLYAI_SYNC_ELIGIBLE_ESTIMATED_SECONDS,
} from '../providers/assemblyai';
import { transcribeWithMistral } from '../providers/mistral';
import { transcribeWithSoniox } from '../providers/soniox';
import type { ProviderRequestContext, TranscriptionResult } from '../providers/types';
import { AudioTooLargeError, ProviderInputError, ProviderUnavailableError, UnsupportedAudioFormatError } from '../providers/types';
import { creditsForCost, estimatePromptInputReservationUsd, formatUsd } from '../lib/cost-calculator';
import {
  estimatedUsdPerMinute,
  getProviderDef,
  isValidProviderId,
  resolveModel,
  MEDICAL_DOMAIN,
  ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE,
  type SttProviderId,
} from '../lib/stt-models';
import { readClientInfo } from '../lib/client-info';
import { generateRequestId, getClientIP, getFlyRequestId } from '../lib/request-id';
import {
  reportLatencySamples,
  type LatencyFailureKind,
  type LatencySample,
} from '../lib/latency-report';
// Content-type aware, unlike the billing estimators: a failed attempt still has
// to land in the right clip-length bucket on the public /latency page.
// runProviderAttempt is how the same page learns whether an attempt ever reached
// the provider at all.
import { estimateAudioSeconds, runProviderAttempt, type ProviderAttemptNetwork } from '../providers/utils';
import { rawQuery } from '../lib/query';
import {
  FLY_REPLAY_MAX_BODY_BYTES,
  GEMINI_INLINE_MAX_BYTES,
  GOOGLE_CHIRP_INLINE_MAX_BYTES,
  MAX_AUDIO_SIZE_BYTES,
  OPENAI_INLINE_MAX_BYTES,
} from '../lib/constants';
import { isIPBlocked } from '../lib/redis';
import {
  errorResponse,
  fileTooLargeResponse,
  invalidContentTypeResponse,
  missingContentLengthResponse,
} from '../lib/responses';
import { validateAuth } from '../middleware/auth';
import { deductCredits, estimateAudioSecondsFromSize, validateCredits } from '../middleware/credits';
import { flyProxyOverheadMs, logEvent, machineUptimeMs } from '../lib/logging';

// Supported providers (mirror the server-side registry in lib/stt-models.ts).
export type Provider = SttProviderId;

// Fly regions where ElevenLabs serves a text/html FAQ page instead of JSON
// (geo-block on Japan + India confirmed via per-region smoke 2026-06-07).
// Requests landing here are replayed to `iad` before any work happens.
const ELEVENLABS_BLOCKED_FLY_REGIONS = new Set(['nrt', 'bom', 'maa']);
const ELEVENLABS_REPLAY_REGION = 'iad';

// Human-readable base label per provider. The model is appended at runtime via
// formatProviderName() so the response header / metering reflects exactly which
// model ran (e.g. "deepgram/nova-3-medical", "openai/gpt-4o-transcribe").
const PROVIDER_NAMES: Record<Provider, string> = {
  deepgram: 'deepgram',
  elevenlabs: 'elevenlabs',
  groq: 'groq',
  grok: 'xai-grok',
  'azure-mai': 'azure-mai',
  'google-chirp': 'google-chirp',
  openai: 'openai',
  gemini: 'gemini',
  assemblyai: 'assemblyai',
  mistral: 'mistral',
  soniox: 'soniox',
};

function formatProviderName(provider: Provider, model: string): string {
  const base = PROVIDER_NAMES[provider];
  return model ? `${base}/${model}` : base;
}

// Fallback chains: the original cheap trio (plus grok) cascade through
// alternatives — ElevenLabs (most expensive) is the last resort. Every other
// provider is SELF-ONLY: the caller picked that specific model, so on failure
// we surface an error rather than silently substituting a different model and
// price. (A cross-provider fallback would also change the metered cost.)
const FALLBACK_CHAINS: Record<Provider, Provider[]> = {
  elevenlabs: ['elevenlabs', 'deepgram', 'groq'],
  groq: ['groq', 'deepgram', 'elevenlabs'],
  deepgram: ['deepgram', 'groq', 'elevenlabs'],
  grok: ['grok', 'deepgram', 'groq', 'elevenlabs'],
  'azure-mai': ['azure-mai'],
  'google-chirp': ['google-chirp'],
  openai: ['openai'],
  gemini: ['gemini'],
  assemblyai: ['assemblyai'],
  mistral: ['mistral'],
  soniox: ['soniox'],
};

const PROVIDER_FN: Record<Provider, (
  audio: ArrayBuffer,
  contentType: string,
  language?: string,
  initialPrompt?: string,
  context?: ProviderRequestContext,
) => Promise<TranscriptionResult>> = {
  deepgram: transcribeWithDeepgram,
  groq: transcribeWithGroq,
  elevenlabs: transcribeWithElevenLabs,
  grok: transcribeWithXaiGrok,
  'azure-mai': transcribeWithAzureMai,
  'google-chirp': transcribeWithGoogleChirp,
  openai: transcribeWithOpenAI,
  gemini: transcribeWithGemini,
  assemblyai: transcribeWithAssemblyAI,
  mistral: transcribeWithMistral,
  soniox: transcribeWithSoniox,
};

/**
 * Preflight credit reservation. For the primary provider we estimate against
 * the chosen model (and medical add-on); for fallback siblings we estimate
 * against their default model. The reservation uses the most expensive member
 * of the chain so we never under-reserve. `model`/`medical` are optional to
 * keep the historical 2-arg call signature working.
 */
export function estimateCreditsForProviderFallbacks(
  sizeBytes: number,
  provider: Provider,
  model?: string,
  medical: boolean = false,
  initialPrompt?: string,
  language?: string,
): number {
  const chain = FALLBACK_CHAINS[provider];
  const estimatedSeconds = estimateAudioSecondsFromSize(sizeBytes);
  const hasInitialPrompt = Boolean(initialPrompt);
  const rates = chain.map((p) => estimatedUsdPerMinute(
    p,
    p === provider ? model : undefined,
    p === provider ? medical : false,
    // The keyterm surcharge is billed by ANY chain member that supports it
    // (ElevenLabs scribe_v2 / AssemblyAI universal-3-pro) whenever an
    // initial_prompt is present — not just the primary provider. A
    // Deepgram→ElevenLabs fallback still forwards initial_prompt and bills the
    // +20% surcharge, so reserve for it on every eligible sibling. Other
    // providers ignore the flag (estimatedUsdPerMinute scopes the add-on), so
    // this never over-reserves for, say, a Deepgram-only success path.
    hasInitialPrompt && (p === 'elevenlabs' || p === 'assemblyai'),
  ));
  // AssemblyAI's sync fast path (<120s, non-medical, EXPLICIT-language clips)
  // always runs universal-3-5-pro at its OWN higher published rate — not the
  // async catalog rate for the requested model (universal-2 / universal-3-pro),
  // which `estimatedUsdPerMinute` above reserves against. A short clip is
  // exactly sync's target case, so without this a short, non-medical
  // AssemblyAI request could be deducted for more than was reserved. This
  // condition must exactly mirror `transcribeWithAssemblyAI`'s real
  // eligibility gate (medical + duration + explicit language) — reusing
  // `hasExplicitLanguage` rather than reimplementing it here, since an
  // auto-language request never actually routes through sync (sync has no
  // auto-detect) and over-reserving for it could wrongly reject a low-balance
  // user at preflight for a request that will only ever go through the
  // cheaper async path.
  if (
    provider === 'assemblyai' && !medical
    && estimatedSeconds < ASSEMBLYAI_SYNC_ELIGIBLE_ESTIMATED_SECONDS
    && hasExplicitAssemblyAILanguage(language)
  ) {
    rates.push(ASSEMBLYAI_SYNC_ESTIMATED_USD_PER_MINUTE);
  }
  const usdPerMinute = Math.max(...rates);
  // Token-billed providers (Gemini, OpenAI gpt-4o*) charge the prompt text as
  // input tokens on top of the audio. Reserve that flat cost for the primary
  // provider (these are self-only chains) so a large vocabulary prompt on a
  // short clip can't be deducted beyond what was reserved.
  const promptReservationUsd = estimatePromptInputReservationUsd(provider, model, initialPrompt);
  const estimatedCostUsd = (estimatedSeconds / 60) * usdPerMinute + promptReservationUsd;
  return Math.max(0.1, creditsForCost(estimatedCostUsd));
}

type ProviderSelection =
  | { ok: true; provider: Provider }
  | { ok: false; provided: string };

function extractProvider(c: Context): ProviderSelection {
  const header = c.req.header('X-STT-Provider')?.toLowerCase().trim();
  // No header → historical default (many clients send only a provider, some
  // none). An explicitly-supplied but unknown provider is REJECTED (fail-closed)
  // rather than silently billed against a default upstream.
  if (!header) {
    return { ok: true, provider: 'deepgram' };
  }
  if (isValidProviderId(header)) {
    return { ok: true, provider: header };
  }
  return { ok: false, provided: header };
}

function extractModel(c: Context): string | undefined {
  return c.req.header('X-STT-Model')?.trim() || rawQuery(c.req.url, 'model')?.trim() || undefined;
}

function extractDomain(c: Context): string | undefined {
  const domain = c.req.header('X-STT-Domain')?.toLowerCase().trim();
  return domain || undefined;
}

/**
 * Values that mean "the header is present but the client is NOT opting out".
 * Everything else counts as an opt-out, including values we never documented —
 * a client that bothers to send this header at all means to be excluded, so an
 * unrecognised value fails toward privacy rather than toward more data.
 */
const LATENCY_OPT_IN_VALUES = new Set(['', '0', 'false', 'no', 'off']);

/**
 * True when the caller asked to be left out of the public latency statistics
 * (`X-Latency-Opt-Out: 1`). The macOS and Windows apps send this when the user
 * turns off "Share anonymous speed data" in settings.
 *
 * Opting out costs the user nothing and changes nothing else about the
 * request — it only stops the anonymous timing row from being written. See
 * lib/latency-report.ts for what that row holds.
 */
export function isLatencyOptOut(c: Context): boolean {
  const header = c.req.header('X-Latency-Opt-Out');
  if (header === undefined) return false;
  return !LATENCY_OPT_IN_VALUES.has(header.toLowerCase().trim());
}

/**
 * The public page's failure taxonomy for whatever the attempt threw. Keeps the
 * mapping in one place so the catch arms below stay pure control flow.
 */
function failureKindFor(error: unknown): LatencyFailureKind {
  if (error instanceof ProviderUnavailableError) return error.kind;
  if (error instanceof ProviderInputError) return 'input_rejected';
  // A revoked key or a bug in an adapter lands here. Without a sample the page
  // would report a 0% error rate for a provider that fails every call.
  return 'unknown';
}

/**
 * How long a failed attempt cost the user, from the route's own clock.
 *
 * Deliberately NOT ProviderUnavailableError.elapsedMs: for the async providers
 * (AssemblyAI, Soniox, Google Chirp) that field times only the single
 * fetchWithTimeout that failed, while the upload, the job creation and every
 * earlier poll are already spent — a 90-second wait reported as the 8 seconds
 * of its last poll. The adapter's own number stays in the structured log, where
 * "which call failed" is the question; the page answers "how long did this
 * take", which is this one.
 */
function elapsedFor(attemptStart: number): number {
  return performance.now() - attemptStart;
}

function validateStreamingHeaders(c: Context, provider: Provider):
  | { ok: true; contentType: string; contentLength: number }
  | { ok: false; response: Response } {
  const contentType = c.req.header('Content-Type') || '';
  if (!contentType.startsWith('audio/')) {
    return { ok: false, response: invalidContentTypeResponse('audio/*', contentType) };
  }

  const contentLengthHeader = c.req.header('Content-Length');
  if (!contentLengthHeader) {
    return { ok: false, response: missingContentLengthResponse() };
  }

  const contentLength = Number.parseInt(contentLengthHeader, 10);
  if (!Number.isFinite(contentLength) || contentLength <= 0) {
    return { ok: false, response: errorResponse(400, 'Invalid Content-Length', 'Content-Length must be a positive integer') };
  }

  if (contentLength > MAX_AUDIO_SIZE_BYTES) {
    return { ok: false, response: fileTooLargeResponse(contentLength, MAX_AUDIO_SIZE_BYTES) };
  }

  // Google Chirp inline cap (~9.5 MB) applies before we buffer the body —
  // without a scratch GCS bucket the provider has no path for larger audio,
  // and we don't want to allocate a 50 MB buffer just to 413 the caller.
  if (
    provider === 'google-chirp'
    && contentLength > GOOGLE_CHIRP_INLINE_MAX_BYTES
    && !(process.env.GOOGLE_SPEECH_GCS_BUCKET || '').trim()
  ) {
    return {
      ok: false,
      response: fileTooLargeResponse(contentLength, GOOGLE_CHIRP_INLINE_MAX_BYTES),
    };
  }

  // Gemini sends audio inline (base64) and rejects anything over ~14 MB raw.
  // Gate on Content-Length before buffering so an oversized upload is rejected
  // early instead of after buffering up to MAX_AUDIO_SIZE_BYTES on the machine.
  if (provider === 'gemini' && contentLength > GEMINI_INLINE_MAX_BYTES) {
    return {
      ok: false,
      response: fileTooLargeResponse(contentLength, GEMINI_INLINE_MAX_BYTES),
    };
  }

  // OpenAI hard-rejects audio over 25 MB with a 400. Gate on Content-Length
  // before buffering so we return 413 without allocating the buffer first.
  if (provider === 'openai' && contentLength > OPENAI_INLINE_MAX_BYTES) {
    return {
      ok: false,
      response: fileTooLargeResponse(contentLength, OPENAI_INLINE_MAX_BYTES),
    };
  }

  return { ok: true, contentType, contentLength };
}

export async function transcribeRoute(c: Context) {
  const requestId = generateRequestId();
  const startTime = performance.now();
  const clientIP = getClientIP(c);
  const flyRequestId = getFlyRequestId(c);

  // IP block check
  if (await isIPBlocked(clientIP)) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'ip_blocked',
      flyRequestId,
    });
    return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
  }
  logEvent(requestId, startTime, 'transcribe.ip_check_done', { flyRequestId });

  const providerSelection = extractProvider(c);
  if (!providerSelection.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_provider',
      flyRequestId,
      provided: providerSelection.provided,
    });
    return errorResponse(400, 'Invalid STT provider',
      `Unknown X-STT-Provider "${providerSelection.provided}".`,
      { requestId, provided: providerSelection.provided },
    );
  }
  const provider = providerSelection.provider;

  // Resolve + validate the requested model against the server-side registry.
  // An unknown model for the provider is rejected (fail-closed) rather than
  // silently routed to the provider default at a possibly different price.
  const requestedModel = extractModel(c);
  const modelResolution = resolveModel(provider, requestedModel);
  if (!modelResolution.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_model',
      flyRequestId,
      provider,
      requestedModel,
    });
    return errorResponse(400, 'Invalid STT model', modelResolution.reason, {
      requestId,
      provider,
      requested_model: requestedModel,
      valid_models: modelResolution.validModels,
    });
  }
  const model = modelResolution.model.id;

  const domain = extractDomain(c);
  // Medical add-on only applies where the provider meters it (AssemblyAI today).
  const medical = domain === MEDICAL_DOMAIN;

  const headerValidation = validateStreamingHeaders(c, provider);
  if (!headerValidation.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_streaming_headers',
      flyRequestId,
      provider,
      status: headerValidation.response.status,
    });
    return headerValidation.response;
  }

  const { contentType, contentLength } = headerValidation;
  // rawQuery, not c.req.query(): Hono's decoder adds an HTML-form `+` → space
  // step, corrupting values like a `C++` vocabulary term. See lib/query.ts.
  const language = rawQuery(c.req.url, 'language');
  const initialPrompt = rawQuery(c.req.url, 'initial_prompt');
  const mode = rawQuery(c.req.url, 'mode');

  // ElevenLabs blocks API access from certain countries — the block surfaces
  // as a 200 OK with a text/html FAQ page ("Do you restrict access ... for any
  // specific countries?") instead of JSON. When the request lands on a Fly
  // machine in one of those countries, replay it via Fly's edge to `iad`
  // before doing any auth/credit work. Adds ~50-80ms vs ~6s of failure.
  // Verified blocked regions (2026-06-07): nrt (JP), bom (IN), maa (IN).
  //
  // Fly only honours `fly-replay` for request bodies ≤ 1 MB; larger requests
  // are silently executed in the original region. For oversized uploads from
  // a blocked region we skip the replay header and let the chain fall back
  // to the next provider instead of letting ElevenLabs return its HTML 200.
  let elevenlabsGeoBlocked = false;
  if (provider === 'elevenlabs' && ELEVENLABS_BLOCKED_FLY_REGIONS.has(process.env.FLY_REGION || '')) {
    if (contentLength <= FLY_REPLAY_MAX_BODY_BYTES) {
      logEvent(requestId, startTime, 'transcribe.fly_replay', {
        flyRequestId,
        provider,
        fromRegion: process.env.FLY_REGION,
        toRegion: ELEVENLABS_REPLAY_REGION,
        reason: 'elevenlabs_geo_block',
      });
      c.header('fly-replay', `region=${ELEVENLABS_REPLAY_REGION}`);
      return c.body(null, 200);
    }

    elevenlabsGeoBlocked = true;
    logEvent(requestId, startTime, 'transcribe.fly_replay_skipped_oversized', {
      flyRequestId,
      provider,
      flyRegion: process.env.FLY_REGION,
      contentLength,
      replayMaxBytes: FLY_REPLAY_MAX_BODY_BYTES,
    });
  }

  const proxyOverheadMs = flyProxyOverheadMs(c.req.header('Fly-Request-Start'));
  const { clientPlatform, clientVersion } = readClientInfo(c);
  logEvent(requestId, startTime, 'transcribe.request_start', {
    flyRequestId,
    clientPlatform,
    clientVersion,
    flyRegion: process.env.FLY_REGION || 'local',
    flyMachineId: process.env.FLY_MACHINE_ID,
    proxyOverheadMs,
    provider,
    model: model || 'default',
    domain: domain || 'none',
    contentType,
    contentLength,
    language: language || 'auto',
    hasInitialPrompt: Boolean(initialPrompt),
    mode: mode || 'default',
  });

  // Auth (query params only) — Cloud is licensed-only; a valid account key is required.
  // `account_key` is the canonical param name; `license_key` is the legacy alias
  // that installed native apps still send, so we accept either.
  const authResult = await validateAuth({
    licenseKey:
      rawQuery(c.req.url, 'account_key') ?? rawQuery(c.req.url, 'license_key'),
  });
  if (!authResult.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'auth_failed',
      flyRequestId,
      status: authResult.response.status,
    });
    return authResult.response;
  }
  logEvent(requestId, startTime, 'transcribe.auth_done');

  // Vocabulary surcharge: AssemblyAI charges a keyterms_prompt add-on (universal-3-pro)
  // and ElevenLabs a +20% keyterm surcharge (scribe_v2) when an initial_prompt is supplied.
  // We pass the raw hasInitialPrompt flag through to the reservation so it can reserve the
  // surcharge for ANY eligible chain member — including ElevenLabs reached via a
  // Deepgram/Groq/Grok fallback, which still forwards the prompt and bills the surcharge.
  // estimatedUsdPerMinute scopes the add-on to universal-3-pro / scribe_v2, so passing it
  // for every request is safe and never under-reserves.
  const estimatedCredits = estimateCreditsForProviderFallbacks(contentLength, provider, model, medical, initialPrompt, language);
  const creditCheck = await validateCredits(authResult.value, estimatedCredits, clientIP);
  if (!creditCheck.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'credits_failed',
      flyRequestId,
      status: creditCheck.response.status,
      estimatedCredits,
    });
    return creditCheck.response;
  }
  logEvent(requestId, startTime, 'transcribe.credits_done', { estimatedCredits });

  const uploadStart = performance.now();
  const audioBuffer = await c.req.arrayBuffer();
  const uploadMs = Math.round(performance.now() - uploadStart);
  const uploadBytesPerSec = uploadMs > 0
    ? Math.round((audioBuffer.byteLength / uploadMs) * 1000)
    : undefined;
  logEvent(requestId, startTime, 'transcribe.buffer_read_done', {
    audioBytes: audioBuffer.byteLength,
    uploadMs,
    uploadBytesPerSec,
  });

  // The credit check above trusted the declared Content-Length. Reject bodies
  // that arrive larger than declared so a client can't under-declare to pass
  // validateCredits cheaply and then stream a bigger payload we'd pay the
  // provider for (issue ray-amjad/hyperwhisper#263). Honest clients always
  // send a body that matches Content-Length exactly.
  if (audioBuffer.byteLength > contentLength) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'content_length_mismatch',
      flyRequestId,
      declaredBytes: contentLength,
      actualBytes: audioBuffer.byteLength,
    });
    return errorResponse(400, 'Content-Length mismatch',
      `Request body (${audioBuffer.byteLength} bytes) exceeds the declared Content-Length (${contentLength} bytes)`,
      { requestId, declared_bytes: contentLength, actual_bytes: audioBuffer.byteLength },
    );
  }

  let result: TranscriptionResult | undefined;
  let fallbackFrom: Provider | undefined;
  let fallbackCount = 0;
  // The model that actually produced the result. Defaults to the requested
  // model; on a cross-provider fallback it becomes that sibling's default model.
  let usedModel = model;

  // When the request landed in a region where ElevenLabs is geo-blocked AND
  // the payload was too large to fly-replay, drop ElevenLabs from the chain
  // so we fall through to the next provider instead of failing the chain on
  // ElevenLabs's HTML-200 geo-block response.
  const chain = elevenlabsGeoBlocked
    ? FALLBACK_CHAINS[provider].filter(p => p !== 'elevenlabs')
    : FALLBACK_CHAINS[provider];
  let lastError: Error | undefined;
  let lastInputError: ProviderInputError | undefined;
  let sawUnavailable = false;
  // Per-attempt failure breadcrumbs, surfaced on the final outcome log so one
  // line explains a degraded/failed request (which provider failed, why, how
  // long it hung) without correlating separate provider-level log events.
  const attemptFailures: Array<{
    provider: Provider;
    kind: string;
    status?: number;
    attemptMs?: number;
  }> = [];
  // Anonymous per-attempt timings for the public /latency page. Collected here
  // and sent once, after the response is decided, so reporting never adds wall
  // time to the latency it is measuring.
  const latencySamples: LatencySample[] = [];
  // Read once, up front: the header cannot change mid-request, and the send
  // site below is the only thing that consults it.
  const latencyOptOut = isLatencyOptOut(c);
  // The clip length every row of this request is filed under — one estimate,
  // from the bytes on the wire and the Content-Type describing them, used
  // identically on success and on failure.
  //
  // Deliberately NOT the adapter's `result.durationSeconds`. That is a BILLING
  // number, and when an upstream omits a duration the adapters fall back to
  // estimateSecondsFromBytes() — a flat 64 kbps assumption that overstates the
  // 16 kHz/16-bit mono WAV both desktop apps upload by ~4x. openai's default
  // model (gpt-4o-transcribe) reports only tokens, so it takes that fallback on
  // every call, and mistral/soniox/assemblyai take it whenever upstream omits a
  // duration: a 3-second dictation would be stored as 12 seconds and bucketed
  // 'medium'. Preferring it on success and estimating on failure also made the
  // two incomparable, and put one clip in different buckets depending on
  // whether the provider that answered happened to report a length. One
  // estimator for every row is what makes a cell a like-for-like comparison.
  const audioSeconds = estimateAudioSeconds(audioBuffer.byteLength, contentType);

  // The one place an attempt becomes a sample. Every arm out of the loop below
  // goes through it — success, retryable failure, and the failures that end the
  // request outright — so "one row per attempt" holds by construction instead
  // of by remembering to push. The loop is wrapped in a try/finally that sends
  // whatever this collected, so an early return can no longer lose the most
  // interesting rows this page has.
  const recordAttempt = (sample: {
    provider: Provider;
    /**
     * On success the model that actually ran (the adapter's, when it reports
     * one); on a failure the model the attempt was made with, since none ran.
     */
    model?: string;
    /** 0-based position in the chain; stored 1-based. */
    index: number;
    latencyMs: number;
    /** Absent on success. */
    failureKind?: LatencyFailureKind;
  }) => {
    latencySamples.push({
      provider: sample.provider,
      model: sample.model || undefined,
      latencyMs: sample.latencyMs,
      ok: sample.failureKind === undefined,
      failureKind: sample.failureKind,
      attempt: sample.index + 1,
      audioSeconds,
    });
  };

  try {
    for (const [index, current] of chain.entries()) {
      // The chosen model + domain only apply to the provider the caller picked.
      // Fallback siblings run their own default model (the caller's model id is
      // meaningless to them) and never inherit the medical add-on.
      const attemptModel = current === provider ? model : getProviderDef(current).defaultModel;
      const attemptDomain = current === provider ? domain : undefined;

      logEvent(requestId, startTime, 'transcribe.provider_attempt_start', {
        provider: current,
        model: attemptModel || 'default',
        attempt: index + 1,
      });

      // logEvent's elapsedMs runs from REQUEST start, so it already includes
      // upload, auth, credits, and every earlier attempt. This clock brackets
      // this provider call alone — the number the /latency page reports.
      const attemptStart = performance.now();
      // Flipped by the fetch helpers the instant this attempt's first request
      // leaves the process. Until then the adapter has only run its own gates —
      // a missing API key, a size cap, a content-type check — and those are our
      // `if`s, not an upstream, so they are not measurements. See
      // ProviderAttemptNetwork in providers/utils.ts for why the signal is taken
      // at the wire rather than from a list of error types.
      const network: ProviderAttemptNetwork = { reachedProvider: false };

      try {
        result = await runProviderAttempt(network, () => PROVIDER_FN[current](audioBuffer, contentType, language, initialPrompt, {
          requestId,
          attempt: index + 1,
          model: attemptModel,
          domain: attemptDomain,
        }));
        // Prefer the model the adapter reports it ACTUALLY ran (e.g. AssemblyAI's
        // universal-3-pro → universal-2 fallback for unsupported languages) so the
        // X-STT-Model header and deduction metadata match what was billed; fall
        // back to the attempted model when the adapter doesn't report one.
        usedModel = result.model || attemptModel;
        if (current !== provider) {
          fallbackFrom = provider;
        }
        const attemptMs = performance.now() - attemptStart;
        logEvent(requestId, startTime, 'transcribe.provider_attempt_done', {
          provider: current,
          model: attemptModel || 'default',
          attempt: index + 1,
          upstreamRequestId: result.requestId,
          transcriptChars: result.text.length,
          resultSource: result.source,
          attemptMs: Math.round(attemptMs),
        });
        recordAttempt({
          provider: current,
          model: usedModel,
          index,
          latencyMs: attemptMs,
        });
        break;
      } catch (error) {
        // ONE row per attempt, recorded here before any branching: the arms
        // below are pure control flow (log, fall back, or return) and none of
        // them can forget a sample or file a second one.
        //
        // The single condition is whether the attempt ever reached the wire.
        // Everything an adapter rejects on its own — a missing API key, a size
        // cap, a content-type it can't take — throws in microseconds without
        // calling anyone, and a row for it would publish that provider
        // "answering" in 1 ms and failing a call it never received. Every real
        // provider failure (timeout, 5xx, rate limit, an unusable 2xx, an
        // upstream 4xx) happens strictly after the request went out, so it is
        // still recorded — that direction is the bug this must not reintroduce.
        if (network.reachedProvider) {
          recordAttempt({
            provider: current,
            model: attemptModel,
            index,
            latencyMs: elapsedFor(attemptStart),
            failureKind: failureKindFor(error),
          });
        }

        if (error instanceof ProviderUnavailableError) {
          const next = chain[chain.indexOf(current) + 1];
          fallbackCount += 1;
          // `unavailableKind` distinguishes the root cause inline — `timeout`
          // (we gave up; upstream may have been fine) vs `upstream_5xx` /
          // `rate_limit` (upstream actually failed) vs `bad_response` (geo-block
          // HTML / empty body) — instead of the old catch-all `provider_unavailable`.
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'provider_unavailable',
            unavailableKind: error.kind,
            upstreamStatus: error.status,
            attemptMs: error.elapsedMs,
            message: error.message,
            nextProvider: next,
          });
          attemptFailures.push({
            provider: current,
            kind: error.kind,
            status: error.status,
            attemptMs: error.elapsedMs,
          });
          lastError = error;
          sawUnavailable = true;
          continue;
        }
        if (error instanceof ProviderInputError) {
          // The provider rejected this specific input (e.g. ElevenLabs 400 on a
          // language code it doesn't accept). A sibling provider may accept the
          // same input, so continue the fallback chain instead of failing the
          // whole request. (issue ray-amjad/hyperwhisper#333)
          const next = chain[chain.indexOf(current) + 1];
          fallbackCount += 1;
          logEvent(requestId, startTime, 'transcribe.provider_attempt_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'provider_input_rejected',
            status: error.status,
            message: error.message,
            nextProvider: next,
          });
          lastError = error;
          lastInputError = error;
          continue;
        }
        if (error instanceof AudioTooLargeError) {
          logEvent(requestId, startTime, 'transcribe.request_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'audio_too_large',
            message: error.message,
            actualBytes: error.actualBytes,
            maxBytes: error.maxBytes,
          });
          return errorResponse(413, 'Audio too large for provider',
            `${PROVIDER_NAMES[current]} accepts at most ${Math.round(error.maxBytes / (1024 * 1024))} MB inline. Your audio is ${(error.actualBytes / (1024 * 1024)).toFixed(2)} MB.`,
            { requestId, provider: current, max_size_mb: Math.round(error.maxBytes / (1024 * 1024)), actual_size_mb: parseFloat((error.actualBytes / (1024 * 1024)).toFixed(2)) },
          );
        }
        if (error instanceof UnsupportedAudioFormatError) {
          logEvent(requestId, startTime, 'transcribe.request_fail', {
            provider: current,
            attempt: index + 1,
            kind: 'unsupported_audio_format',
            message: error.message,
            receivedContentType: error.contentType,
            acceptedFormats: error.acceptedFormats,
          });
          return errorResponse(415, 'Unsupported audio format for provider',
            `${PROVIDER_NAMES[current]} accepts only ${error.acceptedFormats.join(', ')}. Received Content-Type: ${error.contentType}.`,
            {
              requestId,
              provider: current,
              received_content_type: error.contentType,
              accepted_formats: error.acceptedFormats,
            },
          );
        }
        // Non-retryable error (401 invalid key, etc.) — don't try fallbacks
        logEvent(requestId, startTime, 'transcribe.request_fail', {
          provider: current,
          attempt: index + 1,
          kind: 'non_retryable',
          message: error instanceof Error ? error.message : String(error),
        });
        return errorResponse(500, 'Transcription failed', error instanceof Error ? error.message : String(error), { requestId });
      }
    }
  } finally {
    // Fire-and-forget, like the credit deduction below: the whole attempt chain
    // goes in one POST, and a slow or failing website must never delay a
    // transcript. In a finally so EVERY path out of the loop reports — the
    // early returns above included — and so it happens exactly once.
    //
    // The opt-out is checked here rather than at recordAttempt: an opted-out
    // request still collects samples, it just never sends them, so they die
    // with the request. Gating the single send is what makes the opt-out
    // impossible to leak past — there is no second way out of this loop.
    if (!latencyOptOut) {
      reportLatencySamples(latencySamples);
    }
  }

  // All providers in the chain failed.
  if (!result) {
    // Every provider rejected the input with a non-auth 4xx and none was merely
    // unavailable — the input itself is the problem, so a retry won't help.
    // Surface a 400 with the upstream message instead of a misleading 429/502
    // ("rate-limited"/"unavailable") that would have the client back off and
    // retry the same bad request. (issue ray-amjad/hyperwhisper#333)
    if (lastInputError && !sawUnavailable) {
      logEvent(requestId, startTime, 'transcribe.request_fail', {
        kind: 'all_providers_rejected_input',
        provider,
        fallbackCount,
        status: lastInputError.status,
        message: lastInputError.message,
      });
      return errorResponse(400, 'Transcription input rejected',
        `No transcription provider accepted this request: ${lastInputError.message}`,
        { requestId, provider },
      );
    }

    // Self-only chains (e.g. azure-mai, google-chirp) mean the user explicitly
    // opted into a single upstream. Surfacing a 429 implies "we'll retry
    // through siblings, just back off" — which is a lie when there are no
    // siblings. Return 502 with the upstream's actual error message so client
    // retry logic doesn't storm against a broken region.
    const isSelfOnlyChain = chain.length === 1;
    if (isSelfOnlyChain) {
      logEvent(requestId, startTime, 'transcribe.request_fail', {
        kind: 'self_only_chain_failed',
        provider,
        fallbackCount,
        attemptFailures,
        message: lastError?.message,
      });
      return errorResponse(502, `${PROVIDER_NAMES[provider]} unavailable`,
        lastError?.message ?? `${PROVIDER_NAMES[provider]} is currently unavailable. Please try again shortly.`,
        { requestId, provider },
      );
    }

    logEvent(requestId, startTime, 'transcribe.request_fail', {
      kind: 'all_providers_unavailable',
      fallbackCount,
      attemptFailures,
      message: lastError?.message,
    });
    return errorResponse(429, 'All providers unavailable', 'All transcription providers are currently rate-limited. Please try again shortly.', { requestId });
  }
  logEvent(requestId, startTime, 'transcribe.stt_done', {
    provider: result.source,
    upstreamRequestId: result.requestId,
  });

  const resultProvider: Provider = result.source === 'no_speech' ? provider : (result.source as Provider);
  const actualProvider = formatProviderName(resultProvider, usedModel);
  const providerName = fallbackFrom
    ? `${actualProvider} (fallback from ${formatProviderName(fallbackFrom, model)})`
    : actualProvider;

  const noSpeech = result.source === 'no_speech';
  const creditsUsed = noSpeech ? 0 : creditsForCost(result.costUsd);

  if (!noSpeech) {
    deductCredits(
      authResult.value,
      result.costUsd,
      {
        audio_duration_seconds: result.durationSeconds,
        transcription_cost_usd: result.costUsd,
        language: result.language ?? language ?? 'auto',
        mode,
        endpoint: '/transcribe',
        stt_provider: providerName,
        stt_model: usedModel || undefined,
      },
      clientIP
    ).catch(console.error);
  }

  const response = {
    text: result.text,
    language: result.language,
    duration: result.durationSeconds,
    cost: {
      usd: result.costUsd,
      credits: creditsUsed,
    },
    metadata: {
      request_id: requestId,
      stt_provider: providerName,
      stt_model: usedModel || undefined,
    },
    ...(noSpeech ? { no_speech_detected: true } : {}),
  };

  c.header('X-Request-ID', requestId);
  c.header('X-STT-Provider', providerName);
  if (usedModel) {
    c.header('X-STT-Model', usedModel);
  }
  c.header('X-Total-Cost-Usd', formatUsd(result.costUsd));
  c.header('X-Credits-Used', creditsUsed.toFixed(1));

  const memUsageMb = Math.round(process.memoryUsage().rss / 1024 / 1024);
  logEvent(requestId, startTime, 'transcribe.request_done', {
    clientPlatform,
    clientVersion,
    finalProvider: providerName,
    fallbackCount,
    // On a degraded success (fallbackCount > 0) this names which provider(s)
    // failed and why, so a slow-but-successful transcription is diagnosable
    // from the single outcome line.
    ...(attemptFailures.length ? { attemptFailures } : {}),
    noSpeech,
    creditsUsed,
    flyMachineId: process.env.FLY_MACHINE_ID,
    // Region on the outcome line makes the Axiom dataset queryable by region on
    // its own, without joining against the machine id.
    flyRegion: process.env.FLY_REGION || 'local',
    // Only present when the caller opted out, so the field's absence is the
    // normal case. Without it, a thin /latency dataset looks like a bug.
    ...(latencyOptOut ? { latencyOptOut: true } : {}),
    machineUptimeMs: machineUptimeMs(),
    rssMb: memUsageMb,
  });
  return c.json(response);
}
