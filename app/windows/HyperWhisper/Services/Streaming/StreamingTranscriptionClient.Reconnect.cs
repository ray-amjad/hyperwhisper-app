using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace HyperWhisper.Services.Streaming;

// The reconnect policy: up to MaxReconnectAttempts with a linear backoff, stopping early on a refused upgrade.
public sealed partial class StreamingTranscriptionClient
{
    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        var uri = _strategy.BuildWebSocketUri(_config);
        if (uri == null)
            return false;

        ChangeState(StreamingConnectionState.Reconnecting);
        SentryService.AddBreadcrumb(
            "streaming_reconnect_started",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = _strategy.TranscriptionProviderLabel });

        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested ||
                State is StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle)
            {
                return false;
            }

            ClientWebSocket? webSocket = null;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);

                webSocket = new ClientWebSocket();
                return await AdoptReconnectedSocketAsync(webSocket, uri, attempt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Same ownership rule as HandleFailedReconnectAttempt: a socket this attempt
                // built and never adopted has no other reference to release it.
                if (webSocket != null && !ReferenceEquals(webSocket, _webSocket))
                    webSocket.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                if (HandleFailedReconnectAttempt(webSocket, attempt, ex))
                    return false;
            }
        }

        GiveUpReconnect();
        return false;
    }

    private async Task<bool> AdoptReconnectedSocketAsync(ClientWebSocket webSocket, Uri uri, int attempt, CancellationToken cancellationToken)
    {
        _strategy.ConfigureWebSocket(webSocket, _config);
        webSocket.Options.CollectHttpResponseDetails = true;
        await webSocket.ConnectAsync(uri, cancellationToken);

        _webSocket?.Dispose();
        _webSocket = webSocket;
        await SendStartMessagesAsync(cancellationToken);

        var reconnectedState = _strategy.SessionStartsOnWebSocketOpen
            ? StreamingConnectionState.Streaming
            : StreamingConnectionState.Ready;

        // Same race as StartAsync's Connecting -> Streaming transition: something else
        // (e.g. StopAsync moving to Disconnecting, or a concurrent terminal close) can
        // land between the awaits above and here. TryChangeState only commits if we're
        // still in the state this branch expects, and reports whether it landed.
        if (!TryChangeState(StreamingConnectionState.Reconnecting, reconnectedState))
        {
            LoggingService.Warn(
                $"StreamingTranscriptionClient: reconnect raced into {State} instead of {reconnectedState} for {_strategy.TranscriptionProviderLabel}");
            return false;
        }

        SentryService.AddBreadcrumb(
            "streaming_reconnect_succeeded",
            "audio.streaming",
            data: new Dictionary<string, string>
            {
                ["provider"] = _strategy.TranscriptionProviderLabel,
                ["attempt"] = attempt.ToString()
            });
        LoggingService.Info($"StreamingTranscriptionClient: reconnect succeeded on attempt {attempt}");
        return true;
    }

    // One failed attempt: log it, release the socket it built, and report whether the server
    // refused the upgrade outright - true ends the reconnect with that refusal as the error.
    private bool HandleFailedReconnectAttempt(ClientWebSocket? webSocket, int attempt, Exception ex)
    {
        LoggingService.Warn($"StreamingTranscriptionClient: reconnect attempt {attempt} failed - {ex.Message}");

        var refusal = TerminalUpgradeMessage(webSocket);

        // Release the socket this attempt built, unless the attempt got
        // far enough to adopt it as _webSocket (ConnectAsync succeeded
        // and SendStartMessagesAsync then threw). Disposing it in that
        // case would hand the next attempt, or StopAsync, a dead socket.
        if (webSocket != null && !ReferenceEquals(webSocket, _webSocket))
            webSocket.Dispose();

        // A balance that ran out mid-session refuses every reconnect the
        // same way, so the remaining attempts are two more seconds spent
        // waiting for an answer that cannot change — ending on
        // "connection was lost", which sends the user to look at their
        // network. Stop on the first refusal and name it instead.
        if (refusal != null)
        {
            LoggingService.Warn($"StreamingTranscriptionClient: reconnect refused outright - {refusal}");
            Raise(ErrorReceived, refusal);
            _sessionStartedTcs?.TrySetException(new InvalidOperationException(refusal));
            TryChangeStateUnless(
                StreamingConnectionState.Error,
                StreamingConnectionState.Disconnecting,
                StreamingConnectionState.Idle);
            return true;
        }

        return false;
    }

    // Every attempt failed without a refusal: the connection is lost for good.
    private void GiveUpReconnect()
    {
        SentryService.AddBreadcrumb(
            "streaming_reconnect_failed",
            "audio.streaming",
            data: new Dictionary<string, string> { ["provider"] = _strategy.TranscriptionProviderLabel });
        Raise(ErrorReceived, "Streaming connection was lost and could not be restored.");
        _sessionStartedTcs?.TrySetException(new InvalidOperationException("Streaming connection was lost and could not be restored."));

        // TryReconnectAsync's per-attempt loop only checks Disconnecting/Idle at the top of each iteration -
        // StopAsync can still win the race in the gap between the last attempt and here.
        TryChangeStateUnless(
            StreamingConnectionState.Error,
            StreamingConnectionState.Disconnecting,
            StreamingConnectionState.Idle);
    }
}
