// CEREBRAS LLM CLIENT

import { computeCerebrasChatCost, type GroqUsage } from '../lib/cost-calculator';
import { CEREBRAS_MAX_COMPLETION_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './groq-llm';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const CEREBRAS_BASE_URL = 'https://api.cerebras.ai/v1';
const CEREBRAS_CHAT_MODEL = 'gpt-oss-120b';

export async function requestCerebrasChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.CEREBRAS_API_KEY;
  if (!apiKey) {
    throw new Error('CEREBRAS_API_KEY not configured');
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: CEREBRAS_BASE_URL,
      apiKey,
      providerTag: 'cerebras',
      errorLogLabel: 'Cerebras API',
      errorChatLabel: 'Cerebras chat',
      buildBody: (body, model) => ({
        model,
        ...body,
        reasoning_effort: 'low',
        stream: false,
        max_completion_tokens: CEREBRAS_MAX_COMPLETION_TOKENS,
      }),
      computeCost: computeCerebrasChatCost,
    },
    payload,
    requestId,
    CEREBRAS_CHAT_MODEL
  );
}
