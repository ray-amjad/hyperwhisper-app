// The non-streaming LLM bound scales with the prompt (#782 review round 1): a
// non-streaming response only arrives once the whole correction is generated,
// so a flat cap would abort a long, healthy correction.

import { describe, expect, test } from 'bun:test';
import {
  computeLLMRequestTimeoutMs,
  LLM_REQUEST_TIMEOUT_CEILING_MS,
  LLM_REQUEST_TIMEOUT_MS,
  LLM_REQUEST_TIMEOUT_PER_1K_CHARS_MS,
  promptCharCount,
} from './llm-fetch';

describe('computeLLMRequestTimeoutMs', () => {
  test('pins the stated numbers: 20 s floor, 10 s per 1,000 chars, 180 s ceiling', () => {
    expect(LLM_REQUEST_TIMEOUT_MS).toBe(20_000);
    expect(LLM_REQUEST_TIMEOUT_PER_1K_CHARS_MS).toBe(10_000);
    expect(LLM_REQUEST_TIMEOUT_CEILING_MS).toBe(180_000);
  });

  test('a short prompt gets the 20 s floor', () => {
    expect(computeLLMRequestTimeoutMs(0)).toBe(20_000);
    expect(computeLLMRequestTimeoutMs(1_000)).toBe(20_000);
    expect(computeLLMRequestTimeoutMs(2_000)).toBe(20_000);
  });

  test('a longer prompt gets 10 s per started 1,000 characters', () => {
    expect(computeLLMRequestTimeoutMs(2_001)).toBe(30_000);
    expect(computeLLMRequestTimeoutMs(5_000)).toBe(50_000);
    expect(computeLLMRequestTimeoutMs(12_345)).toBe(130_000);
  });

  test('a very long prompt stops at the 180 s ceiling, so a silent upstream still ends', () => {
    expect(computeLLMRequestTimeoutMs(18_000)).toBe(180_000);
    expect(computeLLMRequestTimeoutMs(18_001)).toBe(180_000);
    expect(computeLLMRequestTimeoutMs(60_000)).toBe(180_000);
    expect(computeLLMRequestTimeoutMs(10_000_000)).toBe(180_000);
  });
});

describe('promptCharCount', () => {
  test('counts every message, the system prompt included', () => {
    expect(promptCharCount([])).toBe(0);
    expect(promptCharCount([{ content: 'abc' }, { content: '' }, { content: 'de' }])).toBe(5);
  });
});
