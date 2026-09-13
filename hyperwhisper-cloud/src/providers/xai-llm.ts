// XAI GROK LLM CLIENT (CHAT COMPLETIONS)

import { computeXaiGrokFastChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const XAI_BASE_URL = 'https://api.x.ai/v1';
const XAI_GROK_FAST_MODEL = 'grok-4.3';

export async function requestXaiGrokChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.XAI_API_KEY || process.env.GROK_API_KEY;
  if (!apiKey) {
    throw new LLMRequestError('XAI_API_KEY not configured', 503, 'grok');
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: XAI_BASE_URL,
      apiKey,
      providerTag: 'grok',
      errorLogLabel: 'SpaceXAI Grok API',
      errorChatLabel: 'SpaceXAI Grok chat',
      buildBody: (body, model) => ({ model, reasoning_effort: 'none', ...body, stream: false }),
      computeCost: computeXaiGrokFastChatCost,
    },
    payload,
    requestId,
    XAI_GROK_FAST_MODEL
  );
}
