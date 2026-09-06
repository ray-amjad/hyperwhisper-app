using System.Reflection;
using System.Text.Json;

namespace HyperWhisper.CloudPostProcessing;

public sealed record PostProcessingModel(string Id, string DisplayName);

/// <summary>Linux BYOK registry kept at feature parity with the Windows model picker.</summary>
public static class PostProcessingModelCatalog
{
    private static readonly IReadOnlyDictionary<CloudPostProcessingProvider, PostProcessingModel[]> Models =
        new Dictionary<CloudPostProcessingProvider, PostProcessingModel[]>
        {
            [CloudPostProcessingProvider.OpenAi] =
            [
                new("gpt-5.6-luna", "GPT-5.6 Luna"), new("gpt-4.1-mini", "GPT-4.1 Mini"),
                new("gpt-4.1", "GPT-4.1"), new("gpt-5-nano", "GPT-5 Nano"),
                new("gpt-5-mini", "GPT-5 Mini"), new("gpt-5", "GPT-5"),
                new("gpt-5.1", "GPT-5.1"), new("gpt-5.2", "GPT-5.2"),
                new("gpt-5.4-nano", "GPT-5.4 Nano"), new("gpt-5.4-mini", "GPT-5.4 Mini"),
                new("gpt-5.4", "GPT-5.4"),
            ],
            [CloudPostProcessingProvider.Anthropic] =
            [
                new("claude-haiku-4-5", "Claude 4.5 Haiku"), new("claude-sonnet-4-5", "Claude 4.5 Sonnet"),
                new("claude-sonnet-4-6", "Claude 4.6 Sonnet"), new("claude-sonnet-5", "Claude Sonnet 5"),
            ],
            [CloudPostProcessingProvider.Groq] =
            [
                new("openai/gpt-oss-120b", "GPT OSS 120B"), new("openai/gpt-oss-20b", "GPT OSS 20B"),
                new("qwen/qwen3.6-27b", "Qwen 3.6 27B"),
            ],
            [CloudPostProcessingProvider.Grok] =
            [new("grok-4.3", "Grok 4.3"), new("grok-4.5", "Grok 4.5"), new("grok-4.6", "Grok 4.6")],
            [CloudPostProcessingProvider.Gemini] =
            [
                new("gemini-3-flash-preview", "Gemini 3 Flash"), new("gemini-3.5-flash", "Gemini 3.5 Flash"),
                new("gemini-3.6-flash", "Gemini 3.6 Flash"), new("gemini-3.7-flash", "Gemini 3.7 Flash"),
                new("gemini-3.8-flash", "Gemini 3.8 Flash"),
                new("gemini-2.5-flash", "Gemini 2.5 Flash"), new("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite"),
                new("gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite"), new("gemini-2.5-pro", "Gemini 2.5 Pro"),
                new("gemini-3.1-pro-preview", "Gemini 3.1 Pro"), new("gemini-3.1-flash-lite", "Gemini 3.1 Flash Lite"),
            ],
            [CloudPostProcessingProvider.Cerebras] =
            [new("gpt-oss-120b", "GPT OSS 120B"), new("gemma-4-31b", "Gemma 4 31B")],
            [CloudPostProcessingProvider.Mistral] =
            [new("mistral-small-latest", "Mistral Small"), new("mistral-medium-3.5", "Mistral Medium 3.5")],
        };

    public static IReadOnlyList<PostProcessingModel> ForProvider(CloudPostProcessingProvider provider) =>
        Models.TryGetValue(provider, out var models) ? models : [];

    public static string? ResolveModel(CloudPostProcessingProvider provider, string? model)
    {
        var available = ForProvider(provider);
        if (available.Count == 0) return null;
        var migrated = Migrate(model?.Trim());
        return available.FirstOrDefault(item => string.Equals(item.Id, migrated, StringComparison.Ordinal))?.Id
            ?? available[0].Id;
    }

    private static string? Migrate(string? model) => model switch
    {
        "gpt-4.1-nano" => "gpt-5-nano",
        "claude-3-haiku-20240307" or "claude-3-5-haiku-latest" or "claude-haiku-4.5" or
            "claude-haiku-4-5-latest" => "claude-haiku-4-5",
        "claude-sonnet-4-20250514" or "claude-sonnet-4-0" or "claude-sonnet-4-5-latest" => "claude-sonnet-4-5",
        "claude-sonnet-4-6-latest" => "claude-sonnet-4-6",
        "meta-llama/llama-4-maverick-17b-128e-instruct" or "moonshotai/kimi-k2-instruct" or
            "mixtral-8x7b-32768" or "llama-3.3-70b-versatile" or "llama-3.1-8b-instant" or
            "meta-llama/llama-4-scout-17b-16e-instruct" or "qwen/qwen3-32b" => "openai/gpt-oss-120b",
        "llama-3.3-70b" or "qwen-3-235b-a22b-instruct-2507" or "zai-glm-4.7" => "gpt-oss-120b",
        "llama-3.1-8b" or "llama3.1-8b" => "gemma-4-31b",
        "grok-4-1-fast-non-reasoning" or "grok-4.1-fast-non-reasoning" or "grok-4-fast-non-reasoning" or
            "grok-4-1-fast-reasoning" or "grok-4-fast-reasoning" => "grok-4.3",
        "open-mistral-nemo" => "mistral-small-latest",
        "gemma-3-12b-it" or "gemma-3-27b-it" => "gemini-2.5-flash",
        "gemini-3-pro-preview" => "gemini-3.1-pro-preview",
        "gemini-3.1-flash-lite-preview" => "gemini-3.1-flash-lite",
        "gemini-2.0-flash" => "gemini-3.6-flash",
        "gemini-2.0-flash-lite" => "gemini-3.1-flash-lite",
        _ => model,
    };
}

internal sealed class HyperWhisperCloudCatalog
{
    public CloudCatalogProvider[] Providers { get; init; } = [];

    public static HyperWhisperCloudCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "HyperWhisper.CloudPostProcessing.cloud-pp-catalog.json");
        if (stream is null) return new();
        try
        {
            return JsonSerializer.Deserialize<HyperWhisperCloudCatalog>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public (string ProviderHeader, string ModelHeader, string Label)? Resolve(string? storageValue)
    {
        var pieces = storageValue?.Trim().Split(':', 2);
        var provider = pieces is { Length: 2 }
            ? Providers.FirstOrDefault(item => item.Enabled != false && item.Id == pieces[0])
            : null;
        // Preserve the existing Windows/macOS migration behavior for empty or
        // unknown persisted values. Fresh modes explicitly store the currently
        // recommended catalog model, so this only affects legacy/corrupt data.
        provider ??= Providers.FirstOrDefault(item => item.Enabled != false && item.Id == "grok")
            ?? Providers.FirstOrDefault(item => item.Enabled != false && item.IsRecommended == true)
            ?? Providers.FirstOrDefault(item => item.Enabled != false);
        if (provider is null || string.IsNullOrWhiteSpace(provider.LlmProvider)) return null;
        var model = pieces is { Length: 2 }
            ? provider.Models.FirstOrDefault(item => item.Enabled != false && item.Id == pieces[1])
            : null;
        model ??= provider.Models.FirstOrDefault(item => item.Enabled != false && item.IsDefault)
            ?? provider.Models.FirstOrDefault(item => item.Enabled != false);
        if (model is null || string.IsNullOrWhiteSpace(model.Id)) return null;
        return (provider.LlmProvider, model.LlmModelHeader ?? model.Id, $"HyperWhisper Cloud · {provider.DisplayName} · {model.DisplayName}");
    }
}

internal sealed class CloudCatalogProvider
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string LlmProvider { get; init; } = "";
    public bool? Enabled { get; init; }
    public bool? IsRecommended { get; init; }
    public CloudCatalogModel[] Models { get; init; } = [];
}

internal sealed class CloudCatalogModel
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? LlmModelHeader { get; init; }
    public bool? Enabled { get; init; }
    public bool IsDefault { get; init; }
}
