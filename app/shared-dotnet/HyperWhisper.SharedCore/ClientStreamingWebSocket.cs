using System.Buffers;
using System.Net.WebSockets;

namespace HyperWhisper.SharedCore;

internal sealed class ClientStreamingWebSocket : IStreamingWebSocket
{
    private const int ReceiveChunkBytes = 16 * 1024;
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(
        StreamingWebSocketConnectOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var header in options.Headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }
        foreach (var protocol in options.SubProtocols)
        {
            _socket.Options.AddSubProtocol(protocol);
        }
        await _socket.ConnectAsync(options.Uri, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(data, messageType, true, cancellationToken).AsTask();

    public async Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(ReceiveChunkBytes);
        ValueWebSocketReceiveResult result;
        do
        {
            var memory = writer.GetMemory(ReceiveChunkBytes);
            result = await _socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new StreamingWebSocketFrame(
                    [],
                    WebSocketMessageType.Close,
                    true,
                    _socket.CloseStatus);
            }
            if (writer.WrittenCount + result.Count > MaxMessageBytes)
            {
                throw new InvalidDataException("Streaming provider message exceeded the 1 MiB limit.");
            }
            writer.Advance(result.Count);
        }
        while (!result.EndOfMessage);

        return new StreamingWebSocketFrame(writer.WrittenSpan.ToArray(), result.MessageType);
    }

    public async Task CloseAsync(WebSocketCloseStatus status, CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(status, string.Empty, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
