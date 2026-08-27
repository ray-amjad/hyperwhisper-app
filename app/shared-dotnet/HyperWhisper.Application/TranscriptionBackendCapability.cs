namespace HyperWhisper.PortableApplication.Transcription;

public sealed record TranscriptionBackendCapability(
    bool IsAvailable,
    string DisplayName,
    string? UnavailableReason = null);
