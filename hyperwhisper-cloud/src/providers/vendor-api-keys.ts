/**
 * The env vars that hold one vendor's API key, in the order they are read: the
 * canonical name first, then the older one still set on some machines. Every
 * path to a vendor (its STT adapter, its live WebSocket, its `/post-process`
 * chat) reads that vendor's one list. A leaf module so any of them can import it.
 */
/** `GEMINI_API_KEY` is canonical; `GOOGLE_GEMINI_API_KEY` is the older name. */
export const GEMINI_API_KEY_ENV = ['GEMINI_API_KEY', 'GOOGLE_GEMINI_API_KEY'] as const;
/** `XAI_API_KEY` is canonical; `GROK_API_KEY` is the older name. */
export const XAI_API_KEY_ENV = ['XAI_API_KEY', 'GROK_API_KEY'] as const;

/**
 * The first non-empty key among `envNames`, or undefined. Read at call time,
 * never at import, so a test (or a secret sync) that changes the env is seen.
 */
export function readApiKey(envNames: readonly string[]): string | undefined {
  for (const name of envNames) {
    const value = process.env[name];
    if (value) return value;
  }
  return undefined;
}
