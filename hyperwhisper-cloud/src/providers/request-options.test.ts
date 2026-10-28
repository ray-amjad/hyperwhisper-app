import { describe, expect, test } from 'bun:test';
import { validateDictationOptions } from './assemblyai-dictation';
import { rejectRequestOptions } from './request-options';

// What the adapter's own check throws, so the gate's message is the same text.
function adapterMessage(language?: string, domain?: string): string {
  try {
    validateDictationOptions(language, domain);
  } catch (error) {
    return (error as Error).message;
  }
  throw new Error('expected validateDictationOptions to throw');
}

describe('rejectRequestOptions', () => {
  test('AssemblyAI Dictation with the language on Auto is rejected', () => {
    const message = rejectRequestOptions('assemblyai', 'dictation', {});
    expect(message).toBe(adapterMessage());
    expect(message).toContain('Select an explicit supported language instead of Auto for Dictation.');
    expect(rejectRequestOptions('assemblyai', 'dictation', { language: 'auto' })).toBe(adapterMessage('auto'));
  });

  test('AssemblyAI Dictation with a domain is rejected', () => {
    const message = rejectRequestOptions('assemblyai', 'dictation', { language: 'en', domain: 'medical' });
    expect(message).toBe(adapterMessage('en', 'medical'));
    expect(message).toContain('Dictation does not support Medical Mode or other domains.');
  });

  test('AssemblyAI Dictation with an explicit supported language is accepted', () => {
    expect(rejectRequestOptions('assemblyai', 'dictation', { language: 'en' })).toBeNull();
    expect(rejectRequestOptions('assemblyai', 'dictation', { language: 'nb-NO' })).toBeNull();
  });

  test('another AssemblyAI model has no option rules', () => {
    expect(rejectRequestOptions('assemblyai', 'universal-3-5-pro', {})).toBeNull();
    expect(rejectRequestOptions('assemblyai', 'universal-3-5-pro', { domain: 'medical' })).toBeNull();
  });

  test('another provider has no option rules, even with the model id dictation', () => {
    expect(rejectRequestOptions('deepgram', 'nova-3', {})).toBeNull();
    expect(rejectRequestOptions('deepgram', 'dictation', { domain: 'medical' })).toBeNull();
  });
});
