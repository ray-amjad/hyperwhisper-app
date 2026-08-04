// GROQ LLM CLIENT (CHAT COMPLETIONS)

import { computeGroqChatCost, estimateUsageFromChars, type GroqUsage } from '../lib/cost-calculator';
import { GROQ_MAX_COMPLETION_TOKENS } from '../lib/llm-token-limits';
import { isRecord } from '../lib/utils';
import { requestOpenAICompatibleChat } from './openai-compat-chat';

const GROQ_BASE_URL = 'https://api.groq.com/openai/v1';
const GROQ_CHAT_MODEL = 'openai/gpt-oss-120b';

export type ChatMessage = {
  role: 'system' | 'user' | 'assistant' | 'tool';
  content: string;
};

export type CorrectionRequestPayload = {
  messages: ChatMessage[];
  temperature: number;
};

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
      buildBody: (body, model) => ({
        model,
        ...body,
        reasoning_effort: 'low',
        stream: false,
        max_completion_tokens: GROQ_MAX_COMPLETION_TOKENS,
      }),
      computeCost: computeGroqChatCost,
    },
    payload,
    requestId,
    GROQ_CHAT_MODEL
  );
}

// Fail-closed fallback for vendor usage-schema drift: estimate tokens from
// character counts so a missing/unrecognized `usage` block is billed instead
// of silently costing 0.
export function reportMissingUsage(
  provider: string,
  payload: CorrectionRequestPayload,
  json: unknown,
  requestId: string
): GroqUsage {
  const promptChars = payload.messages.reduce((sum, message) => sum + message.content.length, 0);

  let completionChars = 0;
  if (isRecord(json)) {
    const choices = json['choices'];
    const message = Array.isArray(choices) && isRecord(choices[0]) ? choices[0]['message'] : undefined;
    const content = isRecord(message) ? message['content'] : undefined;
    completionChars = typeof content === 'string' ? content.length : JSON.stringify(json).length;
  }

  const estimatedUsage = estimateUsageFromChars(promptChars, completionChars);
  console.warn('LLM response missing/unrecognized usage; billing char-based estimate', {
    requestId,
    provider,
    estimatedUsage,
  });

  return estimatedUsage;
}

export function buildCorrectionRequest(systemPrompt: string, userContent: string): CorrectionRequestPayload {
  return {
    messages: [
      { role: 'system', content: systemPrompt },
      { role: 'user', content: userContent },
    ],
    temperature: 0,
  };
}
