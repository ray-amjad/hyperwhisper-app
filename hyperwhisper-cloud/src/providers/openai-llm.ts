// OPENAI LLM CLIENT (CHAT COMPLETIONS)

import { computeOpenAIChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const OPENAI_BASE_URL = 'https://api.openai.com/v1';

/**
 * GPT-5 family on /v1/chat/completions diverges from the shared OpenAI chat
 * payload that buildCorrectionRequest() produces:
 *   - only the default `temperature` (1) is supported → `temperature: 0` errors,
 *     so we drop it entirely.
 * `reasoning_effort: 'none'` is the lowest-latency setting gpt-5.6-luna accepts
 * (none|low|medium|high|xhigh|max, default medium; `'minimal'` is NOT accepted)
 * and keeps post-processing fast/cheap. Verified against the OpenAI model doc
 * 2026-09-25: https://developers.openai.com/api/docs/models/gpt-5.6-luna
 */
export function buildOpenAIBody(payload: CorrectionRequestPayload, model: string): Record<string, unknown> {
  const { temperature, ...rest } = payload;
  return {
    model,
    ...rest,
    reasoning_effort: 'none',
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
    throw new LLMRequestError('OPENAI_API_KEY not configured', 503, 'openai');
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
