/**
 * The env vars that hold the Gemini API key, in the order they are read.
 * `GEMINI_API_KEY` is canonical; `GOOGLE_GEMINI_API_KEY` is the older name.
 * Every Gemini path reads this one list: the `gemini` and `gemini-transcribe`
 * STT adapters, the `gemini-transcribe` live WebSocket and the Gemini
 * `/post-process` chat. A leaf module so any of them can import it.
 */
export const GEMINI_API_KEY_ENV = ['GEMINI_API_KEY', 'GOOGLE_GEMINI_API_KEY'] as const;

/**
 * The first non-empty Gemini key, or undefined. Read at call time, never at
 * import, so a test (or a secret sync) that changes the env is seen.
 */
export function geminiApiKey(): string | undefined {
  for (const name of GEMINI_API_KEY_ENV) {
    const value = process.env[name];
    if (value) return value;
  }
  return undefined;
}
