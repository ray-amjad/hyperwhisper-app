import { isRecord } from './utils';
import { containsPromptLeakage, extractCorrectedText, stripCleanMarkers } from './text-processing';

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

export type CompletionFailure = 'none' | 'output_limit' | 'incomplete_response' | 'prompt_leakage' | 'empty_cleaned_text';

/**
 * Composed completion policy — the TS analog of the Rust core's
 * `evaluate_completion`. Gates on completion state, extracts and strips
 * markers, then rejects on prompt leakage or an empty result. A throw from
 * `extractCorrectedText` (a response with no text field anywhere) is
 * genuinely malformed and propagates to the caller rather than being
 * absorbed here.
 */
export function evaluateCompletionResponse(
  raw: unknown,
  original: string
): { accepted: boolean; text: string; failure: CompletionFailure; state: LLMCompletionStatus['state']; reason: string } {
  const status = getLLMCompletionStatus(raw);

  if (status.state !== 'complete' && status.state !== 'unspecified') {
    const failure: CompletionFailure = status.state === 'output_limit' ? 'output_limit' : 'incomplete_response';
    return { accepted: false, text: original, failure, state: status.state, reason: status.reason };
  }

  const cleaned = stripCleanMarkers(extractCorrectedText(raw));

  if (containsPromptLeakage(cleaned)) {
    return { accepted: false, text: original, failure: 'prompt_leakage', state: status.state, reason: status.reason };
  }

  if (cleaned.length === 0) {
    return { accepted: false, text: original, failure: 'empty_cleaned_text', state: status.state, reason: status.reason };
  }

  return { accepted: true, text: cleaned, failure: 'none', state: status.state, reason: status.reason };
}
