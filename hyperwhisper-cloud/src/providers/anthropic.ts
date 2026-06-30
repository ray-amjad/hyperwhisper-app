// ANTHROPIC VISION LLM CLIENT (MESSAGES API, STREAMING)
// Used by the /assistant endpoint for screen-aware AI responses.

import { computeAnthropicCost, type GroqUsage } from '../lib/cost-calculator';
import { LLM_MAX_TOKENS } from '../lib/llm-token-limits';
import type { CorrectionRequestPayload } from './groq-llm';

const ANTHROPIC_API_URL = 'https://api.anthropic.com/v1/messages';
const ANTHROPIC_MODEL = 'claude-haiku-4-5-20251001';
const ANTHROPIC_VERSION = '2023-06-01';

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

/**
 * Calls the Anthropic Messages API without streaming.
 * Used for post-processing text correction via the /post-process endpoint.
 * Returns shape matching Cerebras/Groq: { raw, usage, costUsd }
 */
export async function requestAnthropicChat(
  payload: CorrectionRequestPayload,
  requestId: string
): Promise<{ raw: unknown; usage?: GroqUsage; costUsd: number }> {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error('ANTHROPIC_API_KEY not configured');
  }

  // Convert OpenAI chat format to Anthropic format:
  // messages[0] = system prompt, messages[1] = user message
  const systemContent = (payload.messages[0]?.content || '')
    + '\n\nIMPORTANT: Output ONLY the corrected text. Do not add markdown headers, labels, or any wrapper around the text.';
  const userContent = payload.messages[1]?.content || '';

  const response = await fetch(ANTHROPIC_API_URL, {
    method: 'POST',
    headers: {
      'x-api-key': apiKey,
      'anthropic-version': ANTHROPIC_VERSION,
      'content-type': 'application/json',
    },
    body: JSON.stringify({
      model: ANTHROPIC_MODEL,
      max_tokens: payload.max_tokens,
      system: systemContent,
      messages: [{ role: 'user', content: userContent }],
      stream: false,
    }),
  });

  if (!response.ok) {
    const errorText = await response.text().catch(() => '');
    const error = new Error(`Anthropic API error: ${response.status} ${errorText.slice(0, 500)}`);
    (error as any).status = response.status;
    throw error;
  }

  const data = await response.json() as {
    content: Array<{ type: string; text?: string }>;
    usage: {
      input_tokens: number;
      output_tokens: number;
      cache_creation_input_tokens?: number;
      cache_read_input_tokens?: number;
    };
  };

  const inputTokens = data.usage?.input_tokens || 0;
  const outputTokens = data.usage?.output_tokens || 0;
  const cacheCreationTokens = data.usage?.cache_creation_input_tokens || 0;
  const cacheReadTokens = data.usage?.cache_read_input_tokens || 0;
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
  requestId: string
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
            max_tokens: LLM_MAX_TOKENS,
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
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop() || '';

          for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            const data = line.slice(6).trim();
            if (!data || data === '[DONE]') continue;

            try {
              const event = JSON.parse(data);

              // Track usage from message_start (cache_* buckets arrive here too)
              if (event.type === 'message_start' && event.message?.usage) {
                inputTokens = event.message.usage.input_tokens || 0;
                cacheCreationTokens = event.message.usage.cache_creation_input_tokens || 0;
                cacheReadTokens = event.message.usage.cache_read_input_tokens || 0;
              }

              // Emit text deltas as OpenAI-compatible chunks
              if (event.type === 'content_block_delta' && event.delta?.text) {
                const chunk = JSON.stringify({
                  choices: [{ delta: { content: event.delta.text } }],
                });
                controller.enqueue(encoder.encode(`data: ${chunk}\n\n`));
              }

              // Track output tokens from message_delta
              if (event.type === 'message_delta' && event.usage) {
                outputTokens = event.usage.output_tokens || 0;
              }
            } catch {
              // Skip malformed JSON lines
            }
          }
        }

        controller.enqueue(encoder.encode('data: [DONE]\n\n'));
        controller.close();

        const costUsd = computeAnthropicCost(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
        console.log(`[${requestId}] Anthropic usage: input=${inputTokens}, output=${outputTokens}, cacheWrite=${cacheCreationTokens}, cacheRead=${cacheReadTokens}, cost=$${costUsd.toFixed(6)}`);
        resolveCost(costUsd);
      } catch (error) {
        if (abortController.signal.aborted) {
          // Client disconnected; cancel() already resolved the partial cost.
          return;
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
      }
    },
    cancel(reason) {
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
