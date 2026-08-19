using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Provider-specific WebSocket protocol adapter for streaming transcription.
/// </summary>
public interface IStreamingProviderStrategy
{
    Uri? BuildWebSocketUri(StreamingSessionConfig config);
    void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config);
    (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData);
    StreamingProviderEvent? ParseMessage(string text);
    IReadOnlyList<StreamingStopStep> GetStopSequence();
    string TranscriptionProviderLabel { get; }
    bool SupportsVocabulary { get; }
    bool SessionStartsOnWebSocketOpen { get; }
    int AudioSampleRate { get; }
    IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config);
    Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Whether a WebSocket close code means this provider's session cannot recover and the
    /// connection should end immediately instead of going through StreamingTranscriptionClient's
    /// reconnect/backoff path. The default covers the WebSocket protocol's own standard
    /// non-recoverable close codes (RFC 6455 §7.4.1), which apply to any provider regardless of
    /// its wire protocol: 1002 (Protocol Error), 1003 (Unsupported Data), 1007 (Invalid Payload
    /// Data), 1008 (Policy Violation), 1009 (Message Too Big), and 1011 (Internal Error). Standard
    /// transient codes - 1000 (Normal, handled separately by the caller), 1001 (Going Away), 1006
    /// (Abnormal/no close frame), 1012 (Service Restart), and 1013 (Try Again Later) - are
    /// deliberately excluded so they keep falling through to the reconnect path. Providers that
    /// use additional close codes of their own to signal an unrecoverable session should override
    /// this and combine with the base set rather than replace it.
    /// </summary>
    bool IsTerminalCloseCode(int closeCode) => closeCode is 1002 or 1003 or 1007 or 1008 or 1009 or 1011;
}

public sealed record StreamingStopStep(
    StreamingStopAction Action,
    byte[]? Payload = null,
    WebSocketMessageType MessageType = WebSocketMessageType.Text,
    TimeSpan? WaitAfter = null
);

public enum StreamingStopAction
{
    SendMessage,
    Wait,
    WaitForSessionComplete,
    Close
}
