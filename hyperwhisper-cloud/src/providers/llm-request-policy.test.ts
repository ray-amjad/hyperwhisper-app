import { describe, expect, test } from 'bun:test';

import { ANTHROPIC_MAX_TOKENS, GROQ_MAX_COMPLETION_TOKENS } from '../lib/llm-token-limits';
import { ANTHROPIC_WRAPPER_INSTRUCTION } from './anthropic';
import { buildCorrectionRequest, buildGroqBody } from './groq-llm';
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

  // Groq is the documented exception to the omit-optional-caps rule: its
  // default ceiling is too low for a cleaned transcript once gpt-oss reasoning
  // tokens are drawn from the same budget.
  test('sends an explicit completion cap on the Groq request', () => {
    const body = buildGroqBody(buildCorrectionRequest('system', 'transcript'), 'openai/gpt-oss-120b');

    expect(body.max_completion_tokens).toBe(GROQ_MAX_COMPLETION_TOKENS);
    expect(body).not.toHaveProperty('max_tokens');
  });

  test('keeps the Groq cap under the free-tier TPM ceiling', () => {
    expect(GROQ_MAX_COMPLETION_TOKENS).toBe(4096);
    expect(GROQ_MAX_COMPLETION_TOKENS).toBeLessThan(8000);
  });

  test('keeps Anthropic required max_tokens at 8192', () => {
    expect(ANTHROPIC_MAX_TOKENS).toBe(8192);
  });

  test('keeps Anthropic aligned with the strict cleaned-wrapper contract', () => {
    expect(ANTHROPIC_WRAPPER_INSTRUCTION).toContain('<<CLEANED>>');
    expect(ANTHROPIC_WRAPPER_INSTRUCTION).toContain('<<END>>');
  });
});
