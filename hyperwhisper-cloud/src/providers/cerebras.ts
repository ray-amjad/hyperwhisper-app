// CEREBRAS LLM CLIENT

import { computeCerebrasChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './llm-contract';
import { requireLLMApiKey } from './llm-errors';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const CEREBRAS_BASE_URL = 'https://api.cerebras.ai/v1';
const CEREBRAS_CHAT_MODEL = 'gpt-oss-120b';

export async function requestCerebrasChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = requireLLMApiKey('cerebras', 'CEREBRAS_API_KEY');

  return requestOpenAICompatibleChat(
    {
      baseUrl: CEREBRAS_BASE_URL,
      apiKey,
      providerTag: 'cerebras',
      errorLogLabel: 'Cerebras API',
      errorChatLabel: 'Cerebras chat',
      buildBody: (body, model) => ({ model, ...body, reasoning_effort: 'low', stream: false }),
      computeCost: computeCerebrasChatCost,
    },
    payload,
    requestId,
    CEREBRAS_CHAT_MODEL
  );
}
