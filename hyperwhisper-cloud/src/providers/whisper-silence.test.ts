import { describe, expect, test } from 'bun:test';
import { isWhisperSilencePhrase } from './whisper-silence';

describe('isWhisperSilencePhrase', () => {
  test('stock silence phrases, in any case and punctuation, match', () => {
    for (const text of [' Thank you.', 'THANK YOU!', 'Thanks for watching!', ' you', 'Thank you. Thank you.', 'Bye-bye.']) {
      expect(isWhisperSilencePhrase(text)).toBe(true);
    }
  });

  test('a stock phrase inside longer speech does not match', () => {
    for (const text of ['Thank you, see you Friday.', 'Thank you. Send it tomorrow.', 'You should subscribe to that list.']) {
      expect(isWhisperSilencePhrase(text)).toBe(false);
    }
  });

  test('empty or punctuation-only text does not match', () => {
    for (const text of ['', '   ', '...']) {
      expect(isWhisperSilencePhrase(text)).toBe(false);
    }
  });
});
