using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperWhisper.PortableApplication.Transcription;

public sealed record TranscriptionWordTimestamp(
    [property: JsonPropertyName("word")] string Word,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("probability")] double? Probability = null);

public sealed record TranscriptionSegmentTimestamp(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);

public sealed record TranscriptionTimestamps(
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptionSegmentTimestamp> Segments,
    [property: JsonPropertyName("words")] IReadOnlyList<TranscriptionWordTimestamp>? Words,
    [property: JsonPropertyName("raw_text")] string RawText)
{
    [JsonPropertyName("basis")]
    public string Basis => "raw_text";

    public string? ToPersistedJson() => Segments.Count == 0 && (Words?.Count ?? 0) == 0
        ? null
        : JsonSerializer.Serialize(this);
}
