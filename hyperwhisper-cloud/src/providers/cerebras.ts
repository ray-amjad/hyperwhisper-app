// CEREBRAS LLM CLIENT

import { computeCerebrasChatCost, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { isRecord, safeReadText } from '../lib/utils';
import { reportMissingUsage, type CorrectionRequestPayload } from './groq-llm';

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

  const chatUrl = `${CEREBRAS_BASE_URL}/chat/completions`;

  const response = await fetch(chatUrl, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      model: CEREBRAS_CHAT_MODEL,
      ...payload,
      reasoning_effort: 'low',
      stream: false,
    }),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error('Cerebras API returned error', {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`Cerebras chat failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = 'cerebras';
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = computeCerebrasChatCost(usage ?? reportMissingUsage('cerebras', payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
