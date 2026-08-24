using System.Text.Json.Serialization;

namespace HyperWhisper.Diagnostics;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(DiagnosticEvent))]
[JsonSerializable(typeof(DiagnosticSystemInfo))]
[JsonSerializable(typeof(DiagnosticCapabilities))]
internal sealed partial class DiagnosticsJson : JsonSerializerContext
{
    internal static DiagnosticsJson Context { get; } = new(new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    });
}
