// GEMINI LLM CLIENT (OPENAI-COMPATIBLE CHAT COMPLETIONS)

import { computeGeminiChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './groq-llm';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

// Gemini exposes an OpenAI-compatible surface that accepts the shared chat
// payload unchanged. Verified 2026-06-19.
const GEMINI_BASE_URL = 'https://generativelanguage.googleapis.com/v1beta/openai';

// The 2.5-flash and 3.8-flash families default to a dynamic thinking budget and
// bill thinking tokens as output — wasted latency/cost for simple text cleanup.
// Gemini's OpenAI-compat surface disables thinking via reasoning_effort: 'none'.
// Listed per family rather than matched on a generic /gemini-\d+.*flash/ so an id
// we have not checked (e.g. a future 3.x flash, or any -pro id) keeps its own
// default instead of silently inheriting this one.
//
// UNVERIFIED for 3.8: the sibling transcription client (gemini.ts thinkingConfig)
// maps every gemini-3* id to thinkingLevel: 'minimal' and gives a zero budget only
// to 2.5-flash/flash-lite, i.e. this repo already assumes a 3.x model may not
// accept fully disabled thinking. No published source confirms whether the
// OpenAI-compat surface accepts reasoning_effort: 'none' for a 3.x id, and a 400
// here would NOT fall back — shouldFallback() retries 5xx only.
const THINKING_DISABLED_PREFIXES = ['gemini-2.5-flash', 'gemini-3.8-flash'] as const;

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
        // Thinking off for the families listed in THINKING_DISABLED_PREFIXES; see
        // the constant for why the list is explicit and what is unverified on 3.8.
        const disableThinking = THINKING_DISABLED_PREFIXES.some((prefix) =>
          requestModel.startsWith(prefix)
        );
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
