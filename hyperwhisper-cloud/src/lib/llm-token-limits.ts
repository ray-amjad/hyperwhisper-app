import type { LLMProvider } from './llm-provider';

export const LLM_MAX_TOKENS = 8192;

// Documented provider ceilings, checked 2026-05-27 (new PP providers 2026-06-19):
// anthropic: Claude Haiku 4.5 supports 64k output tokens.
// cerebras: gpt-oss-120b supports a 131k context window; public docs do not list a separate output cap.
// groq: openai/gpt-oss-120b supports 65,536 output tokens on Groq.
// grok: xAI publishes a 1M context window for grok-4.3; public docs do not list a separate output cap.
// openai: gpt-5-mini/nano publish a 128k max output tokens cap (chat/completions).
// gemini: 2.5-flash / 2.5-flash-lite publish a 65,536 max output tokens cap.
// mistral: mistral-small-latest / open-mistral-nemo do not document a separate output cap (128k context).
export const LLM_PROVIDER_OUTPUT_CAPACITY_TOKENS: Record<LLMProvider, number | null> = {
  anthropic: 64000,
  cerebras: null,
  groq: 65536,
  grok: null,
  openai: 128000,
  gemini: 65536,
  mistral: null,
};
