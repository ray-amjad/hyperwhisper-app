namespace HyperWhisper.Models;

/// <summary>
/// The Model Library search box (#2169). The same rule as the macOS fix for
/// #1447 (PR #2168), on Ray's decisions there.
///
/// Every whitespace-separated word must match (AND), case-insensitively, as a
/// substring of the display name, the provider name, the detail line, the tag
/// or the model id. So <c>nemotron latin</c> needs both words, and
/// <c>base.en</c> finds the Whisper Base English row by its id. Each word may
/// match a different field.
/// </summary>
public static class LibraryModelSearch
{
    public static bool Matches(LibraryModel model, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string?[] fields =
        [
            model.DisplayName,
            model.ProviderName,
            model.Detail,
            model.Tag,
            ModelId(model),
        ];
        return words.All(word => fields.Any(field =>
            field?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>
    /// The model id the Local API and the docs show (<c>base.en</c>,
    /// <c>gpt-4o-transcribe</c>), read from the row's payload rather than from
    /// <see cref="LibraryModel.Id"/>, whose <c>cloud-tx-</c>/<c>pp-</c>/<c>whisper-</c>
    /// prefix would make words like "cloud" or "pp" match every row of a kind.
    /// </summary>
    public static string? ModelId(LibraryModel model) => model.Payload switch
    {
        CloudTranscriptionModel m => m.Id,
        WhisperModelInfo m => m.Type,
        ParakeetModelInfo m => m.Id,
        LanguageModelInfo m => m.Id,
        LocalLlmModelInfo m => m.Id,
        CustomPostProcessingEndpoint e => e.ModelName,
        _ => null
    };
}
