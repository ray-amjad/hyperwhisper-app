// LANGUAGE MODEL INFO
// Metadata for available LLM models used in post-processing.
// Each model is associated with a provider and has display information.

namespace HyperWhisper.Models;

/// <summary>
/// Represents metadata for an LLM model available for post-processing.
/// Includes the model ID (for API calls), display name, provider, and description.
/// </summary>
public class LanguageModelInfo
{
    /// <summary>
    /// The model ID used in API requests (e.g., "gpt-4.1-mini", "claude-3-haiku-20240307").
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Human-readable name for UI display (e.g., "GPT-4.1 Mini", "Claude 3 Haiku").
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// The provider this model belongs to.
    /// </summary>
    public PostProcessingProvider Provider { get; }

    /// <summary>
    /// Short description of the model's characteristics (e.g., "Fast and affordable").
    /// </summary>
    public string Description { get; }

    public LanguageModelInfo(string id, string displayName, PostProcessingProvider provider, string description)
    {
        Id = id;
        DisplayName = displayName;
        Provider = provider;
        Description = description;
    }

    /// <summary>
    /// Returns the display name for use in ComboBox items.
    /// </summary>
    public override string ToString() => DisplayName;

    // =========================================================================
    // AVAILABLE MODELS
    // This list defines all LLM models available for post-processing.
    // Models are grouped by provider and ordered by capability/cost.
    // =========================================================================

    /// <summary>
    /// All available language models for post-processing.
    /// </summary>
    public static readonly LanguageModelInfo[] AvailableModels =
    [
        // OpenAI Models
        // GPT-4.1 and GPT-5 series - text-focused models optimized for writing tasks
        new("gpt-4.1-nano", "GPT-4.1 Nano", PostProcessingProvider.OpenAI, "Fastest, cheapest"),
        new("gpt-4.1-mini", "GPT-4.1 Mini", PostProcessingProvider.OpenAI, "Balanced (recommended)"),
        new("gpt-4.1", "GPT-4.1", PostProcessingProvider.OpenAI, "High quality"),
        new("gpt-5-nano", "GPT-5 Nano", PostProcessingProvider.OpenAI, "Next-gen fastest"),
        new("gpt-5-mini", "GPT-5 Mini", PostProcessingProvider.OpenAI, "Next-gen balanced"),
        new("gpt-5", "GPT-5", PostProcessingProvider.OpenAI, "Next-gen quality"),
        new("gpt-5.1", "GPT-5.1", PostProcessingProvider.OpenAI, "Latest flagship"),
        new("gpt-5.2", "GPT-5.2", PostProcessingProvider.OpenAI, "Advanced flagship"),
        new("gpt-5.4-nano", "GPT-5.4 Nano", PostProcessingProvider.OpenAI, "Fast, lightweight"),
        new("gpt-5.4-mini", "GPT-5.4 Mini", PostProcessingProvider.OpenAI, "Latest generation, balanced"),
        new("gpt-5.4", "GPT-5.4", PostProcessingProvider.OpenAI, "Latest generation, highest quality"),
        new("gpt-5.6-luna", "GPT-5.6 Luna", PostProcessingProvider.OpenAI, "Latest generation, fastest"),

        // Anthropic Models
        // Claude series - known for nuanced text understanding
        new("claude-haiku-4-5", "Claude 4.5 Haiku", PostProcessingProvider.Anthropic, "Fast and affordable"),
        new("claude-sonnet-4-5", "Claude 4.5 Sonnet", PostProcessingProvider.Anthropic, "High quality"),
        new("claude-sonnet-4-6", "Claude 4.6 Sonnet", PostProcessingProvider.Anthropic, "High quality, capable Sonnet model"),
        new("claude-sonnet-5", "Claude Sonnet 5", PostProcessingProvider.Anthropic, "Latest, most capable Sonnet model"),

        // Groq Models
        // Ultra-fast inference via specialized hardware
        new("openai/gpt-oss-120b", "GPT OSS 120B", PostProcessingProvider.Groq, "Fast, high quality"),
        new("openai/gpt-oss-20b", "GPT OSS 20B", PostProcessingProvider.Groq, "Fast, lightweight"),
        new("qwen/qwen3.6-27b", "Qwen 3.6 27B", PostProcessingProvider.Groq, "Latest Qwen, strong quality-to-speed ratio"),

        // xAI Grok Models
        new("grok-4.3", "Grok 4.3", PostProcessingProvider.Grok, "xAI Grok 4.3 with reasoning disabled for low-latency text enhancement"),
        new("grok-4.5", "Grok 4.5", PostProcessingProvider.Grok, "xAI Grok 4.5 with reasoning disabled for low-latency text enhancement"),
        new("grok-4.6", "Grok 4.6", PostProcessingProvider.Grok, "xAI Grok 4.6 with a 500k context window and reasoning disabled for low-latency text enhancement"),

        // Google Gemini Models
        // Fast and efficient models via OpenAI-compatible endpoint
        new("gemini-3-flash-preview", "Gemini 3 Flash", PostProcessingProvider.Gemini, "Pro-level intelligence"),
        new("gemini-3.5-flash", "Gemini 3.5 Flash", PostProcessingProvider.Gemini, "Most intelligent flash, frontier agentic performance"),
        new("gemini-3.6-flash", "Gemini 3.6 Flash", PostProcessingProvider.Gemini, "Capable flash model, frontier performance for agentic tasks"),
        new("gemini-3.7-flash", "Gemini 3.7 Flash", PostProcessingProvider.Gemini, "Latest flash model, frontier performance for agentic tasks"),
        new("gemini-2.5-flash", "Gemini 2.5 Flash", PostProcessingProvider.Gemini, "Fast and efficient"),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", PostProcessingProvider.Gemini, "Lightweight, fastest"),
        new("gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite", PostProcessingProvider.Gemini, "Latest lightweight flash, fast and cost-efficient"),
        new("gemini-2.0-flash", "Gemini 2.0 Flash", PostProcessingProvider.Gemini, "Fast and efficient"),
        new("gemini-2.0-flash-lite", "Gemini 2.0 Flash Lite", PostProcessingProvider.Gemini, "Lightweight, fastest"),
        new("gemini-2.5-pro", "Gemini 2.5 Pro", PostProcessingProvider.Gemini, "High quality, advanced reasoning"),
        new("gemini-3-pro-preview", "Gemini 3 Pro", PostProcessingProvider.Gemini, "Latest pro-level intelligence"),
        new("gemini-3.1-flash-lite-preview", "Gemini 3.1 Flash Lite", PostProcessingProvider.Gemini, "Next-gen lightweight flash"),

        // Mistral Models
        // Fast, multilingual models via OpenAI-compatible endpoint
        new("mistral-small-latest", "Mistral Small", PostProcessingProvider.Mistral, "Fast, multilingual"),
        new("mistral-medium-3.5", "Mistral Medium 3.5", PostProcessingProvider.Mistral, "High quality, multilingual, balanced cost"),

        // Cerebras Models
        // Ultra-fast inference on custom silicon
        new("gpt-oss-120b", "GPT OSS 120B", PostProcessingProvider.Cerebras, "Fast, high quality"),
        new("llama3.1-8b", "Llama 3.1 8B", PostProcessingProvider.Cerebras, "Fastest, lightweight"),
        new("qwen-3-235b-a22b-instruct-2507", "Qwen 3 235B Instruct (Preview)", PostProcessingProvider.Cerebras, "Strong multilingual model"),

        // Local LLM Models
        // GGUF files managed by the local model catalog; IDs match the macOS local LLM model IDs.
        new("gemma-4-E2B-it-Q4_K_M.gguf", "Gemma 4 E2B (Q4)", PostProcessingProvider.LocalLlm, "Recommended lightweight local model"),
        new("gemma-4-E4B-it-Q4_K_M.gguf", "Gemma 4 E4B (Q4)", PostProcessingProvider.LocalLlm, "Balanced local model"),
        new("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", "Gemma 4 26B MoE (Q4)", PostProcessingProvider.LocalLlm, "Higher quality mixture-of-experts local model"),
        new("gemma-4-31B-it-Q4_K_M.gguf", "Gemma 4 31B Dense (Q4)", PostProcessingProvider.LocalLlm, "Highest quality dense local model"),
    ];

    /// <summary>
    /// Migrates legacy model IDs to current model IDs.
    /// Used for backward compatibility when loading saved mode settings.
    /// </summary>
    public static string? MigrateModelId(string? oldId) => oldId switch
    {
        // Anthropic model ID migrations
        "claude-3-haiku-20240307" => "claude-haiku-4-5",
        "claude-3-5-haiku-latest" => "claude-haiku-4-5",
        "claude-haiku-4.5" => "claude-haiku-4-5",
        "claude-haiku-4-5-latest" => "claude-haiku-4-5",
        "claude-sonnet-4-20250514" => "claude-sonnet-4-5",
        "claude-sonnet-4-0" => "claude-sonnet-4-5",
        "claude-sonnet-4-5-latest" => "claude-sonnet-4-5",
        "claude-sonnet-4-6-latest" => "claude-sonnet-4-6",
        // Groq model removals
        // Shut down by Groq 2026-03-09 → openai/gpt-oss-120b (GroqCloud deprecation notice).
        // Missed when the 2026-07-17 removals were mapped below, so it stayed in the
        // picker and every selection 404'd at post-processing time.
        "meta-llama/llama-4-maverick-17b-128e-instruct" => "openai/gpt-oss-120b",
        // Shut down by Groq 2025-10-10 → openai/gpt-oss-120b. Groq's own chain is
        // kimi-k2-instruct → kimi-k2-instruct-0905 → openai/gpt-oss-120b; the 0905
        // hop was never in this picker, so map straight to the end of the chain.
        "moonshotai/kimi-k2-instruct" => "openai/gpt-oss-120b",
        // Decommissioned by Groq 2026-07-17 → openai/gpt-oss-120b (GroqCloud deprecation notice)
        "mixtral-8x7b-32768" => "openai/gpt-oss-120b",
        "llama-3.3-70b-versatile" => "openai/gpt-oss-120b",
        "llama-3.1-8b-instant" => "openai/gpt-oss-120b",
        "meta-llama/llama-4-scout-17b-16e-instruct" => "openai/gpt-oss-120b",
        "qwen/qwen3-32b" => "openai/gpt-oss-120b",
        // Cerebras: llama-3.3-70b removed, llama-3.1-8b ID changed 2026-03-20
        "llama-3.3-70b" => "gpt-oss-120b",
        "llama-3.1-8b" => "llama3.1-8b",
        // Cerebras: zai-glm-4.7 deprecates 2026-08-17. Cerebras has NOT published an
        // official successor in the public Inference API catalog as of this writing —
        // redirecting to gpt-oss-120b is a documented judgment call (not vendor-confirmed),
        // matching the mapping used on macOS: gpt-oss-120b is the sole "Production"-tier
        // general-purpose model in that catalog, and where every other non-Z.ai Cerebras
        // deprecation in this table already redirects.
        "zai-glm-4.7" => "gpt-oss-120b",
        // xAI Grok: all grok-4-* fast variants retired 2026-05-15, redirect to grok-4.3.
        "grok-4-1-fast-non-reasoning" => "grok-4.3",
        "grok-4.1-fast-non-reasoning" => "grok-4.3",
        "grok-4-fast-non-reasoning" => "grok-4.3",
        "grok-4-1-fast-reasoning" => "grok-4.3",
        "grok-4-fast-reasoning" => "grok-4.3",
        // Mistral: open-mistral-nemo weights retired 2026-07-31 → mistral-small-latest
        // (the redirect used on macOS).
        "open-mistral-nemo" => "mistral-small-latest",
        // Local LLM: migrated from Qwen 3.5 to Gemma 4 (matches macOS).
        "Qwen3.5-4B-Q4_K_M.gguf" => "gemma-4-E2B-it-Q4_K_M.gguf",
        "Qwen3.5-9B-Q4_K_M.gguf" => "gemma-4-E4B-it-Q4_K_M.gguf",
        // Gemini: Gemma hosted models removed from API 2026-03-08
        "gemma-3-12b-it" => "gemini-2.5-flash",
        "gemma-3-27b-it" => "gemini-2.5-flash",
        _ => oldId
    };

    /// <summary>
    /// Gets all models available for a specific provider.
    /// Used to populate the model dropdown when provider changes.
    /// </summary>
    public static LanguageModelInfo[] GetModelsForProvider(PostProcessingProvider provider) =>
        AvailableModels.Where(m => m.Provider == provider).ToArray();

    /// <summary>
    /// Finds a model by its ID.
    /// Used when loading saved mode settings.
    /// </summary>
    public static LanguageModelInfo? GetById(string? id) =>
        string.IsNullOrEmpty(id) ? null : AvailableModels.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Gets the default model for a provider.
    /// Used when switching providers to auto-select a reasonable default.
    /// </summary>
    public static LanguageModelInfo? GetDefaultForProvider(PostProcessingProvider provider) =>
        GetModelsForProvider(provider).FirstOrDefault();
}
