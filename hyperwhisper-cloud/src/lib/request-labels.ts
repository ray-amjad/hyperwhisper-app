// REQUEST LABELS: `mode` AND `language`
//
// Two caller-supplied query values that ride along to the charge: the
// `/transcribe` charge records both in its metadata, a live session's charge
// records `language`. Unchecked, a long value made the charge body too big for
// the licence API, the charge was refused, and the transcription went
// unbilled. Every route that bills one of them checks it here first, and
// answers a 400 before any vendor is called or any credit is held.
//
// `mode`: the id of the client's own dictation mode. The native clients send
// the mode's UUID (macOS `Mode.id`, `uuidString`; iOS the same) or no `mode` at
// all (Windows, Linux, the shared Rust core). The shared backup schema types
// `Mode.id` as `format: uuid` (`shared-backup/hyperwhisper-backup.schema.json`),
// so a UUID is the whole set of values a client can hold. Absent or empty
// means "no mode", as before.
//
// `language`: a BCP 47 style tag (`en`, `en-US`, `zh-Hant-TW`, `auto`), or
// absent / empty for auto-detect. Each provider maps the tag to its own code
// in `providers/language-codes.ts`; this only bounds its shape and length.

/** RFC 5646 §4.4.1: a tag of up to 35 characters must be supported. */
export const MAX_LANGUAGE_TAG_LENGTH = 35;

const MODE_ID_PATTERN = /^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/;

/**
 * A primary subtag of letters (2-8, which covers `auto`), then up to three
 * more letter-or-digit subtags. `_` is accepted as a separator beside `-`:
 * `language-codes.ts` already treats the two alike.
 */
const LANGUAGE_TAG_PATTERN = /^[A-Za-z]{2,8}(?:[-_][A-Za-z0-9]{1,8}){0,3}$/;

export type LabelCheck = { ok: true } | { ok: false; field: 'mode' | 'language'; message: string };

const OK: LabelCheck = { ok: true };

export function checkModeParam(mode: string | undefined): LabelCheck {
  if (mode === undefined || mode === '' || MODE_ID_PATTERN.test(mode)) return OK;
  return { ok: false, field: 'mode', message: 'mode must be a mode id (UUID).' };
}

export function checkLanguageParam(language: string | undefined): LabelCheck {
  if (language === undefined || language === '') return OK;
  if (language.length <= MAX_LANGUAGE_TAG_LENGTH && LANGUAGE_TAG_PATTERN.test(language)) return OK;
  return {
    ok: false,
    field: 'language',
    message: `language must be a language code such as "en" or "en-US" (at most ${MAX_LANGUAGE_TAG_LENGTH} characters), or omitted for auto-detect.`,
  };
}

/** The first failing check, `mode` before `language`, or ok. */
export function checkRequestLabels(labels: { mode?: string; language?: string }): LabelCheck {
  const mode = checkModeParam(labels.mode);
  if (!mode.ok) return mode;
  return checkLanguageParam(labels.language);
}
