// TEXT PROCESSING HELPERS
// Shared helpers for LLM post-processing outputs

import { isRecord } from './utils';

/**
 * Threaded through the recursive extraction helpers below so a present-but-empty
 * string (e.g. `message.content: ""`) can be distinguished from a response that
 * has no text field anywhere. `sawEmpty` is only set, never used to short-circuit
 * the search — a non-empty candidate found anywhere still wins.
 */
interface ExtractionContext {
  sawEmpty: boolean;
}

/**
 * Extract corrected text from chat responses
 */
export function tryExtractCorrectionText(value: unknown, ctx: ExtractionContext = { sawEmpty: false }): string | undefined {
  if (typeof value === 'string') {
    if (value.length > 0) {
      return value;
    }
    ctx.sawEmpty = true;
    return undefined;
  }

  if (!isRecord(value)) {
    return undefined;
  }

  const directKeys = ['response', 'result', 'output_text', 'text'] as const;
  for (const key of directKeys) {
    const candidate = value[key];
    if (typeof candidate === 'string') {
      if (candidate.length > 0) {
        return candidate;
      }
      ctx.sawEmpty = true;
    }
  }

  const responseField = value['response'];
  const nestedResponse = tryExtractCorrectionText(responseField, ctx);
  if (nestedResponse) {
    return nestedResponse;
  }

  const choices = value['choices'];
  if (Array.isArray(choices)) {
    for (const choice of choices) {
      const choiceText = tryExtractCorrectionText(choice, ctx);
      if (choiceText) {
        return choiceText;
      }

      if (!isRecord(choice)) {
        continue;
      }

      const messageText = tryExtractCorrectionText(choice['message'], ctx);
      if (messageText) {
        return messageText;
      }

      const deltaText = tryExtractCorrectionText(choice['delta'], ctx);
      if (deltaText) {
        return deltaText;
      }
    }
  }

  const output = value['output'];
  const outputText = extractTextFromContent(output, ctx);
  if (outputText) {
    return outputText;
  }

  const content = value['content'];
  const contentText = extractTextFromContent(content, ctx);
  if (contentText) {
    return contentText;
  }

  return undefined;
}

function extractTextFromContent(value: unknown, ctx: ExtractionContext): string | undefined {
  if (typeof value === 'string') {
    if (value.length > 0) {
      return value;
    }
    ctx.sawEmpty = true;
    return undefined;
  }

  if (isRecord(value)) {
    const direct = value['text'];
    if (typeof direct === 'string') {
      if (direct.length > 0) {
        return direct;
      }
      ctx.sawEmpty = true;
    }

    const nested = tryExtractCorrectionText(value['message'], ctx);
    if (nested) {
      return nested;
    }

    const nestedContent = extractTextFromContent(value['content'], ctx);
    if (nestedContent) {
      return nestedContent;
    }
  }

  if (Array.isArray(value)) {
    const segments: string[] = [];
    for (const item of value) {
      const text = tryExtractCorrectionText(item, ctx);
      if (text) {
        segments.push(text);
      }
    }

    if (segments.length) {
      return segments.join('');
    }
  }

  return undefined;
}

/**
 * Extract corrected text. Throws only when no text field was found anywhere
 * in the response; a present-but-empty text field (e.g. `message.content: ""`)
 * returns `''` so callers can treat it as a graceful empty result rather than
 * a malformed response.
 */
export function extractCorrectedText(response: unknown): string {
  const ctx: ExtractionContext = { sawEmpty: false };
  const text = tryExtractCorrectionText(response, ctx);
  if (typeof text === 'string' && text.length > 0) {
    return text;
  }

  if (ctx.sawEmpty) {
    return '';
  }

  throw new Error('Correction response missing text');
}

/**
 * Wrap the raw transcript in clear delimiters for the post-processing prompt
 */
export function buildTranscriptUserContent(text: string): string {
  return `--TRANSCRIPT--\n${text}\n--ENDTRANSCRIPT--`;
}

const CLEAN_MARKER_PATTERN = /<<\/?CLEANED>>|<<CLEANED>|<CLEANED>>|<CLEANED>|<<\/?END>>|<<END>|<END>>|<END>/gi;

const PROMPT_LEAKAGE_MARKERS = [
  '--TRANSCRIPT--',
  '--ENDTRANSCRIPT--',
  '<INSTRUCTIONS>',
  '<USER_SYSTEM_PROMPT>',
  '<APPLICATION_CONTEXT>',
  '<CUSTOM_VOCABULARY>',
];

/**
 * Detect if LLM response contains echoed prompt content instead of corrected text.
 * Under load, LLMs can return 200 OK with the raw prompt leaked back.
 */
export function containsPromptLeakage(text: string): boolean {
  return PROMPT_LEAKAGE_MARKERS.some(marker => text.includes(marker));
}

/**
 * Remove <<CLEANED>> / <<END>> markers left by Groq post-processing prompts
 */
export function stripCleanMarkers(text: string): string {
  if (typeof text !== 'string') {
    return '';
  }

  const withoutMarkers = text.replace(CLEAN_MARKER_PATTERN, '');
  return withoutMarkers.trim();
}
