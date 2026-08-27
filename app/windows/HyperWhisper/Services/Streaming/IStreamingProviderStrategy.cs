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

    /// <summary>
    /// Whether a <see cref="StreamingProviderEvent.SessionComplete"/> ends the
    /// session even when the client has NOT asked to stop yet.
    ///
    /// True for a vendor whose completion signal is emitted once, at the end of
    /// the session: xAI's <c>transcript.done</c> and HyperWhisper Cloud's
    /// <c>session_complete</c> (the backend only forwards that once the client
    /// stopped). Deepgram, ElevenLabs and OpenAI map nothing to SessionComplete at
    /// all, so this flag cannot reach them — which is why the default is the
    /// pre-existing unconditional behaviour and no other strategy changes.
    ///
    /// FALSE for a vendor that emits it at every TURN boundary — Gemini's
    /// <c>serverContent.generationComplete</c> fires at each pause in speech, and
    /// a terminal reading silently ends a live dictation at the first one. The
    /// backend models exactly this rule in
    /// <c>hyperwhisper-cloud/src/routes/ws-streaming-shared.ts</c> (a 'complete'
    /// upstream event closes the session only once <c>stopRequested</c>), and so
    /// does the shared .NET stack —
    /// <c>ILiveTranscriptionProtocol.CompleteEndsSessionBeforeStop</c> in
    /// HyperWhisper.SharedCore, whose name and semantics this deliberately mirrors.
    /// </summary>
    bool CompleteEndsSessionBeforeStop => true;
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
