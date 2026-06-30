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

public sealed class XaiStreamingStrategy : IStreamingProviderStrategy
{
    private static readonly HashSet<string> SupportedFormattingLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi"
    };

    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tl"] = "fil"
    };

    private string _committedTranscript = string.Empty;

    public string TranscriptionProviderLabel => "xAI (Streaming)";
    public bool SupportsVocabulary => false;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 16000;
    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) => [];

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LoggingService.Warn("XaiStreamingStrategy: missing API key");
            return null;
        }

        var query = new List<string>
        {
            "sample_rate=16000",
            "encoding=pcm",
            "interim_results=true",
            "endpointing=300"
        };

        var language = SupportedFormattingLanguage(config.Language);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }

        return new Uri($"wss://api.x.ai/v1/stt?{string.Join("&", query)}");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");
        }
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData) =>
        (pcmData, WebSocketMessageType.Binary);

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<XaiMessage>(text);
            if (message == null) return null;

            return message.Type switch
            {
                "transcript.created" => new StreamingProviderEvent.SessionStarted(null),
                "transcript.partial" => ParseTranscriptPartial(message),
                "transcript.done" => ParseTranscriptDone(message),
                "error" => new StreamingProviderEvent.Error(message.Message ?? "xAI streaming transcription failed"),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"XaiStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        TextStep("{\"type\":\"audio.done\"}"),
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

    private StreamingProviderEvent? ParseTranscriptPartial(XaiMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return null;

        if (message.IsFinal == true)
        {
            var delta = CommittedDelta(message.Text);
            return string.IsNullOrWhiteSpace(delta)
                ? null
                : new StreamingProviderEvent.FinalTranscript(delta);
        }

        return new StreamingProviderEvent.PartialTranscript(message.Text);
    }

    private StreamingProviderEvent ParseTranscriptDone(XaiMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            var delta = CommittedDelta(message.Text);
            if (!string.IsNullOrWhiteSpace(delta))
            {
                return new StreamingProviderEvent.FinalTranscriptAndSessionComplete(
                    delta,
                    message.Duration ?? 0,
                    0
                );
            }
        }

        return new StreamingProviderEvent.SessionComplete(message.Duration ?? 0, 0);
    }

    private string? CommittedDelta(string transcript)
    {
        var normalized = transcript.Trim();
        if (normalized.Length == 0)
            return null;

        if (_committedTranscript.Length == 0)
        {
            _committedTranscript = normalized;
            return normalized;
        }

        if (normalized.StartsWith(_committedTranscript, StringComparison.Ordinal))
        {
            var suffix = normalized[_committedTranscript.Length..].Trim();
            _committedTranscript = normalized;
            return suffix.Length == 0 ? null : suffix;
        }

        if (_committedTranscript.StartsWith(normalized, StringComparison.Ordinal))
            return null;

        _committedTranscript += " " + normalized;
        return normalized;
    }

    private static string? SupportedFormattingLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized == "auto")
            return null;

        var separatorIndex = normalized.IndexOf('-');
        var primary = separatorIndex > 0 ? normalized[..separatorIndex] : normalized;
        var aliased = LanguageAliases.GetValueOrDefault(primary, primary);
        return SupportedFormattingLanguages.Contains(aliased) ? aliased : null;
    }

    private static StreamingStopStep TextStep(string json) =>
        new(StreamingStopAction.SendMessage, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    private sealed class XaiMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("is_final")]
        public bool? IsFinal { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
