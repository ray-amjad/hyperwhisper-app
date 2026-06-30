// GEMINI LLM CLIENT (OPENAI-COMPATIBLE CHAT COMPLETIONS)

import { computeGeminiChatCost, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { isRecord, safeReadText } from '../lib/utils';
import { reportMissingUsage, type CorrectionRequestPayload } from './groq-llm';

// Gemini exposes an OpenAI-compatible surface that accepts the standard chat
// payload (messages, temperature: 0, max_tokens) unchanged. Verified 2026-06-19.
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

  // The 2.5-flash family defaults to a dynamic thinking budget and bills
  // thinking tokens as output — wasted latency/cost for simple text cleanup.
  // Gemini's OpenAI-compat surface disables thinking via reasoning_effort: 'none'.
  const disableThinking = model.startsWith('gemini-2.5-flash');

  const response = await fetch(`${GEMINI_BASE_URL}/chat/completions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      model,
      ...payload,
      ...(disableThinking ? { reasoning_effort: 'none' } : {}),
      stream: false,
    }),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error('Gemini API returned error', {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`Gemini chat failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = 'gemini';
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = computeGeminiChatCost(model, usage ?? reportMissingUsage('gemini', payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
