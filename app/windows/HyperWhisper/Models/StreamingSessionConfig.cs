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
/// <param name="CloudTier">
/// Which vendor HyperWhisper Cloud's live route should relay to, as a
/// <c>cloud-stt-catalog.json</c> entry id (<c>deepgramNova3</c>,
/// <c>geminiTranscribe</c>, …). Meaningful only when the provider is
/// HyperWhisper Cloud; every other provider ignores it.
///
/// A path selector, deliberately NOT a
/// <c>StreamingTranscriptionProvider</c> case: the credit and entitlement wiring
/// keys off provider == hyperwhisperCloud and must keep matching whichever
/// vendor is behind the relay. The shared core derives the route
/// (<c>/ws/streaming-{sttProvider}</c>) and the auto-detect vocabulary gate from
/// it. Null means the catalog default, which reproduces the endpoint this path
/// hardcoded before the tier picker existed.
/// </param>
public sealed record StreamingSessionConfig(
    string? LicenseKey,
    string? DeviceId,
    string? Language,
    IReadOnlyList<string>? Vocabulary,
    string? ApiKey,
    string? Model,
    bool FastFormatting,
    bool RemoveFillerWords,
    string? CloudTier = null
);
