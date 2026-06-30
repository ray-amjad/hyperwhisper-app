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
