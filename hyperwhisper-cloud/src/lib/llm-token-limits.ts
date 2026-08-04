// Anthropic's Messages API requires an explicit output ceiling. Providers with
// optional limit fields omit them and use their model/API defaults instead.
export const ANTHROPIC_MAX_TOKENS = 8192;

// Groq's and Cerebras's server default (2,048 completion tokens) is shared
// between visible output and gpt-oss's hidden reasoning tokens, so long
// dictations can exhaust the budget on reasoning alone and get truncated
// (issue #98). Cap explicitly, matching the client-side (Windows/macOS BYOK)
// fix for the same model family. Both providers serve `gpt-oss-120b`.
export const GROQ_MAX_COMPLETION_TOKENS = 8192;
export const CEREBRAS_MAX_COMPLETION_TOKENS = 8192;
