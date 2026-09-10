import { describe, expect, test } from 'bun:test';

import { buildCorrectionRequest } from './llm-contract';

describe('provider-neutral LLM request contract', () => {
  test('builds one system message and one user message with deterministic temperature', () => {
    expect(buildCorrectionRequest('system prompt', 'user transcript')).toEqual({
      messages: [
        { role: 'system', content: 'system prompt' },
        { role: 'user', content: 'user transcript' },
      ],
      temperature: 0,
    });
  });
});
