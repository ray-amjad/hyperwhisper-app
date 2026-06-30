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

public sealed class OpenAIStreamingStrategy : IStreamingProviderStrategy
{
    private const string Model = "gpt-realtime-whisper";

    private static class EventType
    {
        public const string SessionUpdate = "session.update";
        public const string AppendAudio = "input_audio_buffer.append";
        public const string CommitAudio = "input_audio_buffer.commit";
        public const string SessionUpdated = "session.updated";
        public const string TranscriptionDelta = "conversation.item.input_audio_transcription.delta";
        public const string TranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
        public const string Error = "error";
    }

    private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(1.2);
    private static readonly byte[] CommitFrame = Encoding.UTF8.GetBytes($"{{\"type\":\"{EventType.CommitAudio}\"}}");

    private readonly Dictionary<string, string> _committedItemTranscripts = new();
    private readonly Dictionary<string, string> _partialItemTranscripts = new();
    private DateTimeOffset _lastCommitTime = DateTimeOffset.UtcNow;
    private bool _hasUncommittedAudio;

    public string TranscriptionProviderLabel => "OpenAI (Streaming)";
    public bool SupportsVocabulary => false;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 24000;

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        return new Uri("wss://api.openai.com/v1/realtime?intent=transcription");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");
        }
    }

    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config)
    {
        _committedItemTranscripts.Clear();
        _partialItemTranscripts.Clear();
        _hasUncommittedAudio = false;
        _lastCommitTime = DateTimeOffset.UtcNow;

        var transcription = new Dictionary<string, object?>
        {
            ["model"] = Model
        };

        var language = NormalizeLanguageCode(config.Language);
        if (!string.IsNullOrWhiteSpace(language))
        {
            transcription["language"] = language;
        }

        var payload = new
        {
            type = EventType.SessionUpdate,
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = AudioSampleRate
                        },
                        transcription,
                        turn_detection = (object?)null
                    }
                }
            }
        };

        return [(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text)];
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData)
    {
        _hasUncommittedAudio = true;
        var payload = new
        {
            type = EventType.AppendAudio,
            audio = Convert.ToBase64String(pcmData)
        };

        return (Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), WebSocketMessageType.Text);
    }

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<OpenAIRealtimeMessage>(text);
            if (message == null) return null;

            return message.Type switch
            {
                EventType.SessionUpdated => new StreamingProviderEvent.SessionStarted(message.Session?.Id),
                EventType.TranscriptionDelta when !string.IsNullOrEmpty(message.Delta) => ParseDelta(message),
                EventType.TranscriptionCompleted => ParseCompleted(message),
                EventType.Error => new StreamingProviderEvent.Error(message.Error?.Message ?? "OpenAI Realtime transcription failed"),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"OpenAIStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        new StreamingStopStep(StreamingStopAction.SendMessage, CommitFrame, WebSocketMessageType.Text),
        new StreamingStopStep(StreamingStopAction.Wait, WaitAfter: TimeSpan.FromSeconds(1)),
        new StreamingStopStep(StreamingStopAction.Close)
    ];

    public Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        if (!_hasUncommittedAudio || DateTimeOffset.UtcNow - _lastCommitTime < CommitInterval)
            return Task.CompletedTask;

        _hasUncommittedAudio = false;
        _lastCommitTime = DateTimeOffset.UtcNow;
        return webSocketSendAsync(CommitFrame, WebSocketMessageType.Text, cancellationToken);
    }

    private StreamingProviderEvent? ParseCompleted(OpenAIRealtimeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ItemId) || string.IsNullOrWhiteSpace(message.Transcript))
            return null;

        var delta = CommittedDelta(message.ItemId, message.Transcript);
        return string.IsNullOrWhiteSpace(delta) ? null : new StreamingProviderEvent.FinalTranscript(delta);
    }

    private StreamingProviderEvent PartialTranscript(string text) =>
        new StreamingProviderEvent.PartialTranscript(text);

    private StreamingProviderEvent ParseDelta(OpenAIRealtimeMessage message)
    {
        if (string.IsNullOrEmpty(message.ItemId))
            return PartialTranscript(message.Delta!);

        _partialItemTranscripts.TryGetValue(message.ItemId, out var existing);
        var partial = string.Concat(existing, message.Delta);
        _partialItemTranscripts[message.ItemId] = partial;
        return PartialTranscript(partial);
    }

    private string? CommittedDelta(string itemId, string transcript)
    {
        var normalized = transcript.Trim();
        if (normalized.Length == 0)
            return null;

        _committedItemTranscripts.TryGetValue(itemId, out var previous);
        _committedItemTranscripts[itemId] = normalized;
        _partialItemTranscripts.Remove(itemId);

        if (string.IsNullOrEmpty(previous))
            return normalized;

        if (normalized.StartsWith(previous, StringComparison.Ordinal))
        {
            var suffix = normalized[previous.Length..].Trim();
            return suffix.Length == 0 ? null : suffix;
        }

        return normalized;
    }

    private static string? NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized == "auto")
            return null;

        var separatorIndex = normalized.IndexOf('-');
        return separatorIndex > 0 ? normalized[..separatorIndex] : normalized;
    }

    private sealed class OpenAIRealtimeMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("session")]
        public OpenAIRealtimeSession? Session { get; set; }

        [JsonPropertyName("item_id")]
        public string? ItemId { get; set; }

        [JsonPropertyName("delta")]
        public string? Delta { get; set; }

        [JsonPropertyName("transcript")]
        public string? Transcript { get; set; }

        [JsonPropertyName("error")]
        public OpenAIRealtimeError? Error { get; set; }
    }

    private sealed class OpenAIRealtimeSession
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class OpenAIRealtimeError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
