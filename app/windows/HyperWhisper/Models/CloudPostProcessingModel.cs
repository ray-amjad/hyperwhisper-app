using HyperWhisper.Services.AppClassification;

namespace HyperWhisper.Models;

/// <summary>
/// A catalog-backed reference to a HyperWhisper Cloud post-processing model —
/// the <c>(engine, model)</c> pair that drives the <c>/post-process</c> request via
/// the <c>X-LLM-Provider</c> (engine) / <c>X-LLM-Model</c> (model) headers.
///
/// Reads its facts (display name, header values, recommended flag) from
/// <see cref="CloudPpCatalog.Shared"/>, mirroring how <c>CloudAccuracyTier</c> +
/// <c>CloudSttCatalog</c> drive the transcription Engine/Model split, and matching
/// the macOS <c>CloudPostProcessingModel</c> struct. Persisted to the free-string
/// <c>Mode.CloudPostProcessingModel</c> column as a <b>provider-qualified key</b>
/// <c>"&lt;engineId&gt;:&lt;modelId&gt;"</c> (e.g. <c>cerebras:gpt-oss-120b</c>,
/// <c>openai:gpt-5-mini</c>) so Groq and Cerebras don't collide on the shared
/// <c>gpt-oss-120b</c> model id. <see cref="CloudPostProcessingModelExtensions.FromString"/>
/// migrates every legacy value.
/// </summary>
public sealed class CloudPostProcessingModel : IEquatable<CloudPostProcessingModel>
{
    /// <summary>Catalog engine (provider) id — the storage-key prefix and <c>X-LLM-Provider</c> source.</summary>
    public string EngineId { get; }

    /// <summary>Catalog model id within the engine — the storage-key suffix and <c>X-LLM-Model</c> source.</summary>
    public string ModelId { get; }

    public CloudPostProcessingModel(string engineId, string modelId)
    {
        EngineId = engineId ?? string.Empty;
        ModelId = modelId ?? string.Empty;
    }

    /// <summary>Provider-qualified key persisted to <c>Mode.CloudPostProcessingModel</c>.</summary>
    public string StorageValue => $"{EngineId}:{ModelId}";

    private CloudPpProvider? CatalogProvider => CloudPpCatalog.Shared.GetById(EngineId);
    private CloudPpModel? CatalogModel => CloudPpCatalog.Shared.GetModel(EngineId, ModelId);

    /// <summary>Model display name (brand name from the catalog), falling back to the model id.</summary>
    public string DisplayName => CatalogModel?.DisplayName ?? ModelId;

    /// <summary>
    /// Value for the <c>X-LLM-Provider</c> header (the catalog engine's <c>llmProvider</c>),
    /// or null when the engine is unknown (let the backend apply its default).
    /// </summary>
    public string? LlmProviderHeader => CatalogProvider?.LlmProvider;

    /// <summary>
    /// Value for the <c>X-LLM-Model</c> header (the catalog model's header / id),
    /// or null when the model is unknown.
    /// </summary>
    public string? LlmModelHeader => CatalogModel?.ModelHeader;

    // =========================================================================
    // LEGACY / DEFAULT FACTORY VALUES
    // Provider-qualified pairs matching the macOS factory values exactly.
    // =========================================================================

    public static CloudPostProcessingModel CerebrasGptOss120B => new("cerebras", "gpt-oss-120b");
    public static CloudPostProcessingModel GroqGptOss120B => new("groq", "openai/gpt-oss-120b");
    public static CloudPostProcessingModel GrokFast => new("grok", "grok-4.3");
    public static CloudPostProcessingModel ClaudeHaiku => new("anthropic", "claude-haiku-4-5");

    /// <summary>
    /// Fallback used when the stored value is empty/unknown. Preserves the historical
    /// default (Grok) so modes with an unset value don't silently change engine on
    /// upgrade. Matches the macOS <c>CloudPostProcessingModel.fallback</c>.
    /// </summary>
    public static CloudPostProcessingModel Fallback => GrokFast;

    public bool Equals(CloudPostProcessingModel? other) =>
        other is not null
        && string.Equals(EngineId, other.EngineId, StringComparison.Ordinal)
        && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as CloudPostProcessingModel);

    public override int GetHashCode() => HashCode.Combine(EngineId, ModelId);

    public override string ToString() => StorageValue;
}

/// <summary>
/// Catalog-backed engine (provider) — the first axis of the HyperWhisper Cloud
/// post-processing picker, structurally identical to the transcription
/// Engine + Model split. The engine list, display names, recommended flag, and
/// per-engine models all come from <see cref="CloudPpCatalog.Shared"/>.
/// <c>enabled: false</c> engines (un-deployed on the backend) are hidden.
/// Mirrors the macOS <c>CloudPostProcessingEngine</c> struct.
/// </summary>
public sealed class CloudPostProcessingEngine : IEquatable<CloudPostProcessingEngine>
{
    /// <summary>Catalog provider id (e.g. <c>anthropic</c>).</summary>
    public string Id { get; }

    public CloudPostProcessingEngine(string id) => Id = id ?? string.Empty;

    /// <summary>
    /// Engines shown in the Engine dropdown, in catalog order, enabled only.
    /// Falls back to the original four hardcoded engines if the catalog failed to
    /// load (so the picker never goes empty).
    /// </summary>
    public static IReadOnlyList<CloudPostProcessingEngine> AllCases()
    {
        var fromCatalog = CloudPpCatalog.Shared.PickerProviders()
            .Select(p => new CloudPostProcessingEngine(p.Id))
            .ToList();
        if (fromCatalog.Count > 0)
            return fromCatalog;

        return new[] { "cerebras", "groq", "anthropic", "grok" }
            .Select(id => new CloudPostProcessingEngine(id))
            .ToList();
    }

    private CloudPpProvider? CatalogProvider => CloudPpCatalog.Shared.GetById(Id);

    /// <summary>Provider name shown in the Engine dropdown.</summary>
    public string DisplayName => CatalogProvider?.DisplayName ?? Id;

    /// <summary>
    /// True for the single engine flagged <c>isRecommended</c> in the catalog
    /// (Anthropic / Claude Haiku 4.5 today).
    /// </summary>
    public bool IsRecommended => CatalogProvider?.IsRecommended == true;

    /// <summary>Models available under this engine, in catalog order (enabled only).</summary>
    public IReadOnlyList<CloudPostProcessingModel> Models =>
        CloudPpCatalog.Shared.ModelsForId(Id)
            .Select(m => new CloudPostProcessingModel(Id, m.Id))
            .ToList();

    /// <summary>Recommended/default model for this engine.</summary>
    public CloudPostProcessingModel DefaultModel
    {
        get
        {
            var def = CloudPpCatalog.Shared.DefaultModelForId(Id);
            if (def is not null)
                return new CloudPostProcessingModel(Id, def.Id);
            return Models.FirstOrDefault() ?? CloudPostProcessingModel.Fallback;
        }
    }

    /// <summary>The engine that owns a given post-processing model.</summary>
    public static CloudPostProcessingEngine EngineFor(CloudPostProcessingModel model) =>
        new(model.EngineId);

    /// <summary>
    /// True when a given model should be tagged "(Recommended)" in the Model
    /// dropdown — the recommended engine's default model.
    /// </summary>
    public bool IsRecommendedModel(CloudPostProcessingModel model) =>
        IsRecommended && model.Equals(DefaultModel);

    public bool Equals(CloudPostProcessingEngine? other) =>
        other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as CloudPostProcessingEngine);

    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Extension / parsing helpers for <see cref="CloudPostProcessingModel"/>.
/// Kept as a static class so existing call sites
/// (<c>CloudPostProcessingModelExtensions.FromString(...)</c>,
/// <c>model.ToStorageValue()</c>, <c>model.ToLlmProviderHeader()</c>) keep compiling
/// after the move from a hardcoded enum to the catalog-backed class.
/// </summary>
public static class CloudPostProcessingModelExtensions
{
    /// <summary>The <c>X-LLM-Provider</c> header value, or null for default behavior.</summary>
    public static string? ToLlmProviderHeader(this CloudPostProcessingModel model) => model.LlmProviderHeader;

    /// <summary>The <c>X-LLM-Model</c> header value, or null for default behavior.</summary>
    public static string? ToLlmModelHeader(this CloudPostProcessingModel model) => model.LlmModelHeader;

    /// <summary>The model display name for UI.</summary>
    public static string ToDisplayName(this CloudPostProcessingModel model) => model.DisplayName;

    /// <summary>The provider-qualified string persisted in the database.</summary>
    public static string ToStorageValue(this CloudPostProcessingModel model) => model.StorageValue;

    /// <summary>
    /// Resolves a persisted <c>cloudPostProcessingModel</c> storage string. Accepts the
    /// new provider-qualified <c>"&lt;engineId&gt;:&lt;modelId&gt;"</c> form (validated
    /// against the catalog) plus every legacy enum raw value and alias. Mirrors the
    /// macOS <c>CloudPostProcessingModel.fromStorageValue</c>.
    /// </summary>
    public static CloudPostProcessingModel FromString(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return CloudPostProcessingModel.Fallback;

        // New provider-qualified format "<engineId>:<modelId>".
        var colon = trimmed.IndexOf(':');
        if (colon >= 0)
        {
            var engineId = trimmed[..colon];
            var modelId = trimmed[(colon + 1)..];

            var model = CloudPpCatalog.Shared.GetModel(engineId, modelId);
            if (model is not null)
                return new CloudPostProcessingModel(engineId, model.Id);

            // Known engine, unknown model → that engine's default model.
            var def = CloudPpCatalog.Shared.DefaultModelForId(engineId);
            if (def is not null)
                return new CloudPostProcessingModel(engineId, def.Id);

            // Unknown engine → fall through to the legacy single-token table.
        }

        // Legacy single-token values (case-insensitive) — the pre-catalog enum
        // raw values and provider aliases. Matches the macOS switch exactly.
        return trimmed.ToLowerInvariant() switch
        {
            "cerebras" or "cerebras-gpt-oss-120b" or "cerebrasgptoss120b" or "gpt-oss-120b" or "default"
                => CloudPostProcessingModel.CerebrasGptOss120B,
            "groq" or "groq-gpt-oss-120b" or "groqgptoss120b" or "openai/gpt-oss-120b"
                => CloudPostProcessingModel.GroqGptOss120B,
            "anthropic" or "claude-haiku-4-5" or "claude-haiku-4.5" or "claudehaiku"
                => CloudPostProcessingModel.ClaudeHaiku,
            "grok" or "grok-4.3" or "grokfast"
                or "grok-4-1-fast-non-reasoning" or "grok-4.1-fast-non-reasoning"
                or "grok-4-fast-non-reasoning" or "grok-4-1-fast-reasoning" or "grok-4-fast-reasoning"
                => CloudPostProcessingModel.GrokFast,
            _ => CloudPostProcessingModel.Fallback
        };
    }
}
