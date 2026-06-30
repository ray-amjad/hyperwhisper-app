// OPENAI LLM CLIENT (CHAT COMPLETIONS)

import { computeOpenAIChatCost, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { isRecord, safeReadText } from '../lib/utils';
import { reportMissingUsage, type CorrectionRequestPayload } from './groq-llm';

const OPENAI_BASE_URL = 'https://api.openai.com/v1';

/**
 * GPT-5 family on /v1/chat/completions diverges from the shared OpenAI chat
 * payload that buildCorrectionRequest() produces:
 *   - `max_tokens` is rejected → must be renamed to `max_completion_tokens`;
 *   - only the default `temperature` (1) is supported → `temperature: 0` errors,
 *     so we drop it entirely.
 * `reasoning_effort: 'minimal'` is the lowest-latency setting GPT-5 accepts and
 * keeps post-processing fast/cheap. Verified against OpenAI docs 2026-06-19.
 */
function buildOpenAIBody(payload: CorrectionRequestPayload, model: string): Record<string, unknown> {
  const { temperature, max_tokens, ...rest } = payload;
  return {
    model,
    ...rest,
    max_completion_tokens: max_tokens,
    reasoning_effort: 'minimal',
    stream: false,
  };
}

export async function requestOpenAIChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.OPENAI_API_KEY;
  if (!apiKey) {
    const error = new Error('OPENAI_API_KEY not configured');
    (error as { status?: number; provider?: string }).status = 503;
    (error as { provider?: string }).provider = 'openai';
    throw error;
  }

  const response = await fetch(`${OPENAI_BASE_URL}/chat/completions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify(buildOpenAIBody(payload, model)),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error('OpenAI API returned error', {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`OpenAI chat failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = 'openai';
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = computeOpenAIChatCost(model, usage ?? reportMissingUsage('openai', payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
