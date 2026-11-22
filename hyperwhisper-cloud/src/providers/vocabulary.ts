/**
 * Per-upstream limits for {@link splitVocabularyTerms}. Everything NOT
 * expressed here is identical across the adapters and lives in the helper: the
 * `,` / newline / `;` separators, the leading-bullet strip, and dropping empty
 * terms.
 */
export interface VocabularyTermPolicy {
  /** Maximum number of terms forwarded upstream. */
  maxTerms: number;
  /**
   * Drop a term longer than this many characters. Omit for no length limit —
   * Gemini receives the terms as prose, so no upstream field bounds them.
   */
  maxTermChars?: number;
  /**
   * Drop a term of more than this many whitespace-separated words. Omit for no
   * word limit. Only ElevenLabs Scribe documents one.
   */
  maxTermWords?: number;
  /**
   * Replace every run of inner whitespace with this string, BEFORE the length
   * checks below. Only Mistral Voxtral needs it: it 400s the whole request when
   * a `context_bias` item holds whitespace, so a multi-word term is joined with
   * `_` rather than dropped. See the call site for the reference.
   */
  joinWordsWith?: string;
  /**
   * Reproduce the Rust core's `sanitize_vocabulary_word`, the canonical
   * sanitizer the BYOK path routes through: strip `<`/`>`, collapse whitespace
   * runs to one space, and de-duplicate case-insensitively. The bullet rule
   * (see {@link LIST_BULLET}) is the same with or without it.
   */
  sanitize?: boolean;
  /**
   * What `maxTermChars` does to a longer term. `'drop'` (the default) is for an
   * upstream hard limit, measured in UTF-16 units. `'truncate'` is the canonical
   * sanitizer's rule, and cuts by code POINT so a term of supplementary
   * characters is not cut early or split mid-pair.
   */
  overLongTerm?: 'truncate' | 'drop';
}

/**
 * A leading list bullet: `-` or `*` followed by whitespace, or standing alone.
 *
 * Without the whitespace a leading `-` or `*` is part of the term (`-Xmx`,
 * `*args`), and stripping it biases the model toward a different token than the
 * user entered (issue #729). A bare `-` or `*` is an empty bullet, so it
 * strips to nothing and the term is dropped rather than sent as punctuation.
 */
const LIST_BULLET = /^[-*](?:\s+|$)/;

/**
 * Split a user's vocabulary prompt into the individual terms an upstream's
 * keyterm / keyword / phrase-list field takes.
 *
 * Input `"HyperWhisper, SwiftUI\n- Claude"` → `["HyperWhisper", "SwiftUI", "Claude"]`.
 *
 * Every adapter had grown its own copy of the same four steps — split on
 * `,` / newline / `;`, trim, strip a leading `-` / `*` bullet, drop empties —
 * differing only in the numeric limits on {@link VocabularyTermPolicy}. So the
 * meaning of "one vocabulary term" was defined in nine places at once, and a
 * term that one adapter forwarded another silently dropped.
 *
 * Every upstream here takes ONE repeated field value per term, never a single
 * comma-joined string (that boosts one literal phrase containing commas, which
 * does nothing), so callers append each returned term separately.
 *
 * Sanitizing is opt-in through `policy.sanitize`: `providers/openai.ts`,
 * `providers/gemini-transcribe.ts`, `providers/xai-stt.ts` and
 * `providers/assemblyai-dictation.ts` turn it on to match the Rust core's
 * `sanitize_vocabulary_word`. Every caller, sanitizing or not, strips a bullet
 * by the one {@link LIST_BULLET} rule.
 */
export function splitVocabularyTerms(
  initialPrompt: string,
  policy: VocabularyTermPolicy,
): string[] {
  const terms: string[] = [];
  const seen = new Set<string>();

  for (const raw of initialPrompt.split(/[,\n;]+/)) {
    if (terms.length >= policy.maxTerms) break;

    let term = raw.trim().replace(LIST_BULLET, '');
    if (policy.sanitize) {
      term = term.replace(/[<>]/g, '').replace(/\s+/g, ' ').trim();
    }
    if (policy.joinWordsWith !== undefined) {
      term = term.replace(/\s+/g, policy.joinWordsWith);
    }
    if (policy.maxTermChars !== undefined && policy.overLongTerm === 'truncate') {
      term = Array.from(term).slice(0, policy.maxTermChars).join('');
    }

    if (term.length === 0) continue;
    if (policy.maxTermChars !== undefined && term.length > policy.maxTermChars && policy.overLongTerm !== 'truncate') continue;
    if (policy.maxTermWords !== undefined && term.split(/\s+/).length > policy.maxTermWords) continue;

    if (policy.sanitize) {
      const key = term.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
    }

    terms.push(term);
  }

  return terms;
}
