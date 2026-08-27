using System.Collections.Generic;

namespace HyperWhisper.Models;

/// <summary>
/// Provider-neutral settings for a single streaming transcription session.
/// Each provider consumes only the fields it needs.
/// </summary>
/// <param name="Vocabulary">
/// Custom-vocabulary terms as a LIST, not the comma-joined string this record
/// used to carry. Joining is a per-provider wire decision — xAI repeats
/// <c>keyterm=</c>, HyperWhisper Cloud sends one comma-joined
/// <c>vocabulary=</c> — and it moved into the shared core with the rest of the
/// wire protocol (issue #281). Null when the provider takes no vocabulary or
/// there is none to send.
/// </param>
/// <param name="RemoveFillerWords">
/// Stays here rather than crossing into the shared live config: it is applied to
/// confirmed deltas by <c>StreamingTranscriptionClient.AppendFinalTranscript</c>
/// after the fact, and never reaches the wire.
/// </param>
public sealed record StreamingSessionConfig(
    string? LicenseKey,
    string? DeviceId,
    string? Language,
    IReadOnlyList<string>? Vocabulary,
    string? ApiKey,
    string? Model,
    bool FastFormatting,
    bool RemoveFillerWords
);
