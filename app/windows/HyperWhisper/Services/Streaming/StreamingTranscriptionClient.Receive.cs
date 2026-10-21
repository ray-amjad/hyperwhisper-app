using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.Utilities;
// Aliased, not imported: HyperWhisper.SharedCore also declares
// CloudTranscriptionProvider, which would clash with HyperWhisper.Models.
using PortableLiveUpgradeRefusal = HyperWhisper.SharedCore.PortableLiveUpgradeRefusal;
using SharedCoreBridge = HyperWhisper.SharedCore.SharedCoreBridge;

namespace HyperWhisper.Services.Streaming;

// Reading the socket: the receive loop, message framing, and close-code classification.
public sealed partial class StreamingTranscriptionClient
{
    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];

        while (!cancellationToken.IsCancellationRequested &&
               webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var message = await ReceiveTextMessageAsync(webSocket, buffer, cancellationToken);
            if (message == null)
                break;

            HandleProviderEvent(_strategy.ParseMessage(message));
        }
    }

    private async Task RunReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var currentWebSocket = webSocket;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReceiveLoopAsync(currentWebSocket, cancellationToken);

                if (cancellationToken.IsCancellationRequested ||
                    State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle or StreamingConnectionState.Error ||
                    _receivedTerminalClose)
                {
                    return;
                }

                if (!await TryReconnectAsync(cancellationToken))
                    return;

                currentWebSocket = _webSocket!;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle)
                return;

            LoggingService.Warn($"StreamingTranscriptionClient: receive loop failed, attempting reconnect - {ex.Message}");

            if (!await TryReconnectAsync(cancellationToken))
            {
                LoggingService.Error("StreamingTranscriptionClient: receive loop failed", ex);
                Raise(ErrorReceived, ex.Message);

                // TryReconnectAsync's own await chain gives StopAsync a window to move to
                // Disconnecting/Idle concurrently - don't clobber that back to Error.
                TryChangeStateUnless(
                    StreamingConnectionState.Error,
                    StreamingConnectionState.Disconnecting,
                    StreamingConnectionState.Idle);
            }
        }
    }

    /// <summary>
    /// The message for an upgrade the server refused outright, or null when the
    /// failure is one a retry can still answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This head and macOS used to keep the same status table twice. Since issue
    /// #281 the classification is the shared core's
    /// (<c>hw_net::live::upgrade_refusal</c>) and only the two user-facing
    /// sentences below stay here, because they are this app's wording and name
    /// this app's Settings screen. It covers the gap the terminal-close handling
    /// below cannot: that only helps a user who runs out of credits
    /// <em>during</em> a session, while the same user one keypress later never
    /// opens a socket at all — HyperWhisper Cloud requires 30 seconds of balance
    /// and refuses the upgrade with 402 — and that path had only .NET's own
    /// "status code '402' when status code '101' was expected" to show for it.
    /// </para>
    /// <para>
    /// Requires <c>CollectHttpResponseDetails</c>, without which
    /// <c>HttpStatusCode</c> is 0 and this correctly finds nothing — 0 is not one
    /// of the refusal statuses.
    /// </para>
    /// </remarks>
    private static string? TerminalUpgradeMessage(ClientWebSocket? webSocket) => webSocket == null
        ? null
        : SharedCoreBridge.LiveUpgradeRefusal((int)webSocket.HttpStatusCode) switch
        {
            PortableLiveUpgradeRefusal.InsufficientCredits =>
                "Streaming could not start because credits are exhausted. Add more credits in Settings.",
            PortableLiveUpgradeRefusal.Unauthorized =>
                "Streaming could not start because the account key was refused. Check your key in Settings.",
            _ => null
        };

    private async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket webSocket,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                HandleCloseResult(result);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return stream.Length == 0 ? null : Encoding.UTF8.GetString(stream.ToArray());
    }

    // internal (not private): direct-call surface for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility change is intended.
    internal void HandleCloseResult(WebSocketReceiveResult result)
    {
        // No status at all is ambiguous (not a clear provider signal) - let it fall through to
        // the existing reconnect/backoff path in RunReceiveLoopAsync, same as today.
        if (result.CloseStatus is null)
            return;

        var closeCode = (int)result.CloseStatus.Value;

        // A clean close (1000) is never a provider error - it's either a graceful server-side
        // close or the server's echo of our own CloseAsync(NormalClosure, ...) during StopAsync.
        if (closeCode == (int)WebSocketCloseStatus.NormalClosure)
            return;

        // Only genuinely non-recoverable codes end the session immediately. 4001/4002 are
        // HyperWhisper's own app-level codes (credits exhausted / max session duration) and stay
        // here rather than in the strategy since they're not provider-specific. Everything else is
        // delegated to the strategy, whose default covers the standard fatal WebSocket protocol
        // codes (1002, 1003, 1007, 1008, 1009, 1011) - see IStreamingProviderStrategy for the full
        // list and rationale. Standard transient codes (1001 Going Away, 1006 Abnormal/no close
        // frame, 1012 Service Restart, 1013 Try Again Later, and anything else not explicitly known
        // to be fatal) fall through to the existing reconnect/backoff path in RunReceiveLoopAsync.
        if (!(closeCode is 4001 or 4002 || _strategy.IsTerminalCloseCode(closeCode)))
            return;

        // We're already mid/post our own intentional shutdown (StopAsync sets Disconnecting
        // before running the stop sequence, and _sessionCts isn't cancelled until after that
        // completes, so the receive loop can still reach here concurrently). TryChangeStateCore
        // performs the "are we shutting down" check and the write to Error atomically under
        // _stateLock, so it cannot race with StopAsync moving to Disconnecting/Idle in between -
        // and it doubles as the guard against reclassifying our own shutdown as a provider error.
        //
        // The bookkeeping in RecordTerminalClose (_receivedTerminalClose, the failure message, the
        // ErrorReceived event, and the startup-failure exception) is populated from the onSuccess callback,
        // which TryChangeStateCore guarantees runs before StateChanged(Error) is published - a
        // subscriber reacting synchronously to StateChanged, or a caller that just observed
        // State == Error, must never see that state ahead of the reason for it. onSuccess also
        // runs when State is already Error (e.g. a prior in-band provider error already got here
        // first) rather than only on an actual Disconnecting/Idle-guarded transition, so a
        // terminal close arriving after an in-band error still records _receivedTerminalClose
        // instead of silently no-op'ing and losing the last partial transcript.
        TryChangeStateCore(
            current => current is not (StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle),
            StreamingConnectionState.Error,
            onSuccess: () => RecordTerminalClose(closeCode, result));
    }

    // HandleCloseResult's onSuccess bookkeeping for a terminal close: runs before StateChanged(Error)
    // is published (see TryChangeStateCore).
    private void RecordTerminalClose(int closeCode, WebSocketReceiveResult result)
    {
        _receivedTerminalClose = true;
        var message = closeCode switch
        {
            4001 => "Streaming stopped because credits are exhausted.",
            4002 => "Streaming stopped because the maximum session duration was reached.",
            _ => string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                ? $"Streaming connection was closed by the provider (code {closeCode})."
                : $"Streaming connection was closed by the provider: {result.CloseStatusDescription}"
        };

        LoggingService.Warn($"StreamingTranscriptionClient: terminal server close {closeCode} ({result.CloseStatusDescription})");
        SentryService.AddBreadcrumb(
            "streaming_terminal_close",
            "audio.streaming",
            data: new Dictionary<string, string>
            {
                ["provider"] = _strategy.TranscriptionProviderLabel,
                ["closeCode"] = closeCode.ToString()
            });
        Raise(ErrorReceived, message);
        _sessionStartedTcs?.TrySetException(new InvalidOperationException(message));
    }
}
