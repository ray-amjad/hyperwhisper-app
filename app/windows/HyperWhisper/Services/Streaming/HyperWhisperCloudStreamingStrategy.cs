using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.Services.AppClassification;

namespace HyperWhisper.Services.Streaming;

public sealed class HyperWhisperCloudStreamingStrategy : IStreamingProviderStrategy
{
    private const string StreamingHost = "wss://transcribe-prod-v2.hyperwhisper.com";

    /// <summary>
    /// The tier whose route reproduces the endpoint this class hard-coded before the
    /// live tier picker existed. Anything unrecognised lands back here.
    /// </summary>
    public const string DefaultCloudTier = "deepgramNova3";

    private readonly string _sttProvider;

    public HyperWhisperCloudStreamingStrategy() : this(DefaultCloudTier) { }

    /// <param name="cloudTier">
    /// A cloud-stt-catalog.json entry id (the global streamingCloudTier setting).
    /// This is a path selector only - it is deliberately not a
    /// StreamingTranscriptionProvider case, because the credit and entitlement
    /// wiring keys off provider == hyperwhisperCloud.
    /// </param>
    public HyperWhisperCloudStreamingStrategy(string? cloudTier)
    {
        _sttProvider = ResolveSttProvider(cloudTier);
    }

    /// <summary>
    /// The route is DERIVED, never a table: /ws/streaming-{sttProvider}, where
    /// sttProvider comes from the catalog entry the tier names. deepgramNova3 gives
    /// /ws/streaming-deepgram, byte-identical to the literal this replaced, so every
    /// installed client keeps working; geminiTranscribe gives
    /// /ws/streaming-gemini-transcribe. A tier the catalog does not know falls back
    /// to Deepgram rather than deriving a path the backend will 404.
    /// </summary>
    internal static string ResolveSttProvider(string? cloudTier)
    {
        var tier = string.IsNullOrWhiteSpace(cloudTier) ? DefaultCloudTier : cloudTier.Trim();
        var eligible = CloudSttCatalog.Shared.StreamingCloudTierEntries();
        if (!eligible.Any(entry => string.Equals(entry.Id, tier, StringComparison.OrdinalIgnoreCase)))
        {
            tier = DefaultCloudTier;
        }
        return CloudSttCatalog.Shared.SttProviderForId(tier) ?? "deepgram";
    }

    /// <summary>
    /// Whether this tier's live vendor needs an explicit language before it will
    /// honour vocabulary terms. True for Deepgram Nova-3, which silently drops
    /// keyterms in auto-detect; false for Gemini, which accepts custom_vocabulary
    /// there (verified live). Applying the Deepgram rule to every tier would delete
    /// the headline feature for auto-detect users on the Gemini tier.
    /// </summary>
    public static bool TierRequiresLanguageForVocabulary(string? cloudTier) =>
        string.Equals(ResolveSttProvider(cloudTier), "deepgram", StringComparison.Ordinal);

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

        // Withholding vocabulary in auto-detect is a DEEPGRAM constraint (Nova-3
        // silently drops keyterms with no language), not a HyperWhisper Cloud one.
        // Gemini accepts custom_vocabulary in auto-detect, and vocabulary is the
        // whole reason to pick that tier.
        var hasLanguage = !string.IsNullOrWhiteSpace(config.Language) && config.Language != "auto";
        if (!string.IsNullOrWhiteSpace(config.Vocabulary) &&
            (hasLanguage || !RequiresLanguageForVocabulary))
        {
            query.Add($"vocabulary={Uri.EscapeDataString(config.Vocabulary)}");
        }

        var endpoint = $"{StreamingHost}/ws/streaming-{_sttProvider}";
        return new Uri(query.Count == 0 ? endpoint : $"{endpoint}?{string.Join("&", query)}");
    }

    private bool RequiresLanguageForVocabulary =>
        string.Equals(_sttProvider, "deepgram", StringComparison.Ordinal);

    /// <summary>
    /// Carries the platform + app version headers into the WebSocket handshake.
    /// Auth stays in the query string (see BuildWebSocketUri); these headers
    /// exist only so the handshake carries the same client identity as the
    /// POST /transcribe path. Note the backend does not record it yet:
    /// /ws/streaming-deepgram emits no structured log lines, so nothing calls
    /// readClientInfo there.
    /// </summary>
    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        ClientInfoHeaders.Apply(webSocket);
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
