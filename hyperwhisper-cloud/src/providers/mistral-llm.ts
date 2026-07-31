// MISTRAL LLM CLIENT (CHAT COMPLETIONS)

import { computeMistralChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './groq-llm';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

// Mistral's chat/completions accepts the shared chat payload unchanged.
// Verified 2026-06-19.
const MISTRAL_BASE_URL = 'https://api.mistral.ai/v1';

export async function requestMistralChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.MISTRAL_API_KEY;
  if (!apiKey) {
    const error = new Error('MISTRAL_API_KEY not configured');
    (error as { status?: number; provider?: string }).status = 503;
    (error as { provider?: string }).provider = 'mistral';
    throw error;
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: MISTRAL_BASE_URL,
      apiKey,
      providerTag: 'mistral',
      errorLogLabel: 'Mistral API',
      errorChatLabel: 'Mistral chat',
      buildBody: (body, requestModel) => ({ model: requestModel, ...body, stream: false }),
      computeCost: (usage) => computeMistralChatCost(model, usage),
    },
    payload,
    requestId,
    model
  );
}
