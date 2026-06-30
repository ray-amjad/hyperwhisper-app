namespace HyperWhisper.Models;

/// <summary>
/// Connection lifecycle for real-time streaming transcription.
/// Mirrors the macOS streaming state model.
/// </summary>
public enum StreamingConnectionState
{
    Idle,
    Connecting,
    Ready,
    Streaming,
    Reconnecting,
    Disconnecting,
    Error
}
