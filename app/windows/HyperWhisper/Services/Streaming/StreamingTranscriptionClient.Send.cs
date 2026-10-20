using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace HyperWhisper.Services.Streaming;

// Writing to the socket: audio, the strategy's start messages, and the normal close.
public sealed partial class StreamingTranscriptionClient
{
    public async Task SendAudioAsync(byte[] pcmData, CancellationToken cancellationToken = default)
    {
        if (pcmData.Length == 0)
            return;

        var webSocket = _webSocket;
        if (webSocket?.State != WebSocketState.Open)
            return;

        await _strategy.OnAudioSendOpportunityAsync(SendEncodedAsync, cancellationToken);

        var encoded = _strategy.EncodeAudioChunk(pcmData);
        await SendEncodedAsync(encoded.Data, encoded.Type, cancellationToken);
    }

    private async Task SendStartMessagesAsync(CancellationToken cancellationToken)
    {
        foreach (var (data, type) in _strategy.GetStartMessages(_config))
        {
            await SendEncodedAsync(data, type, cancellationToken);
        }
    }

    private async Task SendEncodedAsync(
        byte[] data,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken
    )
    {
        var webSocket = _webSocket;
        if (webSocket?.State != WebSocketState.Open)
            return;

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);
        await _sendLock.WaitAsync(linkedCts.Token);

        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    messageType,
                    endOfMessage: true,
                    linkedCts.Token
                );
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseWebSocketAsync(CancellationToken cancellationToken)
    {
        var webSocket = _webSocket;
        if (webSocket == null)
            return;

        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "streaming session ended", linkedCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggingService.Warn($"StreamingTranscriptionClient: close failed - {ex.Message}");
        }
    }

    private CancellationTokenSource CreateLinkedSessionToken(CancellationToken cancellationToken)
    {
        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        return CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
    }
}
