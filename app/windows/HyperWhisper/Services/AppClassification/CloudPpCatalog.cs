using System.Reflection;
using System.Text.Json;
// Rust shared-core binding. `PpModel` lives here but is NOT used by the
// struct-returning methods below (those keep the UI-bound native CloudPpModel /
// CloudPpProvider records — see the "kept native" note).
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.AppClassification;

/// <summary>
/// Loads and exposes <c>shared-app-classification/cloud-pp-catalog.json</c> — the
/// cross-platform source of truth for HyperWhisper Cloud <b>post-processing</b>
/// (LLM) engines. Drives the credit-billed (no-key) post-processing Engine +
/// Model picker and the <c>X-LLM-Provider</c> / <c>X-LLM-Model</c> headers sent to
/// the backend <c>/post-process</c> route.
///
/// Mirrors <see cref="CloudSttCatalog"/>. Prices in the catalog are display/estimate
/// only — actual billing comes from the backend <c>cost-calculator.ts</c>, which
/// must be kept in sync (see <c>shared-app-classification/CLAUDE.md</c>).
/// </summary>
public sealed class CloudPpCatalog
{
    public static CloudPpCatalog Shared { get; } = LoadCatalog();

    public int Version { get; init; }
    public string Updated { get; init; } = "missing";
    public CloudPpProvider[] Providers { get; init; } = [];

    /// <summary>Look up an engine by id (case-insensitive); null if unknown.</summary>
    public CloudPpProvider? GetById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var p in Providers)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>
    /// Engines surfaced in the Engine dropdown, in catalog order. Hides any engine
    /// gated off by <c>enabled == false</c> (un-deployed on the backend); a null
    /// <c>enabled</c> is treated as enabled.
    /// </summary>
    public IReadOnlyList<CloudPpProvider> PickerProviders()
    {
        var list = new List<CloudPpProvider>();
        foreach (var p in Providers)
            if (p.Enabled != false)
                list.Add(p);
        return list;
    }

    /// <summary>Selectable models for an engine, in catalog order (hides <c>enabled == false</c>); empty when unknown.</summary>
    public IReadOnlyList<CloudPpModel> ModelsForId(string? id)
    {
        var provider = GetById(id);
        if (provider?.Models is null) return [];
        var list = new List<CloudPpModel>();
        foreach (var m in provider.Models)
            if (m.Enabled != false)
                list.Add(m);
        return list;
    }

    /// <summary>The default model for an engine — the <c>isDefault</c> model, else the first; null when none.</summary>
    public CloudPpModel? DefaultModelForId(string? id)
    {
        var models = ModelsForId(id);
        if (models.Count == 0) return null;
        foreach (var m in models)
            if (m.IsDefault) return m;
        return models[0];
    }

    /// <summary>Look up a single model within an engine by its model id (case-insensitive); null if not found.</summary>
    public CloudPpModel? GetModel(string? id, string? modelId)
    {
        if (modelId is null) return null;
        foreach (var m in ModelsForId(id))
            if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    /// <summary>The <c>X-LLM-Provider</c> header value for an engine id, or null if unknown.</summary>
    // TODO-verify (Windows/CI): Rust shared-core swap.
    public string? LlmProviderForId(string? id)
        => string.IsNullOrEmpty(id) ? null : HyperwhisperCoreMethods.CloudPpLlmProvider(id);

    // NOTE (kept native): GetById / GetModel / DefaultModelForId / ModelsForId /
    // PickerProviders return the rich UI-bound CloudPpModel / CloudPpProvider
    // records consumed directly by CloudPostProcessingModel.cs and the mode editor.
    // The core's CloudPp* fns return the owned `PpModel` mirror (different field
    // set / shape), so swapping them would lose fields and break callers — mirrors
    // the macOS decision to keep CloudPPCatalog decoded-struct lookups native.
    // The scalar CloudPpIsEnabled / CloudPpLlmModelHeader fns also have no scalar
    // call site here (callers read the struct's Enabled / ModelHeader), so they
    // stay folded inside the native struct accessors above.

    private static CloudPpCatalog LoadCatalog()
    {
        const string resourceName = "HyperWhisper.SharedAppClassification.cloud-pp-catalog.json";
        var assembly = Assembly.GetExecutingAssembly();

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                LoggingService.Error($"CloudPpCatalog resource {resourceName} not found — falling back to empty catalog");
                return new CloudPpCatalog();
            }

            return JsonSerializer.Deserialize<CloudPpCatalog>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CloudPpCatalog();
        }
        catch (Exception ex)
        {
            // Must never propagate out of this static initializer — that would
            // poison the CLR's cached TypeInitializationException and brick the
            // mode editor for every user.
            LoggingService.Error("CloudPpCatalog deserialization failed — falling back to empty catalog", ex);
            return new CloudPpCatalog();
        }
    }
}

/// <summary>
/// A post-processing engine (provider). <see cref="Id"/> is the provider-qualified
/// key prefix persisted in <c>Mode.CloudPostProcessingModel</c> (<c>&lt;id&gt;:&lt;modelId&gt;</c>),
/// chosen so Groq vs Cerebras don't collide on the shared <c>gpt-oss-120b</c> model id.
/// <see cref="LlmProvider"/> is the <c>X-LLM-Provider</c> header value.
/// </summary>
public sealed class CloudPpProvider
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The <c>X-LLM-Provider</c> header value the backend routes on.</summary>
    public string LlmProvider { get; init; } = string.Empty;

    /// <summary>"openai" (OpenAI-compatible /chat/completions) or "anthropic" (native). Informational.</summary>
    public string? ApiStyle { get; init; }

    /// <summary>
    /// Rollout gate. Null is treated as enabled. When false, the app hides the
    /// engine so its <c>X-LLM-Provider</c> value can't silently fall back to
    /// Cerebras on a backend that hasn't deployed it yet.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>The single engine flagged as the recommended default — drives the "(Recommended)" badge on the Engine dropdown.</summary>
    public bool? IsRecommended { get; init; }

    public CloudPpModel[] Models { get; init; } = [];
}

/// <summary>
/// A selectable model within an engine. <see cref="Id"/> drives the Model dropdown;
/// the <c>X-LLM-Model</c> header value is <see cref="LlmModelHeader"/> (falling back to <see cref="Id"/>).
/// </summary>
public sealed class CloudPpModel
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? LlmModelHeader { get; init; }
    public double? PricePerMInput { get; init; }
    public double? PricePerMOutput { get; init; }
    public bool IsDefault { get; init; }
    public bool IsRecommended { get; init; }
    public int? Accuracy { get; init; }
    public int? Speed { get; init; }
    public bool PreviewStatus { get; init; }
    public bool? Enabled { get; init; }

    /// <summary>The <c>X-LLM-Model</c> header value — explicit <see cref="LlmModelHeader"/> or the id.</summary>
    public string ModelHeader => LlmModelHeader ?? Id;
}
