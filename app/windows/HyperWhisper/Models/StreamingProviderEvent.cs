namespace HyperWhisper.Models;

/// <summary>
/// Normalized events emitted by provider-specific streaming WebSocket protocols.
/// The shared streaming client handles these instead of provider JSON directly.
/// </summary>
public abstract record StreamingProviderEvent
{
    public sealed record SessionStarted(string? SessionId) : StreamingProviderEvent;
    public sealed record PartialTranscript(string Text) : StreamingProviderEvent;
    public sealed record FinalTranscript(string Text) : StreamingProviderEvent;
    public sealed record FinalTranscriptAndSessionComplete(
        string Text,
        double DurationSeconds,
        double CreditsUsed
    ) : StreamingProviderEvent;
    public sealed record SessionComplete(double DurationSeconds, double CreditsUsed) : StreamingProviderEvent;
    public sealed record Error(string Message) : StreamingProviderEvent;
    public sealed record Warning(string Message, double? RemainingSeconds = null) : StreamingProviderEvent;
    public sealed record Metadata(string Raw) : StreamingProviderEvent;
}
