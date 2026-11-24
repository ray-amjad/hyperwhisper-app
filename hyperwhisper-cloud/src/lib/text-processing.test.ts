import { describe, expect, test } from 'bun:test';
import {
  buildTranscriptUserContent,
  containsPromptLeakage,
  extractCorrectedText,
  stripCleanMarkers,
} from './text-processing';

describe('extractCorrectedText', () => {
  test('joins array-of-parts message content (Mistral content chunks)', () => {
    const response = {
      choices: [{ message: { content: [{ type: 'text', text: 'Hello ' }, { type: 'text', text: 'world' }] } }],
    };
    expect(extractCorrectedText(response)).toBe('Hello world');
  });

  test('extracts from OpenAI-style choices[].message.content', () => {
    const response = {
      choices: [{ message: { content: 'corrected transcript' } }],
    };
    expect(extractCorrectedText(response)).toBe('corrected transcript');
  });

  test('extracts from Anthropic-style content array of text blocks', () => {
    const response = {
      content: [{ type: 'text', text: 'block one ' }, { type: 'thinking' }, { type: 'text', text: 'block two' }],
    };
    expect(extractCorrectedText(response)).toBe('block one block two');
  });

  test('prefers the first non-empty candidate across multiple choices', () => {
    const response = {
      choices: [{ message: { content: '' } }, { message: { content: 'second choice wins' } }],
    };
    expect(extractCorrectedText(response)).toBe('second choice wins');
  });

  test('returns "" for a present-but-empty text field rather than throwing', () => {
    expect(extractCorrectedText({ content: [{ type: 'text', text: '' }] })).toBe('');
    expect(extractCorrectedText({ choices: [{ message: { content: '' } }] })).toBe('');
  });

  test('throws when no text field is found anywhere in the response', () => {
    expect(() => extractCorrectedText({ foo: 'bar' })).toThrow('Correction response missing text');
    expect(() => extractCorrectedText({})).toThrow('Correction response missing text');
    expect(() => extractCorrectedText({ choices: [{ message: { content: null } }] })).toThrow('Correction response missing text');
    expect(() => extractCorrectedText(null)).toThrow('Correction response missing text');
    expect(() => extractCorrectedText(42)).toThrow('Correction response missing text');
  });
});

describe('buildTranscriptUserContent', () => {
  test('wraps text in transcript delimiters', () => {
    expect(buildTranscriptUserContent('hello')).toBe('--TRANSCRIPT--\nhello\n--ENDTRANSCRIPT--');
  });

  test('preserves multi-line transcripts verbatim', () => {
    expect(buildTranscriptUserContent('line one\nline two')).toBe(
      '--TRANSCRIPT--\nline one\nline two\n--ENDTRANSCRIPT--',
    );
  });
});

describe('containsPromptLeakage', () => {
  test('detects the transcript delimiter markers', () => {
    expect(containsPromptLeakage('some text --TRANSCRIPT-- leaked')).toBe(true);
    expect(containsPromptLeakage('leaked --ENDTRANSCRIPT-- here')).toBe(true);
  });

  test('detects instruction/system-prompt/vocab tag leakage', () => {
    expect(containsPromptLeakage('<INSTRUCTIONS>do the thing</INSTRUCTIONS>')).toBe(true);
    expect(containsPromptLeakage('<USER_SYSTEM_PROMPT>...</USER_SYSTEM_PROMPT>')).toBe(true);
    expect(containsPromptLeakage('<APPLICATION_CONTEXT>ide</APPLICATION_CONTEXT>')).toBe(true);
    expect(containsPromptLeakage('<CUSTOM_VOCABULARY>term</CUSTOM_VOCABULARY>')).toBe(true);
  });

  test('returns false for clean corrected text', () => {
    expect(containsPromptLeakage('This is a normal corrected sentence.')).toBe(false);
  });
});

describe('stripCleanMarkers', () => {
  test('strips <<CLEANED>> / <<END>> wrapper markers', () => {
    expect(stripCleanMarkers('<<CLEANED>>hello world<<END>>')).toBe('hello world');
  });

  test('strips single-bracket and unterminated marker variants', () => {
    expect(stripCleanMarkers('<CLEANED>hello<END>')).toBe('hello');
    expect(stripCleanMarkers('<<CLEANED>hello world')).toBe('hello world');
  });

  test('is case-insensitive', () => {
    expect(stripCleanMarkers('<<cleaned>>hi<<end>>')).toBe('hi');
  });

  test('trims surrounding whitespace after stripping markers', () => {
    expect(stripCleanMarkers('<<CLEANED>>\n  hi there  \n<<END>>')).toBe('hi there');
  });

  test('leaves text without markers unchanged apart from trimming', () => {
    expect(stripCleanMarkers('  plain text  ')).toBe('plain text');
  });

});
