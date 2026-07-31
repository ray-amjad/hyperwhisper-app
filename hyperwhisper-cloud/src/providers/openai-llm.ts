// OPENAI LLM CLIENT (CHAT COMPLETIONS)

import { computeOpenAIChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './groq-llm';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const OPENAI_BASE_URL = 'https://api.openai.com/v1';

/**
 * GPT-5 family on /v1/chat/completions diverges from the shared OpenAI chat
 * payload that buildCorrectionRequest() produces:
 *   - only the default `temperature` (1) is supported → `temperature: 0` errors,
 *     so we drop it entirely.
 * `reasoning_effort: 'minimal'` is the lowest-latency setting GPT-5 accepts and
 * keeps post-processing fast/cheap. Verified against OpenAI docs 2026-06-19.
 */
export function buildOpenAIBody(payload: CorrectionRequestPayload, model: string): Record<string, unknown> {
  const { temperature, ...rest } = payload;
  return {
    model,
    ...rest,
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

  return requestOpenAICompatibleChat(
    {
      baseUrl: OPENAI_BASE_URL,
      apiKey,
      providerTag: 'openai',
      errorLogLabel: 'OpenAI API',
      errorChatLabel: 'OpenAI chat',
      buildBody: buildOpenAIBody,
      computeCost: (usage) => computeOpenAIChatCost(model, usage),
    },
    payload,
    requestId,
    model
  );
}
