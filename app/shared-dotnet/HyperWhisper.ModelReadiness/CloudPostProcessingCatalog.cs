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

    public static IReadOnlyList<Entry> Entries => _entries ??= Load();

    private static IReadOnlyList<Entry> Load()
    {
        var entries = new List<Entry>();
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
                    entries.Add(new Entry($"{providerId}:{model.@id}", $"{providerName} — {modelName}"));
                }
            }
        }
        catch (Exception)
        {
            // A catalog fault must leave the mode editor usable rather than take the window
            // down, so fall through to the default pair below.
        }

        return entries.Count > 0
            ? entries
            : [new Entry("anthropic:claude-haiku-4-5", "Anthropic — Claude Haiku 4.5")];
    }
}
