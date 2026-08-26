using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
// Aliased, not imported: HyperWhisper.SharedCore also declares
// CloudTranscriptionProvider, which would clash with HyperWhisper.Models.
using SharedCoreBridge = HyperWhisper.SharedCore.SharedCoreBridge;

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
    /// reconnect/backoff path. The default is the shared core's RFC 6455 §7.4.1 set (issue #281),
    /// so this head, the Linux head and macOS cannot drift on it - the full rationale for which
    /// codes are in and which are deliberately out now lives on
    /// <c>hw_net::live::is_terminal_close_code</c>. Providers that use additional close codes of
    /// their own to signal an unrecoverable session should override this and combine with the base
    /// set rather than replace it.
    /// </summary>
    bool IsTerminalCloseCode(int closeCode) => SharedCoreBridge.IsTerminalLiveCloseCode(closeCode);
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
