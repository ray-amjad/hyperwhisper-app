// WHISPER SILENCE HALLUCINATIONS
//
// Whisper does not answer silence with nothing. It answers with a stock phrase
// from its training subtitles: "Thank you.", "Thanks for watching!", "you".
// This module recognises that answer so the empty-transcript recovery can keep
// the `no_speech` the chosen provider already returned, instead of billing the
// phrase and pasting it into the user's app.
//
// It is applied ONLY on the recovery attempt (`isEmptyTranscriptRecovery`),
// where the chosen provider has already said "no speech" for this audio. A
// person who really says "Thank you." to a Whisper-first Mode still gets it.
// (issue ray-amjad/hyperwhisper-app#381 follow-up)

// Why a phrase list and not Whisper's own scores: measured on large-v3-turbo
// (the model Groq serves), 2.3 s, 4.4 s and 10 s of digital silence and 2
// low-noise clips ALL came back " Thank you." with `no_speech_prob` ≈ 1e-10,
// `avg_logprob` -0.22 to -0.39 and `compression_ratio` 0.556. A REAL spoken
// "Thank you." scored 5e-11 / -0.33 / 0.556. The scores cannot tell them
// apart; only the chosen provider's empty answer can, which is why this runs on
// the recovery attempt and nowhere else.

/**
 * Whole-transcript answers Whisper gives for silence, normalised by
 * `normalisePhrase`. A transcript is a hallucination only when EVERY sentence
 * in it is on this list, so "Thank you, see you Friday" is never dropped.
 */
const SILENCE_PHRASES = new Set([
  'thank you',
  'thank you very much',
  'thank you so much',
  'thanks',
  'thanks for watching',
  'thank you for watching',
  'thank you so much for watching',
  'thank you for watching this video',
  'please subscribe',
  'subscribe',
  'bye',
  'bye bye',
  'you',
]);

function normalisePhrase(sentence: string): string {
  return sentence
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s]/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * True when every sentence of the transcript is a stock Whisper silence phrase.
 */
export function isWhisperSilencePhrase(text: string): boolean {
  const sentences = text
    .split(/[.!?…。]+/)
    .map(normalisePhrase)
    .filter((sentence) => sentence.length > 0);
  return sentences.length > 0 && sentences.every((sentence) => SILENCE_PHRASES.has(sentence));
}
