namespace HyperWhisper.Models;

/// <summary>
/// Provider-neutral settings for a single streaming transcription session.
/// Each provider strategy consumes only the fields it needs.
/// </summary>
public sealed record StreamingSessionConfig(
    string? LicenseKey,
    string? DeviceId,
    string? Language,
    string? Vocabulary,
    string? ApiKey,
    string? Model,
    bool FastFormatting
);
