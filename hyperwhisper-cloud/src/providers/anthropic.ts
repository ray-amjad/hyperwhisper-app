// ANTHROPIC VISION LLM CLIENT (MESSAGES API, STREAMING)
// Used by the /assistant endpoint for screen-aware AI responses.

import { computeAnthropicCost, type GroqUsage } from '../lib/cost-calculator';
import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './llm-contract';
import { LLMRequestError } from './llm-errors';
import { computeLLMRequestTimeoutMs, fetchLLMWithTimeout, LLM_REQUEST_TIMEOUT_MS, transcriptCharCount } from './llm-fetch';

const ANTHROPIC_API_URL = 'https://api.anthropic.com/v1/messages';
const ANTHROPIC_MODEL = 'claude-haiku-4-5-20251001';
const ANTHROPIC_VERSION = '2023-06-01';
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
  delta?: { text?: unknown };
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
      body: JSON.stringify({
        model: ANTHROPIC_MODEL,
        max_tokens: ANTHROPIC_MAX_TOKENS,
        system: systemContent,
        messages: [{ role: 'user', content: userContent }],
        stream: false,
      }),
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
 * Calls the Anthropic Messages API with streaming enabled.
 * Returns a ReadableStream that emits OpenAI-compatible SSE chunks,
 * and a promise that resolves to the total cost in USD after the stream completes.
 */
export function streamAnthropicChat(
  systemPrompt: string,
  messages: AnthropicMessage[],
  requestId: string,
  firstByteTimeoutMs: number = LLM_REQUEST_TIMEOUT_MS,
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

  // Time-to-first-byte bound: aborts the same controller when no body chunk
  // has arrived within firstByteTimeoutMs, so a silent upstream cannot hold
  // the /assistant stream open forever. Cleared on the first chunk and on
  // every exit — a stream that has started is never cut off mid-way.
  let firstByteTimedOut = false;
  let firstByteTimer: ReturnType<typeof setTimeout> | undefined = setTimeout(() => {
    firstByteTimedOut = true;
    abortController.abort();
  }, firstByteTimeoutMs);
  const clearFirstByteTimer = () => {
    if (firstByteTimer !== undefined) {
      clearTimeout(firstByteTimer);
      firstByteTimer = undefined;
    }
  };

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
          body: JSON.stringify({
            model: ANTHROPIC_MODEL,
            max_tokens: ANTHROPIC_MAX_TOKENS,
            system: systemPrompt,
            messages,
            stream: true,
          }),
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
          clearFirstByteTimer();
          if (done) break;

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

            // Emit text deltas as OpenAI-compatible chunks
            const text = event.delta?.text;
            if (event.type === 'content_block_delta' && typeof text === 'string' && text) {
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
        if (abortController.signal.aborted && !firstByteTimedOut) {
          // Client disconnected; cancel() already resolved the partial cost.
          return;
        }
        if (firstByteTimedOut) {
          // Not a client disconnect: the upstream sent no body within the bound.
          // Ends the stream the same way as the transport-error path below.
          console.error(`[${requestId}] Anthropic stream first-byte timeout after ${firstByteTimeoutMs}ms (upstream silent, not a client disconnect)`);
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
        clearFirstByteTimer();
      }
    },
    cancel(reason: unknown) {
      clearFirstByteTimer();
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
