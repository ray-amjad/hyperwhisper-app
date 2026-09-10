// SHARED OPENAI-COMPATIBLE CHAT COMPLETION CLIENT
//
// groq/cerebras/xai/mistral/openai/gemini all expose an OpenAI-compatible
// `/chat/completions` endpoint and follow the same fetch -> check-ok ->
// parse -> cost sequence; this factors out that plumbing. Per-provider API
// key resolution, missing-key error shape, and request-body construction
// stay in each provider's own file so provider-specific quirks (fixed vs
// allowlisted model, tagged vs untagged missing-key errors, extra body
// fields) are unchanged.

import { isRecord, safeReadText } from '../lib/utils';
import { estimateUsageFromChars, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './llm-contract';

export type OpenAICompatChatResult = { raw: unknown; usage?: GroqUsage; costUsd: number };

export type OpenAICompatChatConfig = {
  baseUrl: string;
  apiKey: string;
  providerTag: string;
  errorLogLabel: string;
  errorChatLabel: string;
  buildBody: (payload: CorrectionRequestPayload, model: string) => Record<string, unknown>;
  computeCost: (usage: GroqUsage) => number;
};

// Fail-closed fallback for vendor usage-schema drift: estimate tokens from
// character counts so a missing/unrecognized `usage` block is billed instead
// of silently costing 0.
function reportMissingUsage(
  provider: string,
  payload: CorrectionRequestPayload,
  json: unknown,
  requestId: string
): GroqUsage {
  const promptChars = payload.messages.reduce((sum, message) => sum + message.content.length, 0);

  let completionChars = 0;
  if (isRecord(json)) {
    const choices = json['choices'];
    const message = Array.isArray(choices) && isRecord(choices[0]) ? choices[0]['message'] : undefined;
    const content = isRecord(message) ? message['content'] : undefined;
    completionChars = typeof content === 'string' ? content.length : JSON.stringify(json).length;
  }

  const estimatedUsage = estimateUsageFromChars(promptChars, completionChars);
  console.warn('LLM response missing/unrecognized usage; billing char-based estimate', {
    requestId,
    provider,
    estimatedUsage,
  });

  return estimatedUsage;
}

export async function requestOpenAICompatibleChat(
  config: OpenAICompatChatConfig,
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<OpenAICompatChatResult> {
  const response = await fetch(`${config.baseUrl}/chat/completions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${config.apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify(config.buildBody(payload, model)),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error(`${config.errorLogLabel} returned error`, {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`${config.errorChatLabel} failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = config.providerTag;
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = config.computeCost(usage ?? reportMissingUsage(config.providerTag, payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
