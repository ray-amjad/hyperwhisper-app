using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Gemini 3.5 Transcribe Live (BYOK) over Google's BidiGenerateContent WebSocket.
///
/// Wire shape is pinned by shared-conformance/live-frame-vectors.json and built in
/// shared-core-rs/crates/hw-net/src/providers/gemini_transcribe.rs. This is the
/// Windows mirror; the shared .NET mirror is GeminiTranscribeLiveProtocol in
/// HyperWhisper.SharedCore (Windows deliberately does not use that stack).
///
/// TRAP: the live transcription config lives at <c>setup.input_audio_transcription</c>.
/// The pre-recorded position (<c>setup.generation_config.transcription_config</c>,
/// which IS correct for POST /v1beta/interactions) makes the server close the socket
/// with 1007 - the same object at two different paths for two models of one family.
///
/// This is Deepgram-shaped, NOT xAI-shaped, despite the JSON framing looking like
/// xAI's. <c>interimInputTranscription</c> is cumulative only WITHIN a turn and
/// restarts after each final, and <c>inputTranscription</c> carries only that turn's
/// committed text. So interim is a replacement preview and inputTranscription is an
/// append-me delta. Do NOT copy XaiStreamingStrategy.CommittedDelta() prefix-diffing
/// here: xAI's transcript is cumulative across the whole SESSION, and diffing Gemini
/// would chop the head off every utterance after the first.
/// </summary>
public sealed class GeminiStreamingStrategy : IStreamingProviderStrategy
{
    private const string ModelPrefix = "models/";
    private const string LiveModel = "gemini-3.5-transcribe-live";
    private const string AudioMimeType = "audio/pcm;rate=16000";

    // Google's live vocabulary caps, mirroring the batch path in the Rust core
    // (`gemini_transcribe::custom_vocabulary`).
    private const int MaxVocabularyTerms = 100;
    private const int MaxVocabularyChars = 80;

    public string TranscriptionProviderLabel => "Gemini 3.5 Transcribe (Streaming)";
    public bool SupportsVocabulary => true;
    public bool SessionStartsOnWebSocketOpen => false;
    public int AudioSampleRate => 16000;

    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LoggingService.Warn("GeminiStreamingStrategy: missing API key");
            return null;
        }

        // Google authenticates the live socket by query parameter; it rejects the
        // Authorization header form that the REST endpoints accept.
        return new Uri(
            "wss://generativelanguage.googleapis.com/ws/" +
            "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" +
            $"?key={Uri.EscapeDataString(config.ApiKey)}");
    }

    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        // Intentionally empty: auth is in the query string, and Google rejects the
        // handshake outright if an unexpected Authorization header is present.
    }

    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config) =>
        [(Encoding.UTF8.GetBytes(BuildSetupFrame(config)), WebSocketMessageType.Text)];

    /// <summary>
    /// Exactly the frame in shared-conformance/live-frame-vectors.json. Kept
    /// internal (not inlined into GetStartMessages) so HyperWhisper.SmokeTests can
    /// assert the JSON without standing up a socket.
    /// </summary>
    internal static string BuildSetupFrame(StreamingSessionConfig config)
    {
        var model = string.IsNullOrWhiteSpace(config.Model) ? LiveModel : config.Model.Trim();
        if (!model.StartsWith(ModelPrefix, StringComparison.Ordinal))
        {
            model = ModelPrefix + model;
        }

        var transcription = new JsonObject();

        // Region-PRESERVING, unlike every other provider here. Deepgram and xAI take
        // a bare subtag so their strategies flatten "en-GB" to "en"; Gemini accepts
        // the qualified form (verified live) and flattening would silently throw
        // away a region the user deliberately picked.
        var language = config.Language?.Trim();
        if (!string.IsNullOrWhiteSpace(language) &&
            !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            transcription["language_codes"] = new JsonArray(JsonValue.Create(language));
        }

        // NOT gated on an explicit language. "No vocabulary without a language" is a
        // Deepgram Nova-3 constraint; Gemini accepts custom_vocabulary in auto-detect
        // mode, and vocabulary is the headline reason to pick this provider.
        //
        // Never send diarization_mode or timestamp_granularities alongside it: Google
        // rejects the pair outright ("custom_vocabulary is incompatible with ...").
        var terms = VocabularyTerms(config.Vocabulary);
        if (terms.Count > 0)
        {
            transcription["custom_vocabulary"] = new JsonArray([.. terms.Select(term => (JsonNode?)JsonValue.Create(term))]);
        }

        var frame = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = model,
                ["input_audio_transcription"] = transcription
            }
        };
        return frame.ToJsonString();
    }

    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData)
    {
        var frame = new JsonObject
        {
            ["realtime_input"] = new JsonObject
            {
                ["audio"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(pcmData),
                    ["mime_type"] = AudioMimeType
                }
            }
        };
        return (Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text);
    }

    public StreamingProviderEvent? ParseMessage(string text)
    {
        try
        {
            var message = JsonSerializer.Deserialize<GeminiMessage>(text);
            if (message == null) return null;

            if (message.SetupComplete != null)
            {
                return new StreamingProviderEvent.SessionStarted(null);
            }
            if (message.Error != null)
            {
                return new StreamingProviderEvent.Error(
                    message.Error.Message ?? "Gemini streaming transcription failed");
            }

            var content = message.ServerContent;
            if (content == null) return null;

            // Final first: a frame may in principle carry both, and the committed
            // text is the one that must reach the document.
            var final = content.InputTranscription?.Text;
            if (!string.IsNullOrWhiteSpace(final))
            {
                return new StreamingProviderEvent.FinalTranscript(final);
            }
            var partial = content.InterimInputTranscription?.Text;
            if (!string.IsNullOrWhiteSpace(partial))
            {
                return new StreamingProviderEvent.PartialTranscript(partial);
            }
            if (content.GenerationComplete == true)
            {
                // Google reports no duration or credit figures on this route; BYOK
                // sessions are not metered by HyperWhisper either way.
                return new StreamingProviderEvent.SessionComplete(0, 0);
            }
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"GeminiStreamingStrategy: failed to parse message: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<StreamingStopStep> GetStopSequence() =>
    [
        new StreamingStopStep(
            StreamingStopAction.SendMessage,
            Encoding.UTF8.GetBytes("{\"realtime_input\":{\"audio_stream_end\":true}}"),
            WebSocketMessageType.Text),
        // Google does NOT close the socket after audio_stream_end - measured 54 s of
        // silence - so this wait is the whole stop budget, not a courtesy pause on an
        // upstream close that is coming. Shorter than the 10 s the other providers
        // use precisely because waiting longer buys nothing here.
        new StreamingStopStep(StreamingStopAction.WaitForSessionComplete, WaitAfter: TimeSpan.FromSeconds(5)),
        new StreamingStopStep(StreamingStopAction.Close)
    ];

    public Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    // No IsTerminalCloseCode override. The interface default already covers the two
    // codes this provider actually produces: 1007 for a rejected setup frame (the
    // TRAP above) and 1011 for an upstream fault. A bad API key arrives as an
    // in-band `error` frame carrying "API key not valid", never as a close code, and
    // ParseMessage turns that into a StreamingProviderEvent.Error above.

    private static IReadOnlyList<string> VocabularyTerms(string? vocabulary)
    {
        if (string.IsNullOrWhiteSpace(vocabulary))
        {
            return [];
        }

        return
        [
            .. vocabulary
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(term => term.Length <= MaxVocabularyChars)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxVocabularyTerms)
        ];
    }

    private sealed class GeminiMessage
    {
        [JsonPropertyName("setupComplete")]
        public JsonElement? SetupComplete { get; set; }

        [JsonPropertyName("serverContent")]
        public GeminiServerContent? ServerContent { get; set; }

        [JsonPropertyName("error")]
        public GeminiError? Error { get; set; }
    }

    private sealed class GeminiServerContent
    {
        [JsonPropertyName("interimInputTranscription")]
        public GeminiTranscription? InterimInputTranscription { get; set; }

        [JsonPropertyName("inputTranscription")]
        public GeminiTranscription? InputTranscription { get; set; }

        [JsonPropertyName("generationComplete")]
        public bool? GenerationComplete { get; set; }
    }

    private sealed class GeminiTranscription
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class GeminiError
    {
        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
