// Anthropic's Messages API requires an explicit output ceiling. Providers with
// optional limit fields omit them and use their model/API defaults instead.
export const ANTHROPIC_MAX_TOKENS = 8192;

