// GROQ LLM CLIENT (CHAT COMPLETIONS)

import { computeGroqChatCost, type GroqUsage } from '../lib/cost-calculator';
import { GROQ_MAX_COMPLETION_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './llm-contract';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const GROQ_BASE_URL = 'https://api.groq.com/openai/v1';
const GROQ_CHAT_MODEL = 'openai/gpt-oss-120b';

/**
 * Groq is the one hosted provider that needs an explicit output ceiling: its
 * default cap is too low for a cleaned transcript, and openai/gpt-oss-120b
 * spends reasoning tokens from the same budget, so omitting it returns
 * finish_reason=length on long dictations. See GROQ_MAX_COMPLETION_TOKENS.
 */
export function buildGroqBody(payload: CorrectionRequestPayload, model: string): Record<string, unknown> {
  return {
    model,
    ...payload,
    max_completion_tokens: GROQ_MAX_COMPLETION_TOKENS,
    reasoning_effort: 'low',
    stream: false,
  };
}

export async function requestGroqChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.GROQ_API_KEY;
  if (!apiKey) {
    throw new Error('GROQ_API_KEY not configured');
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: GROQ_BASE_URL,
      apiKey,
      providerTag: 'groq',
      errorLogLabel: 'Groq LLM API',
      errorChatLabel: 'Groq chat',
      buildBody: buildGroqBody,
      computeCost: computeGroqChatCost,
    },
    payload,
    requestId,
    GROQ_CHAT_MODEL
  );
}
