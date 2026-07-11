import { describe, expect, test } from 'bun:test';

import { evaluateCompletionResponse, getLLMCompletionStatus } from './llm-completion';

describe('getLLMCompletionStatus', () => {
  test('accepts normal terminal reasons from both supported response protocols', () => {
    expect(getLLMCompletionStatus({ choices: [{ finish_reason: 'stop' }] })).toEqual({
      state: 'complete',
      reason: 'stop',
    });
    expect(getLLMCompletionStatus({ stop_reason: 'end_turn' })).toEqual({
      state: 'complete',
      reason: 'end_turn',
    });
    expect(getLLMCompletionStatus({ stop_reason: 'stop_sequence' })).toEqual({
      state: 'complete',
      reason: 'stop_sequence',
    });
  });

  test('normalizes provider output limits', () => {
    expect(getLLMCompletionStatus({ choices: [{ finish_reason: 'length' }] }).state).toBe('output_limit');
    expect(getLLMCompletionStatus({ stop_reason: 'max_tokens' }).state).toBe('output_limit');
  });

  test('fails closed for recognized non-terminal reasons', () => {
    expect(getLLMCompletionStatus({ choices: [{ finish_reason: 'content_filter' }] })).toEqual({
      state: 'incomplete',
      reason: 'content_filter',
    });
    expect(getLLMCompletionStatus({ stop_reason: 'refusal' })).toEqual({
      state: 'incomplete',
      reason: 'refusal',
    });
  });

  test('treats missing termination metadata as unspecified rather than a rejection', () => {
    expect(getLLMCompletionStatus({ choices: [{ message: { content: 'partial' } }] })).toEqual({
      state: 'unspecified',
      reason: 'missing_finish_reason',
    });
    expect(getLLMCompletionStatus('partial')).toEqual({
      state: 'unspecified',
      reason: 'missing_response_object',
    });
    expect(getLLMCompletionStatus({})).toEqual({
      state: 'unspecified',
      reason: 'missing_finish_reason',
    });
  });
});

// Cross-language conformance: every case is run through the same composed
// policy the TS route applies (state gate -> extract -> strip markers ->
// leakage/empty checks) and checked against the vectors that also drive the
// Rust core's test suite. See shared-conformance/completion-vectors.json for
// the source of truth and case-by-case rationale.
type ConformanceCase = {
  name: string;
  wireProtocol: 'openai_chat' | 'anthropic_messages' | 'unspecified';
  reason: string | null;
  content: string;
  original: string;
  expect: { state: string; accepted: boolean; text: string; failure: string };
};

function buildRaw(testCase: ConformanceCase): unknown {
  switch (testCase.wireProtocol) {
    case 'openai_chat':
      return { choices: [{ finish_reason: testCase.reason ?? undefined, message: { content: testCase.content } }] };
    case 'anthropic_messages':
      return { stop_reason: testCase.reason ?? undefined, content: [{ type: 'text', text: testCase.content }] };
    case 'unspecified':
      // The reason is intentionally ignored here: the core treats a reason
      // arriving on an unrecognized/unspecified protocol as untrustworthy,
      // so no metadata is attached at all.
      return { choices: [{ message: { content: testCase.content } }] };
  }
}

// Runs the same composed policy the TS route applies via
// evaluateCompletionResponse; a throw (genuinely malformed response, no text
// field anywhere) is mapped to 'extract_failed' for safety, though none of
// the current vectors are expected to hit it.
function evaluate(testCase: ConformanceCase): { accepted: boolean; text: string; failure: string } {
  try {
    return evaluateCompletionResponse(buildRaw(testCase), testCase.original);
  } catch {
    return { accepted: false, text: testCase.original, failure: 'extract_failed' };
  }
}

describe('completion policy conformance vectors', () => {
  const vectorsPromise = Bun.file(
    new URL('../../../shared-conformance/completion-vectors.json', import.meta.url)
  ).json() as Promise<{ cases: ConformanceCase[] }>;

  test('TS composed policy agrees with the shared conformance vectors', async () => {
    const { cases } = await vectorsPromise;
    expect(cases.length).toBeGreaterThan(0);

    for (const testCase of cases) {
      const result = evaluate(testCase);

      expect(result.accepted, `${testCase.name}: accepted`).toBe(testCase.expect.accepted);
      expect(result.text, `${testCase.name}: text`).toBe(testCase.expect.text);
      expect(result.failure, `${testCase.name}: failure`).toBe(testCase.expect.failure);
    }
  });
});
