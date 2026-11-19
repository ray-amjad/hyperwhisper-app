import type { Context } from 'hono';
import { readClientInfo } from '../lib/client-info';
import { planGeoRouting } from '../providers/geo-availability';
import { rejectRequestOptions } from '../providers/request-options';
import { flyProxyOverheadMs, logEvent } from '../lib/logging';
import { rawQuery } from '../lib/query';
import { readAccountKey } from '../lib/account-key';
import { generateRequestId, getFlyRequestId } from '../lib/request-id';
import { errorResponse } from '../lib/responses';
import {
  MEDICAL_DOMAIN,
  resolveModel,
  type SttProviderId,
} from '../lib/stt-models';
import {
  authDiagnosticsForLog,
  type AuthContext,
} from '../middleware/auth';
import { openRequestGate } from '../middleware/request-gate';
import { prepareTranscriptionAudio } from './transcribe-audio';
import {
  extractDomain,
  extractModel,
  extractProvider,
  isLatencyOptOut,
  validateStreamingHeaders,
} from './transcribe-request';
import { clientOffersLatencyOptOut } from '../lib/latency-eligibility';

export interface PreparedTranscriptionRequest {
  requestId: string;
  startTime: number;
  clientIP: string;
  provider: SttProviderId;
  model: string;
  domain: string | undefined;
  contentType: string;
  language: string | undefined;
  initialPrompt: string | undefined;
  mode: string | undefined;
  clientPlatform: string;
  clientVersion: string;
  auth: AuthContext;
  audioBuffer: ArrayBuffer;
  latencyOptOut: boolean;
  latencyReportable: boolean;
}

/**
 * Validate and prepare one transcription request before provider routing starts.
 * Early failures keep their existing response and stop before the audio buffer
 * reaches the fallback chain.
 */
export async function prepareTranscriptionRequest(
  c: Context,
): Promise<PreparedTranscriptionRequest | Response> {
  const requestId = generateRequestId();
  const startTime = performance.now();
  const flyRequestId = getFlyRequestId(c);

  // IP block now; auth + the per-key rate limit after the header checks.
  const gate = await openRequestGate(c, { endpoint: '/transcribe' });
  if (!gate.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', { reason: gate.reason, flyRequestId });
    return gate.response;
  }
  const clientIP = gate.clientIP;
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
  // Per-model option rules (language, domain) live in the providers layer; the
  // route only turns a rejection into the 400. See providers/request-options.ts.
  const optionsRejection = rejectRequestOptions(provider, model, { language, domain });
  if (optionsRejection !== null) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: 'invalid_dictation_options',
      flyRequestId,
      // Caller-supplied query value, so cap what reaches the log.
      language: language?.slice(0, 35),
      hasDomain: Boolean(domain),
    });
    return errorResponse(400, 'Invalid Dictation options', optionsRejection, { requestId });
  }

  // Some providers are unreachable from the region this machine runs in. The
  // providers layer owns which ones, from where, and where to send the request
  // instead — the route only carries out the plan it is handed, before doing
  // any auth/credit work. A replay adds ~50-80ms vs ~6s of certain failure.
  // See providers/geo-availability.ts.
  const geoPlan = planGeoRouting(provider, contentLength);
  if (geoPlan.action === 'replay') {
    logEvent(requestId, startTime, 'transcribe.fly_replay', {
      flyRequestId,
      provider,
      fromRegion: geoPlan.fromRegion,
      toRegion: geoPlan.toRegion,
      reason: geoPlan.reason,
    });
    c.header('fly-replay', `region=${geoPlan.toRegion}`);
    return c.body(null, 200);
  }
  // The body was too large for Fly to replay, so the request stays in this
  // region and the unreachable provider comes out of the chain below.
  if (geoPlan.action === 'drop_from_chain') {
    logEvent(requestId, startTime, 'transcribe.fly_replay_skipped_oversized', {
      flyRequestId,
      provider,
      flyRegion: geoPlan.fromRegion,
      contentLength,
      replayMaxBytes: geoPlan.replayMaxBytes,
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
  // Per-key rate limit after auth, before the audio is buffered or any
  // credits are held (middleware/rate-limit.ts, #2010).
  const admission = await gate.admitKey(readAccountKey((field) => rawQuery(c.req.url, field)));
  if (!admission.ok) {
    logEvent(requestId, startTime, 'transcribe.request_rejected', {
      reason: admission.reason,
      flyRequestId,
      status: admission.response.status,
      ...(admission.reason === 'auth_failed' ? authDiagnosticsForLog(admission.authDiagnostics) : {}),
    });
    return admission.response;
  }
  logEvent(requestId, startTime, 'transcribe.auth_done', authDiagnosticsForLog(admission.authDiagnostics));

  const audioPreparation = await prepareTranscriptionAudio({
    c,
    requestId,
    startTime,
    flyRequestId,
    provider,
    contentType,
    contentLength,
    model,
    medical,
    initialPrompt,
    language,
    auth: admission.auth,
    clientIP,
  });
  if (!audioPreparation.ok) {
    return audioPreparation.response;
  }

  const latencyOptOut = isLatencyOptOut(c);
  const latencyEligibleClient = clientOffersLatencyOptOut(clientPlatform, clientVersion);

  return {
    requestId,
    startTime,
    clientIP,
    provider,
    model,
    domain,
    contentType,
    language,
    initialPrompt,
    mode,
    clientPlatform,
    clientVersion,
    auth: admission.auth,
    audioBuffer: audioPreparation.audioBuffer,
    latencyOptOut,
    latencyReportable: !latencyOptOut && latencyEligibleClient,
  };
}
