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
/// The seam between <see cref="StreamingTranscriptionClient"/> — which owns
/// transport, reconnect and state — and whatever decides what goes on the wire.
///
/// <para>
/// There used to be five implementations of this, one per provider. Since issue
/// #281 there is one, <see cref="LiveProtocolStreamingStrategy"/>, and it
/// answers from the shared Rust core. The interface is kept unchanged rather
/// than folded away: it is what let the five deletions land without touching the
/// thousand-line client, and it is the seam the smoke suite drives the client
/// through with no socket.
/// </para>
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
    ///
    /// The per-provider answer comes from the shared core
    /// (<c>hw_net::live::complete_ends_session_before_stop</c>), so this head,
    /// the Linux head and macOS cannot drift on it. The default here stays
    /// <c>true</c> for any strategy that is not core-backed.
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
