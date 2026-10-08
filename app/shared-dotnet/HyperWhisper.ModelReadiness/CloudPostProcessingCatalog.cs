using uniffi.hyperwhisper_core;

namespace HyperWhisper.ModelReadiness;

/// <summary>
/// The HyperWhisper Cloud post-processing models a mode may choose, as a stored value and the
/// label to draw beside it.
///
/// The Windows mode editor reads the same core catalog directly, because it lives in the
/// assembly the core's generated binding is visible to (ModeEditorWindow.xaml.cs:1000-1039).
/// The shared view model and the Linux converter do not, and neither may keep its own copy of
/// the table: this is the one place that walks it.
/// </summary>
public static class CloudPostProcessingCatalog
{
    /// <summary>A "provider:model" storage value with its "Provider — Model" display label.</summary>
    public readonly record struct Entry(string Value, string Label);

    private static IReadOnlyList<Entry>? _entries;
    private static Dictionary<string, string>? _providerDefaults;

    public static IReadOnlyList<Entry> Entries => _entries ??= Load();

    /// <summary>
    /// Maps a stored "provider:model" value onto a value the picker lists. A listed value comes
    /// back unchanged. A known provider with a model the catalog no longer offers (a mode saved
    /// as "anthropic:claude-haiku-4-5" before the 2026-10 retirement) goes to that provider's
    /// default model, the same rule Windows <c>CloudPostProcessingModelExtensions.FromString</c>
    /// and the shared-dotnet runtime route (<c>HyperWhisperCloudCatalog.Resolve</c>) apply.
    /// Anything else comes back as given, so the editor never invents a provider.
    /// </summary>
    public static string Canonicalize(string? stored)
    {
        var trimmed = stored?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return string.Empty;
        var entries = Entries;
        if (entries.Any(entry => string.Equals(entry.Value, trimmed, StringComparison.Ordinal)))
            return trimmed;
        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return trimmed;
        var provider = trimmed[..colon];
        return _providerDefaults is not null && _providerDefaults.TryGetValue(provider, out var fallback)
            ? fallback
            : trimmed;
    }

    private static IReadOnlyList<Entry> Load()
    {
        var entries = new List<Entry>();
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var provider in HyperwhisperCoreMethods.CloudPpProviders())
            {
                // `enabled` and `models` already have the rollout gate applied by the core.
                if (!provider.@enabled) continue;
                var providerId = provider.@llmProvider;
                if (string.IsNullOrWhiteSpace(providerId)) continue;
                var providerName = string.IsNullOrWhiteSpace(provider.@displayName)
                    ? providerId
                    : provider.@displayName;
                foreach (var model in provider.@models)
                {
                    if (string.IsNullOrWhiteSpace(model.@id)) continue;
                    var modelName = string.IsNullOrWhiteSpace(model.@displayName)
                        ? model.@id
                        : model.@displayName;
                    var value = $"{providerId}:{model.@id}";
                    entries.Add(new Entry(value, $"{providerName} — {modelName}"));
                    // isDefault wins, else the first model (cloud_pp.rs default_model).
                    if (model.@isDefault == true || !defaults.ContainsKey(providerId))
                        defaults[providerId] = value;
                }
            }
        }
        catch (Exception)
        {
            // A catalog fault must leave the mode editor usable rather than take the window
            // down, so fall through to the default pair below.
        }

        if (entries.Count > 0)
        {
            _providerDefaults = defaults;
            return entries;
        }
        _providerDefaults = new(StringComparer.Ordinal) { ["anthropic"] = "anthropic:claude-haiku-5-5" };
        return [new Entry("anthropic:claude-haiku-5-5", "Anthropic — Claude Haiku 5.5")];
    }
}
