import { describe, expect, test } from 'bun:test';

import { getLLMCompletionStatus } from './llm-completion';
import { extractCompleteCleanedText } from './text-processing';

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

  test('fails closed for missing and non-success terminal reasons', () => {
    expect(getLLMCompletionStatus({ choices: [{ finish_reason: 'content_filter' }] })).toEqual({
      state: 'incomplete',
      reason: 'content_filter',
    });
    expect(getLLMCompletionStatus({ choices: [{ message: { content: 'partial' } }] })).toEqual({
      state: 'incomplete',
      reason: 'missing_finish_reason',
    });
    expect(getLLMCompletionStatus('partial')).toEqual({
      state: 'incomplete',
      reason: 'missing_response_object',
    });
  });
});

describe('extractCompleteCleanedText', () => {
  test('accepts only a non-empty, fully wrapped correction', () => {
    expect(extractCompleteCleanedText({
      choices: [{ message: { content: '<<CLEANED>>clean transcript<<END>>' } }],
    })).toBe('clean transcript');

    expect(extractCompleteCleanedText({
      choices: [{ message: { content: '<<CLEANED>>partial transcript' } }],
    })).toBeUndefined();
    expect(extractCompleteCleanedText({
      choices: [{ message: { content: 'markerless transcript' } }],
    })).toBeUndefined();
    expect(extractCompleteCleanedText({
      choices: [{ message: { content: '<<CLEANED>><<END>>' } }],
    })).toBeUndefined();
  });
});
