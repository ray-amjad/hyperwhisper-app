// Anthropic's Messages API requires an explicit output ceiling. Providers with
// optional limit fields omit them and use their model/API defaults instead —
// except where a provider's default is too low to fit a cleaned transcript
// (see GROQ_MAX_COMPLETION_TOKENS).
export const ANTHROPIC_MAX_TOKENS = 8192;

// Groq is the one provider whose default is not a usable ceiling: when the
// request omits a cap it applies a low default (its reasoning docs cite 1,024),
// and openai/gpt-oss-120b spends reasoning tokens from that same budget. Long
// dictations therefore come back finish_reason=length, which the completion
// policy rejects — the caller falls back to the raw transcript and the user is
// still billed for the call. 4,096 rather than 8,192 because Groq's TPM
// admission check counts prompt + requested cap, not actual usage.
export const GROQ_MAX_COMPLETION_TOKENS = 4096;
