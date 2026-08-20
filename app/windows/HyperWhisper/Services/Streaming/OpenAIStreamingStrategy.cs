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

    /// <summary>
    /// Minimum amount of appended audio a commit frame is allowed to cover.
    /// OpenAI Realtime rejects <c>input_audio_buffer.commit</c> with
    /// "buffer too small. Expected at least 100ms of audio" when less than 100 ms
    /// has been appended since the previous commit (HYPERWHISPER-S8 /
    /// HYPERWHISPER-S9). 0.12 keeps a 20% margin over that server rule so a
    /// single short resampler chunk cannot leave us one buffer under the line.
    /// </summary>
    private const double MinimumCommitSeconds = 0.12;

    /// <summary>16-bit mono PCM.</summary>
    private const int BytesPerSample = 2;

    private readonly Dictionary<string, string> _committedItemTranscripts = new();
    private readonly Dictionary<string, string> _partialItemTranscripts = new();
    private readonly object _pendingAudioLock = new();
    private DateTimeOffset _lastCommitTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Bytes of PCM appended since the last commit frame was sent. A plain
    /// "did any audio arrive" flag is not enough: the send-opportunity hook runs
    /// BEFORE the append, so right after a periodic commit exactly one capture
    /// buffer is outstanding — and that buffer can be short. Counting bytes is
    /// what lets both the periodic path and the stop sequence answer "how much",
    /// not just "whether". Guarded by <see cref="_pendingAudioLock"/> because it
    /// is written from the NAudio capture thread and read during stop.
    /// </summary>
    private long _pendingAudioBytes;

    public string TranscriptionProviderLabel => "OpenAI (Streaming)";
    public bool SupportsVocabulary => false;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 24000;

    /// <summary>
    /// <see cref="MinimumCommitSeconds"/> expressed in bytes at
    /// <see cref="AudioSampleRate"/>: 5760 @ 24 kHz. The server's own 100 ms
    /// floor is 4800 bytes.
    /// </summary>
    private long MinimumCommitBytes => (long)(AudioSampleRate * BytesPerSample * MinimumCommitSeconds);

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
        ResetPendingAudio();
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
        NotePendingAudio(pcmData.Length);
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

    public IReadOnlyList<StreamingStopStep> GetStopSequence()
    {
        var steps = new List<StreamingStopStep>(3);

        // COMMIT ONLY WHAT THE SERVER WILL ACCEPT:
        // A stop that lands shortly after a periodic commit leaves a tail of
        // under 100 ms outstanding, and committing that is rejected outright
        // ("buffer too small", HYPERWHISPER-S8 / HYPERWHISPER-S9) — which the
        // client surfaces as a spurious streaming-error toast. Dropping a tail
        // that short is the accepted trade: it is silence-or-a-syllable, and it
        // used to be lost to the rejection anyway.
        if (TryConsumeCommittableAudio())
        {
            steps.Add(new StreamingStopStep(StreamingStopAction.SendMessage, CommitFrame, WebSocketMessageType.Text));
        }

        // KEEP THE WAIT EVEN WHEN NOTHING WAS COMMITTED:
        // The receive loop is still live at this point, and the
        // conversation.item.input_audio_transcription.completed for the LAST
        // PERIODIC commit can still be in flight — exactly the timing window
        // this bug lives in. Closing immediately would trade the toast for a
        // truncated transcript.
        steps.Add(new StreamingStopStep(StreamingStopAction.Wait, WaitAfter: TimeSpan.FromSeconds(1)));
        steps.Add(new StreamingStopStep(StreamingStopAction.Close));

        return steps;
    }

    public Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        if (DateTimeOffset.UtcNow - _lastCommitTime < CommitInterval)
            return Task.CompletedTask;

        // Deliberately leaves _lastCommitTime stale when the threshold is not
        // met: that is what makes the commit fire on the next chunk that clears
        // it, rather than waiting out another full interval.
        if (!TryConsumeCommittableAudio())
            return Task.CompletedTask;

        _lastCommitTime = DateTimeOffset.UtcNow;
        return webSocketSendAsync(CommitFrame, WebSocketMessageType.Text, cancellationToken);
    }

    /// <summary>Record PCM bytes handed to the WebSocket since the last commit.</summary>
    private void NotePendingAudio(int byteCount)
    {
        lock (_pendingAudioLock)
        {
            _pendingAudioBytes += byteCount;
        }
    }

    /// <summary>
    /// Claim the accumulated audio for a commit frame. Returns true (and zeroes
    /// the counter) only when enough has accumulated to clear the server's
    /// minimum. The check and the reset happen under one lock so the periodic
    /// path and the stop sequence can never both claim the same bytes and emit
    /// two commits for one buffer.
    /// </summary>
    private bool TryConsumeCommittableAudio()
    {
        lock (_pendingAudioLock)
        {
            if (_pendingAudioBytes < MinimumCommitBytes)
                return false;

            _pendingAudioBytes = 0;
            return true;
        }
    }

    /// <summary>Drop any accumulated audio — a new session starts with an empty buffer.</summary>
    private void ResetPendingAudio()
    {
        lock (_pendingAudioLock)
        {
            _pendingAudioBytes = 0;
        }
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
