using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Streaming;

public sealed class HyperWhisperCloudStreamingStrategy : IStreamingProviderStrategy
{
    private const string StreamingEndpoint = "wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram";

    public string TranscriptionProviderLabel => "HyperWhisper Cloud (Streaming)";
    public bool SupportsVocabulary => true;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 16000;
    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => [];

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.LicenseKey))
        {
            query.Add($"license_key={Uri.EscapeDataString(config.LicenseKey)}");
        }
        else if (!string.IsNullOrWhiteSpace(config.DeviceId))
        {
            query.Add($"device_id={Uri.EscapeDataString(config.DeviceId)}");
        }
        else
        {
            LoggingService.Warn("HyperWhisperCloudStreamingStrategy: missing license key and device ID");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(config.Language) && config.Language != "auto")
        {
            query.Add($"language={Uri.EscapeDataString(config.Language)}");
        }

        if (!string.IsNullOrWhiteSpace(config.Vocabulary) &&
            !string.IsNullOrWhiteSpace(config.Language) &&
            config.Language != "auto")
        {
            query.Add($"vocabulary={Uri.EscapeDataString(config.Vocabulary)}");
        }

        return new Uri(query.Count == 0 ? StreamingEndpoint : $"{StreamingEndpoint}?{string.Join("&", query)}");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData) =>
        (pcmData, WebSocketMessageType.Binary);

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ServerMessage>(text);
            return message?.Type switch
            {
                "ready" => new StreamingProviderEvent.SessionStarted(message.SessionId),
                "transcript" when !string.IsNullOrEmpty(message.Text) && message.IsFinal == true =>
                    new StreamingProviderEvent.FinalTranscript(message.Text),
                "transcript" when !string.IsNullOrEmpty(message.Text) =>
                    new StreamingProviderEvent.PartialTranscript(message.Text),
                "session_complete" => new StreamingProviderEvent.SessionComplete(
                    message.DurationSeconds ?? 0,
                    message.CreditsUsed ?? 0),
                "error" => new StreamingProviderEvent.Error(message.Message ?? "Unknown server error"),
                "warning" => new StreamingProviderEvent.Warning(message.Message ?? "Server warning", message.RemainingSeconds),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"HyperWhisperCloudStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        new StreamingStopStep(
            StreamingStopAction.SendMessage,
            Encoding.UTF8.GetBytes("{\"type\":\"stop\"}"),
            WebSocketMessageType.Text),
        new StreamingStopStep(StreamingStopAction.WaitForSessionComplete, WaitAfter: TimeSpan.FromSeconds(10)),
        new StreamingStopStep(StreamingStopAction.Close)
    ];

    public Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    private sealed class ServerMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("is_final")]
        public bool? IsFinal { get; set; }

        [JsonPropertyName("duration_seconds")]
        public double? DurationSeconds { get; set; }

        [JsonPropertyName("credits_used")]
        public double? CreditsUsed { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("remaining_seconds")]
        public double? RemainingSeconds { get; set; }
    }
}
