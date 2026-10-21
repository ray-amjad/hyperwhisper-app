// ANTHROPIC LLM CLIENT (MESSAGES API)
// Used by the /post-process endpoint for text correction.

import { computeAnthropicCost, type GroqUsage } from '../lib/cost-calculator';
import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { readUpstreamError } from './upstream-error';
import { computeLLMRequestTimeoutMs, fetchLLMWithTimeout, readLLMJson, transcriptCharCount } from './llm-fetch';

const ANTHROPIC_API_URL = 'https://api.anthropic.com/v1/messages';
// Undated id on purpose: Anthropic's undated aliases are the supported form, and
// a pinned snapshot is what left this file on Haiku 4.5 after Haiku 5.5 shipped.
export const ANTHROPIC_MODEL = 'claude-haiku-5-5';
const ANTHROPIC_VERSION = '2023-06-01';

/**
 * The `thinking` field that turns extended thinking OFF for `model`, or
 * undefined when the model needs none.
 *
 * - `claude-haiku-5-5` and `claude-sonnet-5` think by default (adaptive), which
 *   adds latency and output tokens to a punctuation pass: `{type:'disabled'}`.
 * - `claude-sonnet-5-5` answers `disabled` with a 400; its off switch is
 *   `{type:'between_tools'}`, with no other thinking field.
 * - Every other id does not think unless asked, so it gets no field and its
 *   request body is byte-for-byte what it was.
 *
 * PARITY: the BYOK builder `anthropic_thinking` in
 * shared-core-rs/crates/hw-net/src/providers/llm/bodies.rs.
 */
export function anthropicThinkingFor(model: string): { type: 'disabled' | 'between_tools' } | undefined {
  switch (model.trim().toLowerCase()) {
    case 'claude-haiku-5-5':
    case 'claude-sonnet-5':
      return { type: 'disabled' };
    case 'claude-sonnet-5-5':
      return { type: 'between_tools' };
    default:
      return undefined;
  }
}

/** Append `thinking` (last, so the other keys keep their order) when the model needs it. */
function withThinking<T extends Record<string, unknown>>(model: string, body: T): T & { thinking?: { type: string } } {
  const thinking = anthropicThinkingFor(model);
  return thinking ? { ...body, thinking } : body;
}
export const ANTHROPIC_WRAPPER_INSTRUCTION =
  'IMPORTANT: Output ONLY the corrected text surrounded by <<CLEANED>> and <<END>> exactly. Do not add markdown headers, labels, or text outside those markers.';

// A token count is billable only as a finite, non-negative number; anything
// else bills 0 instead of turning the cost into NaN.
function toTokenCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0;
}

/**
 * Calls the Anthropic Messages API without streaming.
 * Used for post-processing text correction via the /post-process endpoint.
 * Returns shape matching Cerebras/Groq: { raw, usage, costUsd }
 */
export async function requestAnthropicChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  timeoutMs?: number,
  model: string = ANTHROPIC_MODEL,
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error('ANTHROPIC_API_KEY not configured');
  }

  // Convert OpenAI chat format to Anthropic format:
  // messages[0] = system prompt, messages[1] = user message
  const systemContent = (payload.messages[0]?.content || '')
    + `\n\n${ANTHROPIC_WRAPPER_INSTRUCTION}`;
  const userContent = payload.messages[1]?.content || '';

  // One timer covers the request and the body read (see llm-fetch.ts), so a
  // silent upstream rejects with a 504 (LLMTimeoutError, not retried) and
  // post-process.ts falls back. The bound scales with the transcript, not the
  // system prompt, because the body only arrives once the whole correction is
  // generated.
  const data = await fetchLLMWithTimeout(
    'anthropic',
    ANTHROPIC_API_URL,
    {
      method: 'POST',
      headers: {
        'x-api-key': apiKey,
        'anthropic-version': ANTHROPIC_VERSION,
        'content-type': 'application/json',
      },
      body: JSON.stringify(withThinking(model, {
        model,
        max_tokens: ANTHROPIC_MAX_TOKENS,
        system: systemContent,
        messages: [{ role: 'user', content: userContent }],
        stream: false,
      })),
    },
    async (response) => {
      if (!response.ok) {
        // The message is logged by post-process.ts and llm-provider.ts, so it
        // names the status and Anthropic's error type, never the body: an
        // Anthropic 400 can quote the transcript it rejected (#2163).
        const { fields } = await readUpstreamError(response);
        const errorType = fields.errorType ?? fields.errorCode;
        throw new LLMRequestError(
          `Anthropic API error: ${response.status}${errorType ? ` ${errorType}` : ''}`,
          response.status,
        );
      }

      return await readLLMJson<{
        content: Array<{ type: string; text?: string }>;
        stop_reason?: string | null;
        usage: {
          input_tokens: unknown;
          output_tokens: unknown;
          cache_creation_input_tokens?: unknown;
          cache_read_input_tokens?: unknown;
        };
      }>(response, 'anthropic', requestId);
    },
    requestId,
    timeoutMs ?? computeLLMRequestTimeoutMs(transcriptCharCount(payload.messages)),
  );

  const inputTokens = toTokenCount(data.usage?.input_tokens);
  const outputTokens = toTokenCount(data.usage?.output_tokens);
  const cacheCreationTokens = toTokenCount(data.usage?.cache_creation_input_tokens);
  const cacheReadTokens = toTokenCount(data.usage?.cache_read_input_tokens);
  const costUsd = computeAnthropicCost(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);

  console.log(`[${requestId}] Anthropic usage: input=${inputTokens}, output=${outputTokens}, cacheWrite=${cacheCreationTokens}, cacheRead=${cacheReadTokens}, cost=$${costUsd.toFixed(6)}`);

  return {
    raw: data,
    usage: { prompt_tokens: inputTokens, completion_tokens: outputTokens, total_tokens: inputTokens + outputTokens },
    costUsd,
  };
}
