// PER-MODEL REQUEST OPTIONS
//
// One question, one answer: does this provider/model accept the request
// options the caller sent (language, domain), and if not, why? The transcribe
// route asks this before any auth or credit work, so a request the provider
// is certain to refuse gets a 400 at once.
//
// This module exists so the route does not have to know FOUR vendor internals
// it previously carried inline:
//   1. that AssemblyAI's `dictation` model has option rules at all,
//   2. the provider and model ids that select it,
//   3. the vendor function that checks them (`validateDictationOptions`),
//   4. the vendor error type that function throws (`ProviderInputError`).
//
// Adding a model with option rules is a row in the table below, not a new `if`
// arm in the route. A provider or model with no entry accepts any options.
//
// This is a fast-fail gate, NOT the enforcement point. Each adapter re-checks
// its own options on the real request (assemblyai-dictation.ts runs
// validateDictationOptions again), so a caller that skips this gate still
// cannot get past it.

import type { SttProviderId } from '../lib/stt-models';
import { validateDictationOptions } from './assemblyai-dictation';
import { ProviderInputError } from './types';

export interface RequestOptions {
  language?: string;
  domain?: string;
}

/** Throws ProviderInputError when the options are not acceptable. */
type OptionsCheck = (options: RequestOptions) => void;

const OPTION_CHECKS: Partial<Record<SttProviderId, Readonly<Record<string, OptionsCheck>>>> = {
  assemblyai: {
    dictation: ({ language, domain }) => { validateDictationOptions(language, domain); },
  },
};

/**
 * The rejection message for options this provider/model cannot accept, or
 * `null` when they are acceptable (including every provider/model with no
 * rule). The message is the same text the adapter's own check would throw.
 */
export function rejectRequestOptions(
  provider: SttProviderId,
  model: string,
  options: RequestOptions,
): string | null {
  const check = OPTION_CHECKS[provider]?.[model];
  if (!check) return null;
  try {
    check(options);
    return null;
  } catch (error) {
    if (error instanceof ProviderInputError) return error.message;
    throw error;
  }
}
