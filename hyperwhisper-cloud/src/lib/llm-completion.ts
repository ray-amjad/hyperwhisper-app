import { isRecord } from './utils';

export type LLMCompletionStatus =
  | { state: 'complete'; reason: string }
  | { state: 'output_limit'; reason: string }
  | { state: 'incomplete'; reason: string };

/**
 * Normalize terminal reasons from Anthropic Messages and OpenAI-compatible
 * Chat Completions responses. A missing or unfamiliar reason fails closed so
 * partial, filtered, or otherwise interrupted text never replaces a transcript.
 */
export function getLLMCompletionStatus(raw: unknown): LLMCompletionStatus {
  if (!isRecord(raw)) {
    return { state: 'incomplete', reason: 'missing_response_object' };
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

  return { state: 'incomplete', reason: 'missing_finish_reason' };
}
