// MISTRAL LLM CLIENT (CHAT COMPLETIONS)

import { computeMistralChatCost, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { isRecord, safeReadText } from '../lib/utils';
import { reportMissingUsage, type CorrectionRequestPayload } from './groq-llm';

// Mistral's chat/completions accepts the standard chat payload (messages,
// temperature: 0, max_tokens) unchanged. Verified 2026-06-19.
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

  const response = await fetch(`${MISTRAL_BASE_URL}/chat/completions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      model,
      ...payload,
      stream: false,
    }),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error('Mistral API returned error', {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`Mistral chat failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = 'mistral';
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = computeMistralChatCost(model, usage ?? reportMissingUsage('mistral', payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
