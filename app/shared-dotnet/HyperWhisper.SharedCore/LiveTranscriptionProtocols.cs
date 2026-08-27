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
        LiveTranscriptionProvider.GeminiTranscribe => new GeminiTranscribeLiveProtocol(config),
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

    /// <summary>
    /// Like <see cref="Language"/> but region-PRESERVING: "auto"/empty still
    /// mean auto-detect, everything else is passed through trimmed.
    ///
    /// Only Gemini 3.5 Transcribe Live uses this. Every other live provider here
    /// takes a bare subtag, so <see cref="Language"/> flattens "en-GB" to "en"
    /// for them; Gemini accepts both forms (verified live) and flattening would
    /// throw away a region the user deliberately picked.
    /// </summary>
    internal static string? LanguageCode(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    /// <summary>
    /// Per-protocol vocabulary terms: the shared core sanitizes, drops empties
    /// and de-duplicates case-insensitively; the per-protocol length drop and
    /// term cap stay here, in that order, because each protocol owns them.
    ///
    /// The core is asked for an UNCAPPED list on purpose. Capping inside it
    /// would apply <paramref name="count"/> before the <paramref name="chars"/>
    /// filter and silently shorten the result.
    ///
    /// BEHAVIOUR CHANGE: <paramref name="chars"/> is now measured against the
    /// SANITIZED term, and sanitizing can shorten a term three different ways,
    /// so the surviving set differs from the old raw trim-only filter at EVERY
    /// value of <paramref name="chars"/> — including 50:
    ///
    /// <list type="bullet">
    /// <item>Truncation at 80 characters. With <paramref name="chars"/> above
    /// 80 (Deepgram and HyperWhisper Cloud, both at 100) a long term now
    /// arrives truncated instead of whole (81-100 chars) or instead of being
    /// dropped (over 100 chars).</item>
    /// <item>Dropping <c>&lt;</c>/<c>&gt;</c> shrinks a term, so one that used
    /// to be dropped for length can now pass: <c>"&lt;"</c> + 50 <c>'a'</c> is
    /// 51 raw characters and was dropped at <paramref name="chars"/> = 50
    /// (Grok); it sanitizes to exactly 50 and is now sent.</item>
    /// <item>Collapsing whitespace runs shrinks a term the same way: 40
    /// <c>'a'</c> + 20 spaces + 8 <c>'b'</c> is 68 raw characters and was
    /// dropped at 50; it collapses to 49 and is now sent.</item>
    /// </list>
    ///
    /// That is the intended direction — sanitizing before egress is the point
    /// of routing this through the core, and the raw term was never the thing
    /// worth measuring. Do NOT "restore" the old set by adding a pre-sanitize
    /// length check.
    /// </summary>
    internal static IReadOnlyList<string> Vocabulary(LiveTranscriptionConfig config, int count, int chars) =>
        [.. SharedCoreBridge.NormalizeVocabularyTerms(config.Vocabulary, null)
            .Where(value => value.Length <= chars)
            .Take(count)];

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

/// <summary>
/// Gemini 3.5 Transcribe Live over <c>BidiGenerateContent</c> (BYOK).
///
/// Wire shape is pinned by <c>shared-conformance/live-frame-vectors.json</c> and
/// built in <c>hw-net/src/providers/gemini_transcribe.rs</c>; this is the .NET
/// mirror. Two things it is easy to get wrong:
///
/// TRAP 3 — the transcription config goes at <c>setup.input_audio_transcription</c>.
/// The pre-recorded position (<c>setup.generation_config.transcription_config</c>)
/// closes the socket with 1007, which is terminal, not retryable.
///
/// NOT Grok-shaped, despite looking like it. <c>interimInputTranscription</c> is
/// cumulative WITHIN a turn and restarts after each final, and
/// <c>inputTranscription</c> carries only that turn's committed text — the same
/// contract Deepgram has. So interim maps to Partial (a replacement preview) and
/// <c>inputTranscription</c> maps straight to Final (an append-me delta). Do NOT
/// copy <see cref="GrokLiveProtocol"/>'s <c>Delta()</c> prefix-diffing here:
/// Grok is cumulative across the whole SESSION, and diffing Gemini would chop
/// the head off every utterance after the first.
/// </summary>
internal sealed class GeminiTranscribeLiveProtocol : ILiveTranscriptionProtocol
{
    private const string ModelPrefix = "models/";
    private const string DefaultModel = "gemini-3.5-transcribe-live";
    private const string AudioMimeType = "audio/pcm;rate=16000";

    public GeminiTranscribeLiveProtocol(LiveTranscriptionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        ConnectOptions = new(
            new Uri(
                "wss://generativelanguage.googleapis.com/ws/" +
                "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" +
                $"?key={Uri.EscapeDataString(config.ApiKey)}"),
            new Dictionary<string, string>(),
            []);

        var model = string.IsNullOrWhiteSpace(config.Model) ? DefaultModel : config.Model.Trim();
        if (!model.StartsWith(ModelPrefix, StringComparison.Ordinal))
        {
            model = ModelPrefix + model;
        }

        var transcription = new Dictionary<string, object?>();
        var language = LiveTranscriptionProtocolFactory.LanguageCode(config.Language);
        if (language is not null)
        {
            transcription["language_codes"] = new[] { language };
        }
        // Vocabulary is NOT gated on an explicit language: Gemini accepts
        // custom_vocabulary in auto-detect mode (that gate is a Deepgram Nova-3
        // constraint). Never send diarization_mode or timestamp_granularities
        // alongside it — each is a hard rejection. See TRAP 2.
        var vocabulary = LiveTranscriptionProtocolFactory.Vocabulary(config, 100, 80);
        if (vocabulary.Count > 0)
        {
            transcription["custom_vocabulary"] = vocabulary;
        }

        StartFrames =
        [
            LiveTranscriptionProtocolFactory.Text(JsonSerializer.Serialize(new
            {
                setup = new { model, input_audio_transcription = transcription },
            })),
        ];
    }

    public LiveTranscriptionProvider Provider => LiveTranscriptionProvider.GeminiTranscribe;
    public int SampleRate => 16000;
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames { get; }

    /// <remarks>
    /// Google does NOT close the socket after <c>audio_stream_end</c> — measured
    /// 54 s of silence — so this budget is the whole stop path, not a courtesy
    /// wait on an upstream close that never comes.
    /// </remarks>
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(5);

    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm) =>
        LiveTranscriptionProtocolFactory.Text(JsonSerializer.Serialize(new
        {
            realtime_input = new
            {
                audio = new { data = Convert.ToBase64String(pcm), mime_type = AudioMimeType },
            },
        }));

    public IReadOnlyList<LiveProtocolFrame> StopFrames() =>
        [LiveTranscriptionProtocolFactory.Text("{\"realtime_input\":{\"audio_stream_end\":true}}")];

    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        if (root.TryGetProperty("setupComplete", out _))
        {
            return new(LiveProtocolEventKind.Started);
        }
        if (root.TryGetProperty("error", out var error))
        {
            return new(LiveProtocolEventKind.Error, ErrorCode: ErrorCodeFor(error));
        }
        if (!root.TryGetProperty("serverContent", out var content) ||
            content.ValueKind != JsonValueKind.Object)
        {
            return new(LiveProtocolEventKind.Ignore);
        }

        var final = TranscriptionText(content, "inputTranscription");
        if (!string.IsNullOrWhiteSpace(final))
        {
            return new(LiveProtocolEventKind.Final, final);
        }
        var partial = TranscriptionText(content, "interimInputTranscription");
        if (!string.IsNullOrWhiteSpace(partial))
        {
            return new(LiveProtocolEventKind.Partial, partial);
        }
        if (LiveTranscriptionProtocolFactory.Boolean(content, "generationComplete") == true)
        {
            return new(LiveProtocolEventKind.Complete);
        }
        return new(LiveProtocolEventKind.Ignore);
    }

    private static string? TranscriptionText(JsonElement content, string name) =>
        content.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Object
            ? LiveTranscriptionProtocolFactory.String(node, "text")
            : null;

    /// <summary>
    /// A bad Google API key arrives as the string "API key not valid", never as
    /// a 401 — and a malformed setup frame arrives as close 1007. Both are
    /// terminal: retrying either just reproduces it.
    /// </summary>
    private static LiveTranscriptionFailureCode ErrorCodeFor(JsonElement error)
    {
        var message = LiveTranscriptionProtocolFactory.String(error, "message") ?? string.Empty;
        if (message.Contains("api key not valid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("api_key_invalid", StringComparison.OrdinalIgnoreCase))
        {
            return LiveTranscriptionFailureCode.Unauthorized;
        }
        var code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
        return code is 1007 or 400 || message.Contains("setup", StringComparison.OrdinalIgnoreCase)
            ? LiveTranscriptionFailureCode.InvalidRequest
            : LiveTranscriptionFailureCode.ProviderUnavailable;
    }
}

internal sealed class HyperWhisperCloudLiveProtocol : ILiveTranscriptionProtocol
{
    /// <summary>
    /// The tier whose route reproduces the path this class hard-coded before the
    /// live tier picker existed. Anything unrecognised lands back here.
    /// </summary>
    internal const string DefaultCloudTier = "deepgramNova3";

    /// <summary>
    /// The route is DERIVED, never a table: <c>/ws/streaming-{sttProvider}</c>,
    /// where <c>sttProvider</c> comes from the catalog entry the user's
    /// <c>streaming.cloudTier</c> setting names. <c>deepgramNova3</c> →
    /// <c>deepgram</c> → <c>/ws/streaming-deepgram</c>, byte-identical to the old
    /// literal, so every installed client keeps working; <c>geminiTranscribe</c>
    /// → <c>gemini-transcribe</c> → <c>/ws/streaming-gemini-transcribe</c>.
    ///
    /// The tier is canonicalised through the core first, so a persisted retired
    /// id (<c>googleChirp3</c>) resolves rather than silently falling back.
    /// </summary>
    internal static string RoutePath(string? cloudTier) => $"/ws/streaming-{RouteProvider(cloudTier)}";

    internal static string RouteProvider(string? cloudTier)
    {
        var tier = SharedCoreBridge.CanonicalCloudSttTier(
            string.IsNullOrWhiteSpace(cloudTier) ? DefaultCloudTier : cloudTier);
        return SharedCoreBridge.CloudSttProvider(tier)
            ?? SharedCoreBridge.CloudSttProvider(DefaultCloudTier)
            ?? "deepgram";
    }

    /// <summary>
    /// Whether the tier's live vendor accepts vocabulary terms without an
    /// explicit language. Deepgram Nova-3 silently drops <c>keyterm</c> in
    /// auto-detect mode, which is why this class withholds them there; Gemini
    /// accepts <c>custom_vocabulary</c> in auto-detect (verified live), and
    /// vocabulary is the whole point of that tier, so withholding them there
    /// would silently remove the headline feature for auto-detect users.
    /// </summary>
    private static bool AllowsVocabularyWithoutLanguage(string provider) =>
        !string.Equals(provider, "deepgram", StringComparison.Ordinal);

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
        var provider = RouteProvider(config.CloudTier);
        var language = LiveTranscriptionProtocolFactory.Language(config.Language);
        if (language is not null)
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }
        if (language is not null || AllowsVocabularyWithoutLanguage(provider))
        {
            var vocabulary = string.Join(", ", LiveTranscriptionProtocolFactory.Vocabulary(config, 100, 100));
            if (vocabulary.Length > 0)
            {
                query.Add($"vocabulary={Uri.EscapeDataString(vocabulary)}");
            }
        }
        ConnectOptions = new(
            new Uri($"wss://transcribe-prod-v2.hyperwhisper.com/ws/streaming-{provider}?{string.Join("&", query)}"),
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
