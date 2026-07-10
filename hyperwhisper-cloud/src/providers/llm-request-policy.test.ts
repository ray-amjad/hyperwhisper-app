import { describe, expect, test } from 'bun:test';

import { ANTHROPIC_MAX_TOKENS } from '../lib/llm-token-limits';
import { ANTHROPIC_WRAPPER_INSTRUCTION } from './anthropic';
import { buildCorrectionRequest } from './groq-llm';
import { buildOpenAIBody } from './openai-llm';

describe('hosted LLM output-limit policy', () => {
  test('omits optional output caps from the shared provider payload', () => {
    const payload = buildCorrectionRequest('system', 'transcript');

    expect(payload).not.toHaveProperty('max_tokens');
    expect(payload).not.toHaveProperty('max_completion_tokens');
  });

  test('does not translate an output cap into the OpenAI request', () => {
    const body = buildOpenAIBody(buildCorrectionRequest('system', 'transcript'), 'gpt-5-mini');

    expect(body).not.toHaveProperty('max_tokens');
    expect(body).not.toHaveProperty('max_completion_tokens');
  });

  test('keeps Anthropic required max_tokens at 8192', () => {
    expect(ANTHROPIC_MAX_TOKENS).toBe(8192);
  });

  test('keeps Anthropic aligned with the strict cleaned-wrapper contract', () => {
    expect(ANTHROPIC_WRAPPER_INSTRUCTION).toContain('<<CLEANED>>');
    expect(ANTHROPIC_WRAPPER_INSTRUCTION).toContain('<<END>>');
  });
});
