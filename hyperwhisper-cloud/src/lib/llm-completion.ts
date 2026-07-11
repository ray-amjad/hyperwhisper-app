import { isRecord } from './utils';

export type LLMCompletionStatus =
  | { state: 'complete'; reason: string }
  | { state: 'output_limit'; reason: string }
  | { state: 'incomplete'; reason: string }
  | { state: 'unspecified'; reason: string };

/**
 * Normalize terminal reasons from Anthropic Messages and OpenAI-compatible
 * Chat Completions responses. Recognized terminal reasons are classified as
 * complete, output-limited, or incomplete, and only a recognized
 * non-terminal reason (e.g. `length`, `content_filter`, `refusal`) fails
 * closed. Missing termination metadata — no response object, no stop_reason,
 * no finish_reason — is `unspecified` rather than a rejection: some
 * custom/self-hosted servers omit it entirely, and callers proceed to text
 * handling for `unspecified` the same as they do for `complete`.
 */
export function getLLMCompletionStatus(raw: unknown): LLMCompletionStatus {
  if (!isRecord(raw)) {
    return { state: 'unspecified', reason: 'missing_response_object' };
  }

  const anthropicReason = raw['stop_reason'];
  if (typeof anthropicReason === 'string') {
    if (anthropicReason === 'end_turn' || anthropicReason === 'stop_sequence') {
      return { state: 'complete', reason: anthropicReason };
    }
    if (anthropicReason === 'max_tokens') {
      return { state: 'output_limit', reason: anthropicReason };
    }
    return { state: 'incomplete', reason: anthropicReason };
  }

  const choices = raw['choices'];
  const firstChoice = Array.isArray(choices) ? choices[0] : undefined;
  const finishReason = isRecord(firstChoice) ? firstChoice['finish_reason'] : undefined;

  if (finishReason === 'stop') {
    return { state: 'complete', reason: finishReason };
  }
  if (finishReason === 'length') {
    return { state: 'output_limit', reason: finishReason };
  }
  if (typeof finishReason === 'string') {
    return { state: 'incomplete', reason: finishReason };
  }

  return { state: 'unspecified', reason: 'missing_finish_reason' };
}
