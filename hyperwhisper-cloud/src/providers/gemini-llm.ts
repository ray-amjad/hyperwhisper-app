// GEMINI LLM CLIENT (OPENAI-COMPATIBLE CHAT COMPLETIONS)

import { computeGeminiChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './groq-llm';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

// Gemini exposes an OpenAI-compatible surface that accepts the shared chat
// payload unchanged. Verified 2026-06-19.
const GEMINI_BASE_URL = 'https://generativelanguage.googleapis.com/v1beta/openai';

export async function requestGeminiChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.GEMINI_API_KEY || process.env.GOOGLE_GEMINI_API_KEY;
  if (!apiKey) {
    const error = new Error('GEMINI_API_KEY not configured');
    (error as { status?: number; provider?: string }).status = 503;
    (error as { provider?: string }).provider = 'gemini';
    throw error;
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: GEMINI_BASE_URL,
      apiKey,
      providerTag: 'gemini',
      errorLogLabel: 'Gemini API',
      errorChatLabel: 'Gemini chat',
      buildBody: (body, requestModel) => {
        // The 2.5-flash family defaults to a dynamic thinking budget and bills
        // thinking tokens as output — wasted latency/cost for simple text cleanup.
        // Gemini's OpenAI-compat surface disables thinking via reasoning_effort: 'none'.
        const disableThinking = requestModel.startsWith('gemini-2.5-flash');
        return {
          model: requestModel,
          ...body,
          ...(disableThinking ? { reasoning_effort: 'none' } : {}),
          stream: false,
        };
      },
      computeCost: (usage) => computeGeminiChatCost(model, usage),
    },
    payload,
    requestId,
    model
  );
}
