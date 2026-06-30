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
import { transcribeWithAssemblyAI } from '../providers/assemblyai';
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
  type SttProviderId,
} from '../lib/stt-models';
import { generateRequestId, getClientIP, getFlyRequestId } from '../lib/request-id';
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
): number {
  const chain = FALLBACK_CHAINS[provider];
  const estimatedSeconds = estimateAudioSecondsFromSize(sizeBytes);
  const hasInitialPrompt = Boolean(initialPrompt);
  const usdPerMinute = Math.max(
    ...chain.map((p) => estimatedUsdPerMinute(
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
    )),
  );
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
  return c.req.header('X-STT-Model')?.trim() || c.req.query('model')?.trim() || undefined;
}

function extractDomain(c: Context): string | undefined {
  const domain = c.req.header('X-STT-Domain')?.toLowerCase().trim();
  return domain || undefined;
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
  const language = c.req.query('language') || undefined;
  const initialPrompt = c.req.query('initial_prompt') || undefined;
  const mode = c.req.query('mode') || undefined;

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
  logEvent(requestId, startTime, 'transcribe.request_start', {
    flyRequestId,
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
      c.req.query('account_key') || c.req.query('license_key') || undefined,
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
  const estimatedCredits = estimateCreditsForProviderFallbacks(contentLength, provider, model, medical, initialPrompt);
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

    try {
      result = await PROVIDER_FN[current](audioBuffer, contentType, language, initialPrompt, {
        requestId,
        attempt: index + 1,
        model: attemptModel,
        domain: attemptDomain,
      });
      // Prefer the model the adapter reports it ACTUALLY ran (e.g. AssemblyAI's
      // universal-3-pro → universal-2 fallback for unsupported languages) so the
      // X-STT-Model header and deduction metadata match what was billed; fall
      // back to the attempted model when the adapter doesn't report one.
      usedModel = result.model || attemptModel;
      if (current !== provider) {
        fallbackFrom = provider;
      }
      logEvent(requestId, startTime, 'transcribe.provider_attempt_done', {
        provider: current,
        model: attemptModel || 'default',
        attempt: index + 1,
        upstreamRequestId: result.requestId,
        transcriptChars: result.text.length,
        resultSource: result.source,
      });
      break;
    } catch (error) {
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
    finalProvider: providerName,
    fallbackCount,
    // On a degraded success (fallbackCount > 0) this names which provider(s)
    // failed and why, so a slow-but-successful transcription is diagnosable
    // from the single outcome line.
    ...(attemptFailures.length ? { attemptFailures } : {}),
    noSpeech,
    creditsUsed,
    flyMachineId: process.env.FLY_MACHINE_ID,
    machineUptimeMs: machineUptimeMs(),
    rssMb: memUsageMb,
  });
  return c.json(response);
}
