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
// `language`: absent / empty for auto-detect, or any value of at most
// MAX_LANGUAGE_BYTES UTF-8 bytes with no control characters. Deliberately not
// a tag-shape check: users type free text (`en US`, `English (US)`, `中文`)
// into a mode's language box, restore it from a backup or send it through the
// Local API, and the providers already map what they know and fall back to
// auto-detect for the rest (`providers/language-codes.ts`). Only the length,
// which is what reaches the charge, and control characters are refused.

/** RFC 5646 §4.4.1's 35, counted in UTF-8 bytes so multi-byte text stays bounded too. */
export const MAX_LANGUAGE_BYTES = 35;

const MODE_ID_PATTERN = /^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/;

/** C0 controls, DEL and C1 controls. */
// eslint-disable-next-line no-control-regex
const CONTROL_CHARACTER = /[\u0000-\u001F\u007F-\u009F]/;

const encoder = new TextEncoder();

export type LabelCheck = { ok: true } | { ok: false; field: 'mode' | 'language'; message: string };

const OK: LabelCheck = { ok: true };

export function checkModeParam(mode: string | undefined): LabelCheck {
  if (mode === undefined || mode === '' || MODE_ID_PATTERN.test(mode)) return OK;
  return { ok: false, field: 'mode', message: 'mode must be a mode id (UUID).' };
}

export function checkLanguageParam(language: string | undefined): LabelCheck {
  if (language === undefined || language === '') return OK;
  // Length first, on UTF-16 units (a UTF-8 byte count is never smaller), so a
  // huge value is refused without encoding it.
  if (
    language.length <= MAX_LANGUAGE_BYTES
    && encoder.encode(language).length <= MAX_LANGUAGE_BYTES
    && !CONTROL_CHARACTER.test(language)
  ) return OK;
  return {
    ok: false,
    field: 'language',
    message: `language must be at most ${MAX_LANGUAGE_BYTES} bytes with no control characters (for example "en" or "en-US"), or omitted for auto-detect.`,
  };
}

/** The first failing check, `mode` before `language`, or ok. */
export function checkRequestLabels(labels: { mode?: string; language?: string }): LabelCheck {
  const mode = checkModeParam(labels.mode);
  if (!mode.ok) return mode;
  return checkLanguageParam(labels.language);
}
