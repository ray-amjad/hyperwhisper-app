// ANTHROPIC VISION LLM CLIENT (MESSAGES API, STREAMING)
// Used by the /assistant endpoint for screen-aware AI responses.

import { computeAnthropicCost, type GroqUsage } from '../lib/cost-calculator';
import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { computeLLMRequestTimeoutMs, fetchLLMWithTimeout, LLM_REQUEST_TIMEOUT_MS, transcriptCharCount } from './llm-fetch';

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

export interface AnthropicContentBlock {
  type: 'text' | 'image';
  text?: string;
  source?: {
    type: 'base64';
    media_type: string;
    data: string;
  };
}

export interface AnthropicMessage {
  role: 'user' | 'assistant';
  content: string | AnthropicContentBlock[];
}

export interface AnthropicStreamResult {
  stream: ReadableStream<Uint8Array>;
  costPromise: Promise<number>;
}

// One parsed SSE `data:` line. Every field is optional and the token counts are
// `unknown`: they decide the credit deduction, so they are checked at runtime.
interface AnthropicStreamEvent {
  type?: string;
  message?: { usage?: { input_tokens?: unknown; cache_creation_input_tokens?: unknown; cache_read_input_tokens?: unknown } };
  // `type` is `text_delta` for answer text; `thinking_delta` / `signature_delta`
  // belong to a thinking block and must never reach the client.
  delta?: { type?: unknown; text?: unknown };
  usage?: { output_tokens?: unknown };
}

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
        const errorText = await response.text().catch(() => '');
        throw new LLMRequestError(
          `Anthropic API error: ${response.status} ${errorText.slice(0, 500)}`,
          response.status,
        );
      }

      return await response.json() as {
        content: Array<{ type: string; text?: string }>;
        stop_reason?: string | null;
        usage: {
          input_tokens: unknown;
          output_tokens: unknown;
          cache_creation_input_tokens?: unknown;
          cache_read_input_tokens?: unknown;
        };
      };
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

/**
 * Inter-chunk idle bound for the /assistant stream (#1112). Chosen when Bun's
 * 10 s default idleTimeout still cut the client. src/index.ts now raises Bun's
 * limit to SERVER_IDLE_TIMEOUT_SECONDS (Bun's maximum, #1252), but on Fly the
 * proxy still cuts a connection that sends no bytes for 60 s. This bound sits
 * well under both, so it ends a stalled upstream cleanly (abort, [DONE],
 * idle-timeout log, bill the tokens seen) before anything cuts the client.
 */
export const ANTHROPIC_STREAM_IDLE_TIMEOUT_MS = 8_000;

/**
 * Calls the Anthropic Messages API with streaming enabled.
 * Returns a ReadableStream that emits OpenAI-compatible SSE chunks,
 * and a promise that resolves to the total cost in USD after the stream completes.
 */
export function streamAnthropicChat(
  systemPrompt: string,
  messages: AnthropicMessage[],
  requestId: string,
  firstByteTimeoutMs: number = LLM_REQUEST_TIMEOUT_MS,
  idleTimeoutMs: number = ANTHROPIC_STREAM_IDLE_TIMEOUT_MS,
): AnthropicStreamResult {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error('ANTHROPIC_API_KEY not configured');
  }

  let resolveCost: (cost: number) => void;
  const costPromise = new Promise<number>((resolve) => {
    resolveCost = resolve;
  });

  const encoder = new TextEncoder();

  // Abort the upstream Anthropic request when the client disconnects,
  // so we stop paying for tokens nobody will receive.
  const abortController = new AbortController();

  // One upstream timer, two bounds, both aborting the same controller so a
  // silent upstream cannot hold the /assistant stream open forever:
  // - first-byte (#782): no body chunk within firstByteTimeoutMs;
  // - idle (#1112): no chunk within idleTimeoutMs of the previous one. Re-armed
  //   on every chunk, so it caps a gap, never the total length: a slow but live
  //   stream is not cut. Anthropic sends `ping` events while it works.
  // Cleared on every exit. `timedOut` records which bound fired, so the catch
  // can tell it apart from a client disconnect.
  let timedOut: 'first-byte' | 'idle' | null = null;
  let upstreamTimer: ReturnType<typeof setTimeout> | undefined;
  const clearUpstreamTimer = () => {
    if (upstreamTimer !== undefined) {
      clearTimeout(upstreamTimer);
      upstreamTimer = undefined;
    }
  };
  const armUpstreamTimer = (kind: 'first-byte' | 'idle', ms: number) => {
    clearUpstreamTimer();
    upstreamTimer = setTimeout(() => {
      timedOut = kind;
      abortController.abort();
    }, ms);
  };
  armUpstreamTimer('first-byte', firstByteTimeoutMs);

  // Hoisted so cancel() can bill the tokens consumed up to the abort point.
  let inputTokens = 0;
  let outputTokens = 0;

  const stream = new ReadableStream<Uint8Array>({
    async start(controller) {
      try {
        const response = await fetch(ANTHROPIC_API_URL, {
          method: 'POST',
          headers: {
            'x-api-key': apiKey,
            'anthropic-version': ANTHROPIC_VERSION,
            'content-type': 'application/json',
          },
          body: JSON.stringify(withThinking(ANTHROPIC_MODEL, {
            model: ANTHROPIC_MODEL,
            max_tokens: ANTHROPIC_MAX_TOKENS,
            system: systemPrompt,
            messages,
            stream: true,
          })),
          signal: abortController.signal,
        });

        if (!response.ok) {
          const errorText = await response.text().catch(() => '');
          console.error(`[${requestId}] Anthropic API error: ${response.status} ${errorText.slice(0, 500)}`);
          const errorChunk = JSON.stringify({
            choices: [{ delta: { content: '' }, finish_reason: 'error' }],
            error: `Anthropic API error: ${response.status}`,
          });
          controller.enqueue(encoder.encode(`data: ${errorChunk}\n\n`));
          controller.enqueue(encoder.encode('data: [DONE]\n\n'));
          controller.close();
          resolveCost(0);
          return;
        }

        const body = response.body;
        if (!body) {
          controller.enqueue(encoder.encode('data: [DONE]\n\n'));
          controller.close();
          resolveCost(0);
          return;
        }

        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        // inputTokens/outputTokens are hoisted to function scope (so cancel()/catch
        // can bill partial cost); the cache buckets are only needed in the success path.
        let cacheCreationTokens = 0;
        let cacheReadTokens = 0;

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          armUpstreamTimer('idle', idleTimeoutMs);

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop() || '';

          for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            const data = line.slice(6).trim();
            if (!data || data === '[DONE]') continue;

            let event: AnthropicStreamEvent | null;
            try {
              const parsed: unknown = JSON.parse(data);
              event = typeof parsed === 'object' ? parsed : null;
            } catch {
              // Skip malformed JSON lines
              continue;
            }
            if (!event) continue;

            // Track usage from message_start (cache_* buckets arrive here too)
            if (event.type === 'message_start' && event.message?.usage) {
              inputTokens = toTokenCount(event.message.usage.input_tokens);
              cacheCreationTokens = toTokenCount(event.message.usage.cache_creation_input_tokens);
              cacheReadTokens = toTokenCount(event.message.usage.cache_read_input_tokens);
            }

            // Emit text deltas as OpenAI-compatible chunks. A delta that names a
            // type other than `text_delta` (a thinking or signature delta) is
            // skipped, so a thinking block can never leak into the answer.
            const text = event.delta?.text;
            const deltaType = event.delta?.type;
            const isTextDelta = deltaType === undefined || deltaType === 'text_delta';
            if (event.type === 'content_block_delta' && isTextDelta && typeof text === 'string' && text) {
              const chunk = JSON.stringify({
                choices: [{ delta: { content: text } }],
              });
              controller.enqueue(encoder.encode(`data: ${chunk}\n\n`));
            }

            // Track output tokens from message_delta
            if (event.type === 'message_delta' && event.usage) {
              outputTokens = toTokenCount(event.usage.output_tokens);
            }
          }
        }

        controller.enqueue(encoder.encode('data: [DONE]\n\n'));
        controller.close();

        const costUsd = computeAnthropicCost(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
        console.log(`[${requestId}] Anthropic usage: input=${inputTokens}, output=${outputTokens}, cacheWrite=${cacheCreationTokens}, cacheRead=${cacheReadTokens}, cost=$${costUsd.toFixed(6)}`);
        resolveCost(costUsd);
      } catch (error) {
        if (abortController.signal.aborted && timedOut === null) {
          // Client disconnected; cancel() already resolved the partial cost.
          return;
        }
        // Not a client disconnect: the upstream went silent past a bound.
        // Ends the stream the same way as the transport-error path below.
        if (timedOut === 'first-byte') {
          console.error(`[${requestId}] Anthropic stream first-byte timeout after ${firstByteTimeoutMs}ms (upstream silent, not a client disconnect)`);
        } else if (timedOut === 'idle') {
          console.error(`[${requestId}] Anthropic stream idle timeout: no chunk for ${idleTimeoutMs}ms after the stream started (upstream stalled, not a client disconnect)`);
        }
        // Bill the tokens observed up to the failure point. Anthropic still
        // charges us for whatever it generated before the stream broke, so
        // zeroing here under-bills (input tokens are known from message_start;
        // output tokens reflect the last message_delta we saw, if any).
        const costUsd = computeAnthropicCost(inputTokens, outputTokens);
        console.error(`[${requestId}] Anthropic stream error: input=${inputTokens}, output=${outputTokens}, cost=$${costUsd.toFixed(6)}`, error);
        try {
          controller.enqueue(encoder.encode('data: [DONE]\n\n'));
          controller.close();
        } catch {
          // Controller may already be closed
        }
        resolveCost(costUsd);
      } finally {
        clearUpstreamTimer();
      }
    },
    cancel(reason: unknown) {
      clearUpstreamTimer();
      // Client disconnected: abort the upstream Anthropic request and bill
      // only the tokens observed up to this point (best effort — Anthropic
      // reports output_tokens in the final message_delta, so a mid-stream
      // disconnect typically bills input tokens only).
      abortController.abort();
      const costUsd = computeAnthropicCost(inputTokens, outputTokens);
      console.log(`[${requestId}] Anthropic stream cancelled (client disconnect): reason=${String(reason ?? 'unknown')}, input=${inputTokens}, output=${outputTokens}, cost=$${costUsd.toFixed(6)}`);
      resolveCost(costUsd);
    },
  });

  return { stream, costPromise };
}
