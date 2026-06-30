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

public sealed class DeepgramStreamingStrategy : IStreamingProviderStrategy
{
    private static readonly TimeSpan KeepAliveThreshold = TimeSpan.FromSeconds(3);
    private readonly object _lastAudioLock = new();
    private DateTime _lastAudioSentTime = DateTime.UtcNow;

    public string TranscriptionProviderLabel => "Deepgram (Streaming)";
    public bool SupportsVocabulary => true;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 16000;
    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => [];

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LoggingService.Warn("DeepgramStreamingStrategy: missing API key");
            return null;
        }

        var requestedModel = string.IsNullOrWhiteSpace(config.Model) ? "nova-3-general" : config.Model;
        var model = CloudTranscriptionModels.ResolveDeepgramModelAlias(requestedModel);
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(model)}",
            "encoding=linear16",
            "sample_rate=16000",
            "channels=1",
            "smart_format=true",
            "punctuate=true",
            "filler_words=true",
            $"no_delay={(config.FastFormatting ? "true" : "false")}",
            "endpointing=300",
            "utterance_end_ms=1500",
            "interim_results=true",
            "vad_events=true",
            "mip_opt_out=true"
        };

        var hasExplicitLanguage = !string.IsNullOrWhiteSpace(config.Language) && config.Language != "auto";
        if (hasExplicitLanguage)
        {
            query.Add($"language={Uri.EscapeDataString(config.Language!)}");
        }
        else
        {
            query.Add("detect_language=true");
        }

        if (hasExplicitLanguage && !string.IsNullOrWhiteSpace(config.Vocabulary))
        {
            foreach (var term in config.Vocabulary.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                query.Add($"keyterm={Uri.EscapeDataString(term)}");
            }
        }

        return new Uri($"wss://api.deepgram.com/v1/listen?{string.Join("&", query)}");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return;

        webSocket.Options.AddSubProtocol("token");
        webSocket.Options.AddSubProtocol(config.ApiKey);
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData) =>
        (pcmData, WebSocketMessageType.Binary);

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<DeepgramMessage>(text);
            if (message == null) return null;

            return message.Type switch
            {
                "Metadata" => new StreamingProviderEvent.SessionStarted(message.RequestId),
                "Results" => ParseResults(message),
                "UtteranceEnd" or "SpeechStarted" => new StreamingProviderEvent.Metadata(text),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"DeepgramStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        TextStep("{\"type\":\"Finalize\"}"),
        new StreamingStopStep(StreamingStopAction.Wait, WaitAfter: TimeSpan.FromMilliseconds(500)),
        TextStep("{\"type\":\"CloseStream\"}"),
        new StreamingStopStep(StreamingStopAction.Close)
    ];

    public async Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        var now = DateTime.UtcNow;
        bool shouldKeepAlive;

        lock (_lastAudioLock)
        {
            shouldKeepAlive = now - _lastAudioSentTime > KeepAliveThreshold;
            _lastAudioSentTime = now;
        }

        if (shouldKeepAlive)
        {
            await webSocketSendAsync(
                Encoding.UTF8.GetBytes("{\"type\":\"KeepAlive\"}"),
                WebSocketMessageType.Text,
                cancellationToken
            );
        }
    }

    private static StreamingStopStep TextStep(string json) =>
        new(StreamingStopAction.SendMessage, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    private static StreamingProviderEvent? ParseResults(DeepgramMessage message)
    {
        var transcript = message.Channel?.Alternatives?.Count > 0
            ? message.Channel.Alternatives[0].Transcript
            : null;

        if (string.IsNullOrEmpty(transcript))
            return null;

        return message.IsFinal == true
            ? new StreamingProviderEvent.FinalTranscript(transcript)
            : new StreamingProviderEvent.PartialTranscript(transcript);
    }

    private sealed class DeepgramMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("channel")]
        public DeepgramChannel? Channel { get; set; }

        [JsonPropertyName("is_final")]
        public bool? IsFinal { get; set; }
    }

    private sealed class DeepgramChannel
    {
        [JsonPropertyName("alternatives")]
        public List<DeepgramAlternative>? Alternatives { get; set; }
    }

    private sealed class DeepgramAlternative
    {
        [JsonPropertyName("transcript")]
        public string? Transcript { get; set; }
    }
}
