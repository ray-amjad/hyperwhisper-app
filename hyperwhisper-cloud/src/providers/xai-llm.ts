// XAI GROK LLM CLIENT (CHAT COMPLETIONS)

import { computeXaiGrokFastChatCost, isGroqUsage, type GroqUsage } from '../lib/cost-calculator';
import { isRecord, safeReadText } from '../lib/utils';
import { reportMissingUsage, type CorrectionRequestPayload } from './groq-llm';

const XAI_BASE_URL = 'https://api.x.ai/v1';
const XAI_GROK_FAST_MODEL = 'grok-4.3';

export async function requestXaiGrokChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.XAI_API_KEY || process.env.GROK_API_KEY;
  if (!apiKey) {
    const error = new Error('XAI_API_KEY not configured');
    (error as { status?: number; provider?: string }).status = 503;
    (error as { provider?: string }).provider = 'grok';
    throw error;
  }

  const response = await fetch(`${XAI_BASE_URL}/chat/completions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      model: XAI_GROK_FAST_MODEL,
      reasoning_effort: 'none',
      ...payload,
      stream: false,
    }),
  });

  if (!response.ok) {
    const errorText = await safeReadText(response);
    console.error('xAI Grok API returned error', {
      requestId,
      status: response.status,
      statusText: response.statusText,
      errorText,
    });
    const error = new Error(`xAI Grok chat failed with status ${response.status}`);
    (error as { status?: number; provider?: string }).status = response.status;
    (error as { provider?: string }).provider = 'grok';
    throw error;
  }

  const json = await response.json();
  const usage = isRecord(json) && isGroqUsage(json['usage']) ? (json['usage'] as GroqUsage) : undefined;
  const costUsd = computeXaiGrokFastChatCost(usage ?? reportMissingUsage('grok', payload, json, requestId));

  return {
    raw: json,
    usage,
    costUsd,
  };
}
