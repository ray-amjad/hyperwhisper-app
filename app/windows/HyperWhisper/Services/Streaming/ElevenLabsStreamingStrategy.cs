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

public sealed class ElevenLabsStreamingStrategy : IStreamingProviderStrategy
{
    public string TranscriptionProviderLabel => "ElevenLabs (Streaming)";
    public bool SupportsVocabulary => false;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 16000;
    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => [];

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LoggingService.Warn("ElevenLabsStreamingStrategy: missing API key");
            return null;
        }

        var query = new List<string>
        {
            "model_id=scribe_v2_realtime",
            "audio_format=pcm_16000",
            "commit_strategy=vad",
            "vad_silence_threshold_secs=1.5",
            "vad_threshold=0.4"
        };

        if (!string.IsNullOrWhiteSpace(config.Language) && config.Language != "auto")
        {
            query.Add($"language_code={Uri.EscapeDataString(NormalizeLanguageCode(config.Language))}");
        }

        return new Uri($"wss://api.elevenlabs.io/v1/speech-to-text/realtime?{string.Join("&", query)}");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            webSocket.Options.SetRequestHeader("xi-api-key", config.ApiKey);
        }
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData)
    {
        var payload = new
        {
            message_type = "input_audio_chunk",
            audio_base_64 = Convert.ToBase64String(pcmData),
            commit = false,
            sample_rate = 16000
        };

        return (Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text);
    }

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ElevenLabsMessage>(text);
            if (message == null) return null;

            return message.MessageType switch
            {
                "session_started" => new StreamingProviderEvent.SessionStarted(null),
                "partial_transcript" when !string.IsNullOrEmpty(message.Text) =>
                    new StreamingProviderEvent.PartialTranscript(message.Text),
                "committed_transcript" when !string.IsNullOrEmpty(message.Text) =>
                    new StreamingProviderEvent.FinalTranscript(message.Text),
                "auth_error" => new StreamingProviderEvent.Error("ElevenLabs authentication failed. Please check your API key in the Model Library API keys manager."),
                "quota_exceeded" => new StreamingProviderEvent.Error("ElevenLabs quota exceeded. Please check your account billing."),
                "rate_limited" => new StreamingProviderEvent.Error("ElevenLabs rate limit reached. Please try again in a moment."),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ElevenLabsStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        new StreamingStopStep(StreamingStopAction.Close)
    ];

    public Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    private static string NormalizeLanguageCode(string code)
    {
        var trimmed = code.Trim();
        var separatorIndex = trimmed.IndexOf('-');
        return separatorIndex > 0 ? trimmed[..separatorIndex] : trimmed;
    }

    private sealed class ElevenLabsMessage
    {
        [JsonPropertyName("message_type")]
        public string? MessageType { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
