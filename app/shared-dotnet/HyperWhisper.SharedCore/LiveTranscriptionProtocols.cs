using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HyperWhisper.SharedCore;

internal enum LiveProtocolEventKind
{
    Started,
    Partial,
    Final,
    Complete,
    Error,
    Ignore,
}

internal sealed record LiveProtocolEvent(
    LiveProtocolEventKind Kind,
    string? Text = null,
    LiveTranscriptionFailureCode ErrorCode = LiveTranscriptionFailureCode.Unknown);

internal sealed record LiveProtocolFrame(byte[] Data, WebSocketMessageType Type);

internal interface ILiveTranscriptionProtocol
{
    LiveTranscriptionProvider Provider { get; }
    int SampleRate { get; }
    StreamingWebSocketConnectOptions ConnectOptions { get; }
    IReadOnlyList<LiveProtocolFrame> StartFrames { get; }
    LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm);
    IReadOnlyList<LiveProtocolFrame> AudioOpportunityFrames() => [];
    IReadOnlyList<LiveProtocolFrame> StopFrames();
    TimeSpan DrainTimeout { get; }
    LiveProtocolEvent Parse(ReadOnlyMemory<byte> message);
}

internal static class LiveTranscriptionProtocolFactory
{
    public static ILiveTranscriptionProtocol Create(LiveTranscriptionConfig config) => config.Provider switch
    {
        LiveTranscriptionProvider.Deepgram => new DeepgramLiveProtocol(config),
        LiveTranscriptionProvider.ElevenLabs => new ElevenLabsLiveProtocol(config),
        LiveTranscriptionProvider.OpenAi => new OpenAiLiveProtocol(config),
        LiveTranscriptionProvider.Grok => new GrokLiveProtocol(config),
        LiveTranscriptionProvider.HyperWhisperCloud => new HyperWhisperCloudLiveProtocol(config),
        _ => throw new ArgumentOutOfRangeException(nameof(config)),
    };

    internal static string? Language(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var normalized = value.Trim().ToLowerInvariant();
        var separator = normalized.IndexOf('-');
        return separator > 0 ? normalized[..separator] : normalized;
    }

    internal static IReadOnlyList<string> Vocabulary(LiveTranscriptionConfig config, int count, int chars) =>
        config.Vocabulary?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= chars)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToArray() ?? [];

    internal static LiveProtocolFrame Text(string value) =>
        new(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text);

    internal static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static bool? Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

internal sealed class DeepgramLiveProtocol : ILiveTranscriptionProtocol
{
    private static readonly TimeSpan KeepAliveThreshold = TimeSpan.FromSeconds(3);
    private DateTimeOffset _lastAudio = DateTimeOffset.UtcNow;

    public DeepgramLiveProtocol(LiveTranscriptionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(string.IsNullOrWhiteSpace(config.Model) ? "nova-3-general" : config.Model)}",
            "encoding=linear16", "sample_rate=16000", "channels=1", "smart_format=true",
            "punctuate=true", "filler_words=true", $"no_delay={config.FastFormatting.ToString().ToLowerInvariant()}",
            "endpointing=300", "utterance_end_ms=1500", "interim_results=true", "vad_events=true", "mip_opt_out=true",
        };
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        query.Add(language is null ? "detect_language=true" : $"language={Uri.EscapeDataString(language)}");
        if (language is not null)
        {
            query.AddRange(LiveTranscriptionProtocolFactory.Vocabulary(config, 100, 100)
                .Select(value => $"keyterm={Uri.EscapeDataString(value)}"));
        }
        ConnectOptions = new(
            new Uri($"wss://api.deepgram.com/v1/listen?{string.Join("&", query)}"),
            new Dictionary<string, string>(),
            ["token", config.ApiKey]);
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.Deepgram;
    public int SampleRate => 16000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames => [];
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(2);
    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm) => new(pcm.ToArray(), WebSocketMessageType.Binary);
    public IReadOnlyList<LiveProtocolFrame> StopFrames() =>
        [LiveTranscriptionProtocolFactory.Text("{\"type\":\"Finalize\"}"), LiveTranscriptionProtocolFactory.Text("{\"type\":\"CloseStream\"}")];

    public IReadOnlyList<LiveProtocolFrame> AudioOpportunityFrames()
    {
        var now = DateTimeOffset.UtcNow;
        var shouldKeepAlive = now - _lastAudio > KeepAliveThreshold;
        _lastAudio = now;
        return shouldKeepAlive
            ? [LiveTranscriptionProtocolFactory.Text("{\"type\":\"KeepAlive\"}")]
            : [];
    }

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        return LiveTranscriptionProtocolFactory.String(root, "type") switch
        {
            "Metadata" => new(LiveProtocolEventKind.Started),
            "Results" => ParseResult(root),
            _ => new(LiveProtocolEventKind.Ignore),
        };
    }

    private static LiveProtocolEvent ParseResult(JsonElement root)
    {
        if (!root.TryGetProperty("channel", out var channel)
            || channel.ValueKind != JsonValueKind.Object
            || !channel.TryGetProperty("alternatives", out var alternatives)
            || alternatives.ValueKind != JsonValueKind.Array
            || alternatives.GetArrayLength() == 0)
        {
            return new(LiveProtocolEventKind.Ignore);
        }
        var text = LiveTranscriptionProtocolFactory.String(alternatives[0], "transcript");
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(LiveProtocolEventKind.Ignore);
        }
        return LiveTranscriptionProtocolFactory.Boolean(root, "is_final") == true
            ? new(LiveProtocolEventKind.Final, text)
            : new(LiveProtocolEventKind.Partial, text);
    }
}

internal sealed class ElevenLabsLiveProtocol : ILiveTranscriptionProtocol
{
    public ElevenLabsLiveProtocol(LiveTranscriptionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        var query = new List<string>
        {
            "model_id=scribe_v2_realtime", "audio_format=pcm_16000", "commit_strategy=vad",
            "vad_silence_threshold_secs=1.5", "vad_threshold=0.4",
        };
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        if (language is not null)
        {
            query.Add($"language_code={Uri.EscapeDataString(language)}");
        }
        ConnectOptions = new(
            new Uri($"wss://api.elevenlabs.io/v1/speech-to-text/realtime?{string.Join("&", query)}"),
            new Dictionary<string, string> { ["xi-api-key"] = config.ApiKey },
            []);
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.ElevenLabs;
    public int SampleRate => 16000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames => [];
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(1);
    public IReadOnlyList<LiveProtocolFrame> StopFrames() => [];

    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm) => LiveTranscriptionProtocolFactory.Text(
        JsonSerializer.Serialize(new
        {
            message_type = "input_audio_chunk",
            audio_base_64 = Convert.ToBase64String(pcm),
            commit = false,
            sample_rate = 16000,
        }));

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var text = LiveTranscriptionProtocolFactory.String(root, "text");
        return LiveTranscriptionProtocolFactory.String(root, "message_type") switch
        {
            "session_started" => new(LiveProtocolEventKind.Started),
            "partial_transcript" when !string.IsNullOrWhiteSpace(text) => new(LiveProtocolEventKind.Partial, text),
            "committed_transcript" when !string.IsNullOrWhiteSpace(text) => new(LiveProtocolEventKind.Final, text),
            "auth_error" => new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.Unauthorized),
            "quota_exceeded" => new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.QuotaExceeded),
            "rate_limited" => new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.RateLimited),
            _ => new(LiveProtocolEventKind.Ignore),
        };
    }
}

internal sealed class OpenAiLiveProtocol : ILiveTranscriptionProtocol
{
    private const int MinimumCommitBytes = 4800;
    private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(1.2);
    private readonly Dictionary<string, string> _committed = [];
    private readonly Dictionary<string, string> _partials = [];
    private DateTimeOffset _lastCommit = DateTimeOffset.UtcNow;
    private long _pendingBytes;

    public OpenAiLiveProtocol(LiveTranscriptionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        ConnectOptions = new(
            new Uri("wss://api.openai.com/v1/realtime?intent=transcription"),
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {config.ApiKey}" },
            []);
        var transcription = new Dictionary<string, object?> { ["model"] = "gpt-realtime-whisper" };
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        if (language is not null)
        {
            transcription["language"] = language;
        }
        StartFrames =
        [
            LiveTranscriptionProtocolFactory.Text(JsonSerializer.Serialize(new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new
                    {
                        input = new
                        {
                            format = new { type = "audio/pcm", rate = 24000 },
                            transcription,
                            turn_detection = (object?)null,
                        },
                    },
                },
            })),
        ];
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.OpenAi;
    public int SampleRate => 24000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames { get; }
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(2);

    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm)
    {
        _pendingBytes += pcm.Length;
        return LiveTranscriptionProtocolFactory.Text(JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm),
        }));
    }

    public IReadOnlyList<LiveProtocolFrame> StopFrames()
    {
        if (_pendingBytes < MinimumCommitBytes)
        {
            return [];
        }
        _pendingBytes = 0;
        return [LiveTranscriptionProtocolFactory.Text("{\"type\":\"input_audio_buffer.commit\"}")];
    }

    public IReadOnlyList<LiveProtocolFrame> AudioOpportunityFrames()
    {
        if (_pendingBytes < MinimumCommitBytes || DateTimeOffset.UtcNow - _lastCommit < CommitInterval)
        {
            return [];
        }
        _pendingBytes = 0;
        _lastCommit = DateTimeOffset.UtcNow;
        return [LiveTranscriptionProtocolFactory.Text("{\"type\":\"input_audio_buffer.commit\"}")];
    }

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = LiveTranscriptionProtocolFactory.String(root, "type");
        if (type == "session.updated")
        {
            return new(LiveProtocolEventKind.Started);
        }
        if (type == "error")
        {
            return new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.ProviderUnavailable);
        }
        var item = LiveTranscriptionProtocolFactory.String(root, "item_id") ?? string.Empty;
        if (type == "conversation.item.input_audio_transcription.delta")
        {
            var delta = LiveTranscriptionProtocolFactory.String(root, "delta");
            if (string.IsNullOrWhiteSpace(delta)) return new(LiveProtocolEventKind.Ignore);
            _partials.TryGetValue(item, out var previousPartial);
            var partial = string.Concat(previousPartial, delta);
            _partials[item] = partial;
            return new(LiveProtocolEventKind.Partial, partial);
        }
        if (type != "conversation.item.input_audio_transcription.completed")
        {
            return new(LiveProtocolEventKind.Ignore);
        }
        var transcript = LiveTranscriptionProtocolFactory.String(root, "transcript")?.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new(LiveProtocolEventKind.Ignore);
        }
        _committed.TryGetValue(item, out var previous);
        _committed[item] = transcript;
        _partials.Remove(item);
        var final = previous is not null && transcript.StartsWith(previous, StringComparison.Ordinal)
            ? transcript[previous.Length..].Trim()
            : transcript;
        return string.IsNullOrWhiteSpace(final)
            ? new(LiveProtocolEventKind.Ignore)
            : new(LiveProtocolEventKind.Final, final);
    }
}

internal sealed class GrokLiveProtocol : ILiveTranscriptionProtocol
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "cs", "da", "de", "en", "es", "fa", "fil", "fr", "hi",
        "id", "it", "ja", "ko", "mk", "ms", "nl", "pl", "pt", "ro",
        "ru", "sv", "th", "tr", "vi",
    };
    private string _committed = string.Empty;

    public GrokLiveProtocol(LiveTranscriptionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        var query = new List<string> { "sample_rate=16000", "encoding=pcm", "interim_results=true", "endpointing=300" };
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        if (language == "tl") language = "fil";
        if (language is not null && !SupportedLanguages.Contains(language)) language = null;
        if (language is not null)
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }
        query.AddRange(LiveTranscriptionProtocolFactory.Vocabulary(config, 100, 50)
            .Select(value => $"keyterm={Uri.EscapeDataString(value)}"));
        ConnectOptions = new(
            new Uri($"wss://api.x.ai/v1/stt?{string.Join("&", query)}"),
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {config.ApiKey}" },
            []);
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.Grok;
    public int SampleRate => 16000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames => [];
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(10);
    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm) => new(pcm.ToArray(), WebSocketMessageType.Binary);
    public IReadOnlyList<LiveProtocolFrame> StopFrames() => [LiveTranscriptionProtocolFactory.Text("{\"type\":\"audio.done\"}")];

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = LiveTranscriptionProtocolFactory.String(root, "type");
        var text = LiveTranscriptionProtocolFactory.String(root, "text");
        if (type == "transcript.created") return new(LiveProtocolEventKind.Started);
        if (type == "error") return new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.ProviderUnavailable);
        if (type == "transcript.done")
        {
            var delta = Delta(text);
            return string.IsNullOrWhiteSpace(delta)
                ? new(LiveProtocolEventKind.Complete)
                : new(LiveProtocolEventKind.Complete, delta);
        }
        if (type != "transcript.partial" || string.IsNullOrWhiteSpace(text))
        {
            return new(LiveProtocolEventKind.Ignore);
        }
        if (LiveTranscriptionProtocolFactory.Boolean(root, "is_final") == true)
        {
            var delta = Delta(text);
            return string.IsNullOrWhiteSpace(delta)
                ? new(LiveProtocolEventKind.Ignore)
                : new(LiveProtocolEventKind.Final, delta);
        }
        return new(LiveProtocolEventKind.Partial, text);
    }

    private string? Delta(string? text)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (_committed.Length == 0)
        {
            _committed = normalized;
            return normalized;
        }
        if (normalized.StartsWith(_committed, StringComparison.Ordinal))
        {
            var suffix = normalized[_committed.Length..].Trim();
            _committed = normalized;
            return suffix.Length == 0 ? null : suffix;
        }
        if (_committed.StartsWith(normalized, StringComparison.Ordinal)) return null;
        _committed += " " + normalized;
        return normalized;
    }
}

internal sealed class HyperWhisperCloudLiveProtocol : ILiveTranscriptionProtocol
{
    public HyperWhisperCloudLiveProtocol(LiveTranscriptionConfig config)
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
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        if (language is not null)
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
            var vocabulary = string.Join(", ", LiveTranscriptionProtocolFactory.Vocabulary(config, 100, 100));
            if (vocabulary.Length > 0)
            {
                query.Add($"vocabulary={Uri.EscapeDataString(vocabulary)}");
            }
        }
        ConnectOptions = new(
            new Uri($"wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-deepgram?{string.Join("&", query)}"),
            new Dictionary<string, string>(),
            []);
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.HyperWhisperCloud;
    public int SampleRate => 16000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames => [];
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(10);
    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm) => new(pcm.ToArray(), WebSocketMessageType.Binary);
    public IReadOnlyList<LiveProtocolFrame> StopFrames() => [LiveTranscriptionProtocolFactory.Text("{\"type\":\"stop\"}")];

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var text = LiveTranscriptionProtocolFactory.String(root, "text");
        return LiveTranscriptionProtocolFactory.String(root, "type") switch
        {
            "ready" => new(LiveProtocolEventKind.Started),
            "transcript" when !string.IsNullOrWhiteSpace(text) && LiveTranscriptionProtocolFactory.Boolean(root, "is_final") == true => new(LiveProtocolEventKind.Final, text),
            "transcript" when !string.IsNullOrWhiteSpace(text) => new(LiveProtocolEventKind.Partial, text),
            "session_complete" => new(LiveProtocolEventKind.Complete),
            "error" => new(LiveProtocolEventKind.Error, ErrorCode: LiveTranscriptionFailureCode.ProviderUnavailable),
            _ => new(LiveProtocolEventKind.Ignore),
        };
    }
}

internal sealed class LiveProtocolException : Exception
{
    private LiveProtocolException(LiveTranscriptionFailure failure) : base(failure.Message)
    {
        Failure = failure;
    }

    public LiveTranscriptionFailure Failure { get; }

    public static LiveProtocolException Unauthorized(LiveTranscriptionProvider provider) =>
        new(new LiveTranscriptionFailure(
            LiveTranscriptionFailureCode.Unauthorized,
            "A streaming provider credential is required.",
            provider));
}
