// POST-PROCESS ROUTE
// POST /post-process - standalone text correction via LLM

import type { Context } from 'hono';
import { defaultModelFor, extractLLMProvider, fallbackProviderFor, servedLLMName, callWithRetry, resolveLLMModel, shouldFallback, type LLMProvider } from '../lib/llm-provider';
import { generateRequestId, getClientIP } from '../lib/request-id';
import { buildTranscriptUserContent, containsPromptLeakage, extractCorrectedText, stripCleanMarkers } from '../lib/text-processing';
import { buildCorrectionRequest } from '../providers/groq-llm';
import { creditsForCost, formatUsd } from '../lib/cost-calculator';
import { isIPBlocked } from '../lib/redis';
import { errorResponse, invalidContentTypeResponse } from '../lib/responses';
import { validateAuth } from '../middleware/auth';
import { deductCredits, validateCredits } from '../middleware/credits';
import { logEvent } from '../lib/logging';

const MAX_TEXT_LENGTH = 100000;
const ESTIMATED_POST_PROCESS_CREDITS = 1.0;

interface PostProcessBody {
  text?: string;
  prompt?: string;
  // `account_key` is the canonical field; `license_key` is the legacy alias that
  // installed native apps still send. Either is accepted.
  account_key?: string;
  license_key?: string;
}

// Per-provider primary retry count. Fast/cheap providers retry more; pricier or
// slower ones retry less to bound latency and spend before falling back.
function retriesFor(provider: LLMProvider): number {
  switch (provider) {
    case 'anthropic': return 2;
    case 'cerebras': return 0;
    case 'grok': return 1;
    case 'openai': return 1;
    case 'gemini': return 2;
    case 'mistral': return 2;
    case 'groq': return 3;
  }
  // Exhaustiveness guard: if a new LLMProvider is added to the union without a
  // case above, TypeScript flags this assignment.
  const _exhaustive: never = provider;
  throw new Error(`Unhandled LLM provider: ${String(_exhaustive)}`);
}

function rawResponseLength(raw: unknown): number | undefined {
  if (typeof raw === 'string') {
    return raw.length;
  }

  try {
    return JSON.stringify(raw).length;
  } catch {
    return undefined;
  }
}

export async function postProcessRoute(c: Context) {
  const requestId = generateRequestId();
  const startTime = performance.now();
  const clientIP = getClientIP(c);

  if (await isIPBlocked(clientIP)) {
    logEvent(requestId, startTime, 'post_process.request_rejected', { reason: 'ip_blocked' });
    return errorResponse(403, 'Access denied', 'Your IP has been temporarily blocked due to abuse');
  }
  logEvent(requestId, startTime, 'post_process.ip_check_done');

  const contentType = c.req.header('Content-Type') || '';
  if (!contentType.includes('application/json')) {
    return invalidContentTypeResponse('application/json', contentType);
  }

  let body: PostProcessBody;
  try {
    body = await c.req.json();
  } catch {
    return errorResponse(400, 'Invalid JSON', 'Request body must be valid JSON');
  }

  const text = typeof body.text === 'string' ? body.text.trim() : '';
  if (!text) {
    return errorResponse(400, 'Missing field', 'Request body must include "text" field');
  }
  if (text.length > MAX_TEXT_LENGTH) {
    return errorResponse(400, 'Text too long', `Text must be ${MAX_TEXT_LENGTH} characters or less`, {
      max_length: MAX_TEXT_LENGTH,
      actual_length: text.length,
    });
  }

  const prompt = typeof body.prompt === 'string' ? body.prompt.trim() : '';
  if (!prompt) {
    return errorResponse(400, 'Missing field', 'Request body must include "prompt" field');
  }

  const provider = extractLLMProvider(c.req.raw);
  const model = resolveLLMModel(provider, c.req.raw);

  logEvent(requestId, startTime, 'post_process.request_start', {
    flyRegion: process.env.FLY_REGION || 'local',
    provider,
    model,
    inputChars: text.length,
    promptChars: prompt.length,
  });

  const authResult = await validateAuth({
    licenseKey: body.account_key || body.license_key,
  });
  if (!authResult.ok) {
    logEvent(requestId, startTime, 'post_process.request_rejected', { reason: 'auth_failed' });
    return authResult.response;
  }
  logEvent(requestId, startTime, 'post_process.auth_done');

  const creditCheck = await validateCredits(authResult.value, ESTIMATED_POST_PROCESS_CREDITS, clientIP);
  if (!creditCheck.ok) {
    logEvent(requestId, startTime, 'post_process.request_rejected', { reason: 'insufficient_credits' });
    return creditCheck.response;
  }
  logEvent(requestId, startTime, 'post_process.credits_done');

  let providerUsed: LLMProvider = provider;
  // The model actually served, tracked alongside providerUsed. Starts as the
  // resolved request model; every fallback/leakage retry routes to the new
  // provider's default, so we update it in lockstep.
  let modelUsed: string = model;

  const userContent = buildTranscriptUserContent(text);
  const payload = buildCorrectionRequest(prompt, userContent);

  let llmResponse: Awaited<ReturnType<typeof callWithRetry>>;

  logEvent(requestId, startTime, 'post_process.llm_attempt_start', { provider, attempt: 1 });

  try {
    const primaryRetries = retriesFor(provider);
    llmResponse = await callWithRetry(provider, payload, requestId, primaryRetries, model);
  } catch (error) {
    logEvent(requestId, startTime, 'post_process.llm_attempt_fail', {
      provider,
      attempt: 1,
      error: error instanceof Error ? error.message : String(error),
    });

    if (shouldFallback(error)) {
      providerUsed = fallbackProviderFor(provider);
      modelUsed = defaultModelFor(providerUsed);
      const fallbackRetries = retriesFor(providerUsed);

      logEvent(requestId, startTime, 'post_process.llm_fallback_start', { requestedProvider: provider, provider: providerUsed });

      try {
        llmResponse = await callWithRetry(providerUsed, payload, requestId, fallbackRetries, modelUsed);
      } catch (fallbackError) {
        logEvent(requestId, startTime, 'post_process.request_fail', {
          reason: 'llm_fallback_failed',
          provider: providerUsed,
          error: fallbackError instanceof Error ? fallbackError.message : String(fallbackError),
        });
        return errorResponse(500, 'Post-processing failed', fallbackError instanceof Error ? fallbackError.message : String(fallbackError), { requestId });
      }
    } else {
      logEvent(requestId, startTime, 'post_process.request_fail', {
        reason: 'llm_failed_no_fallback',
        provider,
        error: error instanceof Error ? error.message : String(error),
      });
      return errorResponse(500, 'Post-processing failed', error instanceof Error ? error.message : String(error), { requestId });
    }
  }

  logEvent(requestId, startTime, 'post_process.llm_attempt_done', {
    provider: providerUsed,
    outputChars: rawResponseLength(llmResponse.raw),
    costUsd: llmResponse.costUsd,
  });

  let correctedText: string;
  let costUsd = llmResponse.costUsd;

  try {
    correctedText = stripCleanMarkers(extractCorrectedText(llmResponse.raw));
  } catch (extractError) {
    // The LLM call already succeeded (and cost us money) — bill the user
    // even though we can't return usable text.
    deductCredits(
      authResult.value,
      costUsd,
      {
        post_processing_cost_usd: costUsd,
        input_length: text.length,
        output_length: 0,
        endpoint: '/post-process',
        llm_provider: providerUsed,
      },
      clientIP
    ).catch(console.error);

    logEvent(requestId, startTime, 'post_process.request_fail', {
      reason: 'extract_failed',
      provider: providerUsed,
      costUsd,
      error: extractError instanceof Error ? extractError.message : String(extractError),
    });
    return errorResponse(500, 'Post-processing failed', extractError instanceof Error ? extractError.message : String(extractError), { requestId });
  }

  if (containsPromptLeakage(correctedText)) {
    logEvent(requestId, startTime, 'post_process.prompt_leakage_detected', {
      provider: providerUsed,
      inputChars: text.length,
      outputChars: correctedText.length,
    });

    const alternateProvider: LLMProvider = fallbackProviderFor(providerUsed);
    const alternateRetries = retriesFor(alternateProvider);

    logEvent(requestId, startTime, 'post_process.llm_leakage_retry_start', { requestedProvider: providerUsed, provider: alternateProvider });

    try {
      const alternateModel = defaultModelFor(alternateProvider);
      const retryResponse = await callWithRetry(alternateProvider, payload, requestId, alternateRetries, alternateModel);
      const retryText = stripCleanMarkers(extractCorrectedText(retryResponse.raw));

      if (containsPromptLeakage(retryText)) {
        logEvent(requestId, startTime, 'post_process.prompt_leakage_persisted', {
          provider: alternateProvider,
          fallbackToRaw: true,
        });
        correctedText = text;
        providerUsed = alternateProvider;
        modelUsed = alternateModel;
        costUsd += retryResponse.costUsd;
      } else {
        correctedText = retryText;
        providerUsed = alternateProvider;
        modelUsed = alternateModel;
        costUsd += retryResponse.costUsd;
      }
    } catch (retryError) {
      logEvent(requestId, startTime, 'post_process.llm_leakage_retry_fail', {
        provider: alternateProvider,
        error: retryError instanceof Error ? retryError.message : String(retryError),
        fallbackToRaw: true,
      });
      correctedText = text;
    }
  }

  const creditsUsed = creditsForCost(costUsd);

  deductCredits(
    authResult.value,
    costUsd,
    {
      post_processing_cost_usd: costUsd,
      input_length: text.length,
      output_length: correctedText.length,
      endpoint: '/post-process',
      llm_provider: providerUsed,
    },
    clientIP
  ).catch(console.error);

  logEvent(requestId, startTime, 'post_process.request_done', {
    finalProvider: servedLLMName(providerUsed, modelUsed),
    inputChars: text.length,
    outputChars: correctedText.length,
    costUsd,
    creditsUsed,
    hadLeakage: correctedText === text && text.length > 0,
  });

  const response = {
    corrected: correctedText,
    cost: {
      usd: costUsd,
      credits: creditsUsed,
    },
  };

  c.header('X-Request-ID', requestId);
  c.header('X-LLM-Provider', servedLLMName(providerUsed, modelUsed));
  c.header('X-Total-Cost-Usd', formatUsd(costUsd));
  c.header('X-Credits-Used', creditsUsed.toFixed(1));

  return c.json(response);
}
