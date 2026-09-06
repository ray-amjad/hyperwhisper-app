using uniffi.hyperwhisper_core;

namespace HyperWhisper.ModelReadiness;

/// <summary>
/// The bring-your-own-key transcription models a vendor offers, as a stored id and the label to
/// draw beside it.
///
/// Windows fills its CloudModelCombo from CloudTranscriptionModels, a hand-maintained Windows
/// class, and draws model.DisplayName while keeping model.Id in the Tag
/// (ModeEditorWindow.xaml.cs:453-502). The shared view model has no access to that class and must
/// not grow a copy of it, so this reads the same cloud STT catalog the model library reads.
/// </summary>
public static class CloudSttModelCatalog
{
    /// <summary>A model id with its display name.</summary>
    public readonly record struct Entry(string Value, string Label);

    private static IReadOnlyDictionary<string, IReadOnlyList<Entry>>? _byProvider;
    private static IReadOnlyDictionary<string, string>? _labels;

    /// <summary>Every model the given BYOK vendor offers, in catalog order.</summary>
    public static IReadOnlyList<Entry> ForProvider(string sttProvider)
        => ByProvider().TryGetValue(sttProvider, out var entries) ? entries : [];

    /// <summary>A model id to its display name, across every vendor. First vendor wins a tie.</summary>
    public static string Label(string modelId)
        => Labels().TryGetValue(modelId, out var label) ? label : modelId;

    private static IReadOnlyDictionary<string, IReadOnlyList<Entry>> ByProvider()
    {
        if (_byProvider is not null) return _byProvider;
        var byProvider = new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var provider in HyperwhisperCoreMethods.CloudSttEntries())
            {
                var sttProvider = provider.@sttProvider;
                if (string.IsNullOrWhiteSpace(sttProvider)) continue;
                // Only a vendor a user can actually point their own key at belongs here; the
                // HyperWhisper Cloud tier is chosen on a different control.
                if (provider.@access is null || !provider.@access.@byokEligible) continue;
                var entries = new List<Entry>();
                foreach (var model in provider.@models)
                {
                    var modelId = model.@id;
                    if (string.IsNullOrEmpty(modelId)) continue;
                    var label = string.IsNullOrWhiteSpace(model.@displayName) ? modelId : model.@displayName;
                    entries.Add(new Entry(modelId, label));
                    labels.TryAdd(modelId, label);
                }
                if (entries.Count > 0) byProvider[sttProvider] = entries;
            }
        }
        catch (Exception)
        {
            // A catalog fault leaves the pickers empty rather than taking the editor down.
        }
        _labels = labels;
        return _byProvider = byProvider;
    }

    private static IReadOnlyDictionary<string, string> Labels()
    {
        ByProvider();
        return _labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
