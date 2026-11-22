// ANTHROPIC LLM CLIENT (MESSAGES API)
// Used by the /post-process endpoint for text correction.

import type { GroqUsage } from '../lib/cost-calculator';
import { computeAnthropicCost, defaultModelFor, llmModelRow } from '../lib/llm-models';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError, requireLLMApiKey } from './llm-errors';
import { readUpstreamError } from './upstream-error';
import { computeLLMRequestTimeoutMs, estimateMissingLLMUsage, fetchLLMWithTimeout, readLLMJson, transcriptCharCount } from './llm-fetch';

const ANTHROPIC_API_URL = 'https://api.anthropic.com/v1/messages';
const ANTHROPIC_VERSION = '2023-06-01';
// The default anthropic row of llm-models.ts.
export const ANTHROPIC_MODEL = defaultModelFor('anthropic');

/**
 * `max_tokens` for `model`: its row's output cap (an id with no row takes the
 * default row's). The Messages API requires one, so every anthropic row in
 * llm-models.ts sets maxOutputTokens (pinned by llm-dispatch.test.ts).
 */
function anthropicMaxTokens(model: string): number {
  const cap = llmModelRow('anthropic', model).maxOutputTokens;
  if (cap === undefined) throw new Error(`llm-models: anthropic/${model} has no maxOutputTokens`);
  return cap;
}

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
// else is not a count (undefined), so it can never turn the cost into NaN.
function toTokenCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

/** Characters of the text blocks of a Messages API response, or of the whole body when it has none. */
function completionCharCount(data: { content?: unknown }): number {
  const blocks = Array.isArray(data.content) ? data.content : [];
  const texts = blocks
    .map((block) => (typeof block === 'object' && block !== null ? (block as { text?: unknown }).text : undefined))
    .filter((text): text is string => typeof text === 'string');
  return texts.length > 0 ? texts.reduce((sum, text) => sum + text.length, 0) : JSON.stringify(data).length;
}

/**
 * Calls the Anthropic Messages API without streaming.
 * Used for post-processing text correction via the /post-process endpoint.
 * Returns shape matching Cerebras/Groq: { raw, usage, costUsd }
 */
export async function requestAnthropicChat(
  payload: CorrectionRequestPayload,
  requestId: string,
  model: string = ANTHROPIC_MODEL,
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = requireLLMApiKey('anthropic', 'ANTHROPIC_API_KEY');

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
        max_tokens: anthropicMaxTokens(model),
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
    computeLLMRequestTimeoutMs(transcriptCharCount(payload.messages)),
  );

  // A missing or unreadable input/output count is billed by the char-based
  // estimate every LLM provider shares (estimateMissingLLMUsage), never at 0.
  // Only the missing count is estimated; a reported one is billed as reported.
  // An estimated input covers the whole prompt, so it bills no cache buckets.
  const reportedInput = toTokenCount(data.usage?.input_tokens);
  const reportedOutput = toTokenCount(data.usage?.output_tokens);
  const estimated = reportedInput === undefined || reportedOutput === undefined
    ? estimateMissingLLMUsage('anthropic', systemContent.length + userContent.length, completionCharCount(data), requestId)
    : undefined;
  const inputTokens = reportedInput ?? estimated?.prompt_tokens ?? 0;
  const outputTokens = reportedOutput ?? estimated?.completion_tokens ?? 0;
  const cacheBilled = reportedInput !== undefined;
  const cacheCreationTokens = cacheBilled ? toTokenCount(data.usage?.cache_creation_input_tokens) ?? 0 : 0;
  const cacheReadTokens = cacheBilled ? toTokenCount(data.usage?.cache_read_input_tokens) ?? 0 : 0;
  const costUsd = computeAnthropicCost(model, inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);

  console.log(`[${requestId}] Anthropic usage: input=${inputTokens}, output=${outputTokens}, cacheWrite=${cacheCreationTokens}, cacheRead=${cacheReadTokens}, cost=$${costUsd.toFixed(6)}`);

  return {
    raw: data,
    usage: { prompt_tokens: inputTokens, completion_tokens: outputTokens, total_tokens: inputTokens + outputTokens },
    costUsd,
  };
}
