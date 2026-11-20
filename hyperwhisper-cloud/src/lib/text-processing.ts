// TEXT PROCESSING HELPERS
// Shared helpers for LLM post-processing outputs

import { isRecord } from './utils';

/**
 * The text of a reply: the first non-empty OpenAI-compatible
 * `choices[].message.content`, or Anthropic's `content[]` text blocks joined.
 * Throws only when no text field was found; a present-but-empty one (e.g.
 * `message.content: ""`) returns `''` so callers can treat it as a graceful
 * empty result rather than a malformed response.
 */
export function extractCorrectedText(response: unknown): string {
  let sawEmpty = false;

  const choices = isRecord(response) ? response['choices'] : undefined;
  if (Array.isArray(choices)) {
    for (const choice of choices) {
      const message = isRecord(choice) ? choice['message'] : undefined;
      const raw = isRecord(message) ? message['content'] : undefined;
      // OpenAI-compatible APIs may send `content` as an array of parts (Mistral
      // content chunks): their `text` fields are joined, as before this was
      // narrowed to the shapes in use.
      const content = Array.isArray(raw) ? joinPartTexts(raw) : raw;
      if (typeof content !== 'string') continue;
      if (content.length > 0) return content;
      sawEmpty = true;
    }
  }

  const blocks = isRecord(response) ? response['content'] : undefined;
  if (Array.isArray(blocks)) {
    const texts = blocks
      .map((block) => (isRecord(block) ? block['text'] : undefined))
      .filter((text): text is string => typeof text === 'string');
    const joined = texts.join('');
    if (joined.length > 0) return joined;
    if (texts.length > 0) sawEmpty = true;
  }

  if (sawEmpty) return '';
  throw new Error('Correction response missing text');
}

/** The `text` of each part that has one, joined; undefined when no part has text. */
function joinPartTexts(parts: unknown[]): string | undefined {
  const texts = parts
    .map((part) => (isRecord(part) ? part['text'] : undefined))
    .filter((text): text is string => typeof text === 'string');
  return texts.length > 0 ? texts.join('') : undefined;
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
  return text.replace(CLEAN_MARKER_PATTERN, '').trim();
}
