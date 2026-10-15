// GEMINI LLM CLIENT (OPENAI-COMPATIBLE CHAT COMPLETIONS)

import { computeGeminiChatCost, type GroqUsage } from '../lib/cost-calculator';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

// Gemini exposes an OpenAI-compatible surface that accepts the shared chat
// payload unchanged. Verified 2026-06-19.
const GEMINI_BASE_URL = 'https://generativelanguage.googleapis.com/v1beta/openai';

// The 2.5-flash and 3.8-flash families default to a dynamic thinking budget and
// bill thinking tokens as output — wasted latency/cost for simple text cleanup —
// so each is sent the lowest reasoning_effort it accepts. Listed per family rather
// than matched on a generic /gemini-\d+.*flash/ so an id we have not checked (e.g.
// a future 3.x flash, or any -pro id) keeps its own default instead of silently
// inheriting one of these. First match wins, so keep a longer prefix above a
// shorter one it extends.
//
// - 2.5-flash: 'none' turns thinking off.
// - 3.8-flash: thinking cannot be turned off; its levels are low/medium/high
//   (https://ai.google.dev/gemini-api/docs/thinking, read 2026-10-07). Measured on
//   the OpenAI-compat surface 2026-10-15: 'minimal' is a 400 ("Thinking level
//   MINIMAL is not supported for this model"), while 'none' is a 200 that still
//   thinks — about the same hidden tokens as 'low'. So send 'low', the floor the
//   docs name, rather than rely on Google quietly remapping 'none'. A 400 here
//   would NOT fall back: shouldFallback() fails over on a 5xx or 429 only. This
//   matches the transcription client (gemini.ts thinkingConfig).
const REASONING_EFFORT_BY_PREFIX: ReadonlyArray<readonly [prefix: string, effort: string]> = [
  ['gemini-2.5-flash', 'none'],
  ['gemini-3.8-flash', 'low'],
];

/** The reasoning_effort to send for a model id, or undefined to send none. */
function geminiReasoningEffort(model: string): string | undefined {
  return REASONING_EFFORT_BY_PREFIX.find(([prefix]) => model.startsWith(prefix))?.[1];
}

export async function requestGeminiChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.GEMINI_API_KEY || process.env.GOOGLE_GEMINI_API_KEY;
  if (!apiKey) {
    throw new LLMRequestError('GEMINI_API_KEY not configured', 503, 'gemini');
  }

  return requestOpenAICompatibleChat(
    {
      baseUrl: GEMINI_BASE_URL,
      apiKey,
      providerTag: 'gemini',
      errorLogLabel: 'Gemini API',
      errorChatLabel: 'Gemini chat',
      buildBody: (body, requestModel) => {
        // Lowest accepted thinking level per family; see REASONING_EFFORT_BY_PREFIX.
        const reasoningEffort = geminiReasoningEffort(requestModel);
        return {
          model: requestModel,
          ...body,
          ...(reasoningEffort ? { reasoning_effort: reasoningEffort } : {}),
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
