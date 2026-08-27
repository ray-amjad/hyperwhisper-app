using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperWhisper.Models;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// THE ONE STREAMING STRATEGY. Every live provider this app speaks, spoken by
/// the shared Rust core (issue #281).
///
/// <para>
/// This replaces five hand-written <c>*StreamingStrategy</c> classes — Deepgram,
/// ElevenLabs, HyperWhisper Cloud, OpenAI and xAI — which were a line-by-line
/// re-write of the same five wire protocols that <c>shared-dotnet</c> and macOS
/// each implemented separately. Everything that decides what goes on the wire
/// (the query strings, the start frames, the framing, the parsers, Deepgram's
/// keepalive, OpenAI's commit gate and every stop sequence) now lives in
/// <c>hw_net::live</c> and reaches this head through
/// <see cref="ILiveTranscriptionProtocol"/>, the same seam the Linux head runs
/// on.
/// </para>
///
/// <para>
/// What is left here is a translation layer and nothing else: this app's
/// <see cref="StreamingSessionConfig"/> into the shared
/// <see cref="LiveTranscriptionConfig"/>, and the shared
/// <see cref="LiveProtocolEvent"/> / <see cref="LiveStopStep"/> back into this
/// app's <see cref="StreamingProviderEvent"/> / <see cref="StreamingStopStep"/>.
/// <see cref="IStreamingProviderStrategy"/> is unchanged, deliberately: keeping
/// the seam is what let the 1000-line <see cref="StreamingTranscriptionClient"/>
/// stay as it is.
/// </para>
///
/// <para>
/// AUDIO NEVER CROSSES THE FFI. The core answers a framing descriptor once, at
/// connect time, and <see cref="EncodeAudioChunk"/> does the base64 and the
/// concatenation here, on bytes this process already holds; the core is told a
/// byte COUNT and never the samples.
/// </para>
///
/// <para>
/// Disposable, because the underlying <c>HwLiveSession</c> is a handle onto a
/// Rust <c>Arc</c>. <see cref="StreamingTranscriptionClient.DisposeAsync"/>
/// disposes it; the session is otherwise held for the life of the process.
/// </para>
/// </summary>
internal sealed class LiveProtocolStreamingStrategy : IStreamingProviderStrategy, IDisposable
{
    private readonly Func<long> _nowMs;

    /// <summary>
    /// The session config, translated. Rebuilt from the config
    /// <see cref="BuildWebSocketUri"/> is handed, so the interface's "the caller
    /// supplies the config" contract holds rather than being quietly ignored.
    /// The client passes the same instance it was constructed with, so in the app
    /// this never changes.
    /// </summary>
    private LiveTranscriptionConfig _liveConfig;

    /// <summary>
    /// Serializes the <see cref="_protocol"/> REFERENCE — the lazy build in
    /// <see cref="Require"/>, the swap on the connect/reconnect path, and the
    /// disposal — together with the <see cref="_disposed"/> check that decides
    /// whether a build is still allowed. That is the whole of what it covers: the
    /// connect/reconnect path replaces the reference while the NAudio capture
    /// thread may be reading it, and reading a torn or already-disposed reference
    /// is the race this prevents.
    ///
    /// It is NOT held across the FFI call. <see cref="Require"/> returns the
    /// reference out of the lock, and every caller then invokes the protocol
    /// unlocked, so two threads can be inside the Rust object at once. That is
    /// deliberate and safe — the Rust object holds its own mutex — but it means
    /// this lock orders nothing about the calls themselves: a
    /// <c>Require().EncodeAudio(...)</c> can be in flight against a protocol that
    /// has since been swapped out or disposed, and it is the protocol's own
    /// lifetime (the handle stays alive for the duration of the call) rather than
    /// this gate that makes that survivable.
    /// </summary>
    private readonly object _gate = new();

    private ILiveTranscriptionProtocol? _protocol;
    private bool _disposed;

    /// <param name="nowMs">
    /// A monotonic millisecond clock. Injectable so the smoke suite can drive
    /// OpenAI's 1.2 s commit interval and Deepgram's 3 s keepalive without
    /// sleeping — the core reads no clock of its own, which is the property that
    /// makes those gates testable at all. Null means a stopwatch started here.
    /// </param>
    internal LiveProtocolStreamingStrategy(
        StreamingTranscriptionProvider provider,
        StreamingSessionConfig config,
        Func<long>? nowMs = null
    )
    {
        ArgumentNullException.ThrowIfNull(config);

        Provider = CoreProvider(provider);
        _liveConfig = ToLiveConfig(Provider, config);

        if (nowMs != null)
        {
            _nowMs = nowMs;
        }
        else
        {
            // One reading per client, not per connection: a reconnect reuses
            // this object, and the core's own per-connection state is reset by
            // BuildWebSocketUri below rather than by rewinding the clock.
            var stopwatch = Stopwatch.StartNew();
            _nowMs = () => stopwatch.ElapsedMilliseconds;
        }
    }

    /// <summary>The shared core's name for the provider this session speaks.</summary>
    internal LiveTranscriptionProvider Provider { get; }

    /// <summary>
    /// The HyperWhisper Cloud live tier whose route reproduces the endpoint this
    /// app hardcoded before the tier picker existed
    /// (<c>/ws/streaming-deepgram</c>). Anything unrecognised resolves back to
    /// it, so an installed client with a stale setting keeps working.
    ///
    /// Mirrors <c>DEFAULT_CLOUD_TIER</c> in
    /// <c>hw_net::live::hw_cloud</c>, which applies the same fallback when it
    /// derives the route. Kept here as well because the settings ComboBox needs
    /// the catalog's own CASING to select a row, which the core does not carry -
    /// <c>SmokeTests</c> pins that this id is in the live-eligible set.
    /// </summary>
    public const string DefaultCloudTier = "deepgramNova3";

    // All three answer from the shared capability table (issue #281, phase 1) and
    // need no credential and no session - which is what lets the settings page
    // read SupportsVocabulary with neither.
    public string TranscriptionProviderLabel => SharedCoreBridge.LiveProviderLabel(Provider);
    public bool SupportsVocabulary => SharedCoreBridge.LiveSupportsVocabulary(Provider);
    public int AudioSampleRate => SharedCoreBridge.LiveRequiredSampleRate(Provider);

    /// <summary>
    /// From the same capability table, so this head cannot drift from the Linux
    /// head or (once issue #326 lands) macOS. False for Gemini alone, whose
    /// <c>generationComplete</c> is a turn boundary rather than the end of the
    /// session - see the interface member for what reading it wrong costs.
    /// </summary>
    public bool CompleteEndsSessionBeforeStop =>
        SharedCoreBridge.LiveCompleteEndsSessionBeforeStop(Provider);

    /// <summary>
    /// Deepgram only. See <see cref="ILiveTranscriptionProtocol.SessionStartsOnOpen"/>
    /// - it comes off the core's connect descriptor rather than a second "is
    /// this Deepgram" list here.
    /// </summary>
    public bool SessionStartsOnWebSocketOpen => Require().SessionStartsOnOpen;

    /// <summary>
    /// The connect URL, and the point at which a fresh core session is built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client calls this once before each socket it opens — at the top of
    /// <c>StartAsync</c> and again at the top of <c>TryReconnectAsync</c> — so it
    /// is the natural place for the core's <c>connect()</c>, which also resets
    /// the per-connection state (OpenAI's pending-byte counter and commit clock,
    /// xAI's and OpenAI's committed transcripts, Deepgram's keepalive mark). The
    /// five deleted strategies reset that state in <c>GetStartMessages</c>
    /// instead; both run before any audio for a given socket, and this one also
    /// covers the providers that send no start message at all.
    /// </para>
    /// <para>
    /// Null on a missing credential, matching every strategy this replaces:
    /// <c>StartAsync</c> reads null as "cannot start" and returns false without
    /// opening a socket.
    /// </para>
    /// </remarks>
    public Uri? BuildWebSocketUri(StreamingSessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Re-read the caller's config rather than quietly reusing the
            // constructor's. The client hands over the same instance every time,
            // so in the app this changes nothing - but the interface says the
            // caller supplies the config, and a strategy that ignored it would
            // pin the language and the vocabulary at construction.
            _liveConfig = ToLiveConfig(Provider, config);
            _protocol?.Dispose();
            _protocol = null;

            try
            {
                _protocol = LiveTranscriptionProtocolFactory.Create(_liveConfig);
            }
            catch (LiveProtocolException ex)
            {
                LoggingService.Warn(
                    $"LiveProtocolStreamingStrategy: cannot open a {TranscriptionProviderLabel} session - {ex.Message}");
                return null;
            }

            return _protocol.ConnectOptions.Uri;
        }
    }

    /// <summary>
    /// Carries the core's handshake headers and subprotocols onto the socket.
    /// </summary>
    /// <remarks>
    /// The client-identity headers are deliberately NOT part of the core's
    /// descriptor — a shared core does not know which platform it is linked into
    /// or what the host app's version is — so this head adds them, for the one
    /// provider that has ever carried them. Everything else is the wire
    /// protocol's own: Deepgram's API key travels as the second subprotocol
    /// (browsers cannot set handshake headers, so Deepgram documents that
    /// route), OpenAI and xAI use <c>Authorization: Bearer</c>, ElevenLabs uses
    /// <c>xi-api-key</c>, and HyperWhisper Cloud authenticates in the query
    /// string.
    /// </remarks>
    public void ConfigureWebSocket(ClientWebSocket webSocket, StreamingSessionConfig config)
    {
        var options = Require().ConnectOptions;

        foreach (var header in options.Headers)
        {
            webSocket.Options.SetRequestHeader(header.Key, header.Value);
        }

        foreach (var subProtocol in options.SubProtocols)
        {
            webSocket.Options.AddSubProtocol(subProtocol);
        }

        if (Provider == LiveTranscriptionProvider.HyperWhisperCloud)
        {
            ClientInfoHeaders.Apply(webSocket);
        }
    }

    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> GetStartMessages(StreamingSessionConfig config)
    {
        var frames = Require().StartFrames;
        var messages = new List<(byte[] Data, WebSocketMessageType Type)>(frames.Count);
        foreach (var frame in frames)
        {
            messages.Add((frame.Data, frame.Type));
        }
        return messages;
    }

    /// <summary>
    /// Wrap one PCM chunk, and tell the core how much audio went out.
    ///
    /// The byte count is the only thing the core ever learns about audio, and
    /// only OpenAI's commit gate reads it — the call is free for the other four,
    /// so it is unconditional.
    /// </summary>
    public (byte[] Data, WebSocketMessageType Type) EncodeAudioChunk(byte[] pcmData)
    {
        var frame = Require().EncodeAudio(pcmData);
        return (frame.Data, frame.Type);
    }

    public StreamingProviderEvent? ParseMessage(string text) =>
        Event(Require().Parse(Encoding.UTF8.GetBytes(text)));

    public IReadOnlyList<StreamingStopStep> GetStopSequence()
    {
        var steps = Require().StopSequence(_nowMs());
        var mapped = new List<StreamingStopStep>(steps.Count);
        foreach (var step in steps)
        {
            mapped.Add(new StreamingStopStep(
                Action(step.Action),
                step.Payload,
                step.MessageType,
                step.WaitAfter));
        }
        return mapped;
    }

    /// <summary>
    /// Runs before the chunk that triggered it is encoded and sent, which is the
    /// order this head has always used. It matters for one provider: OpenAI's
    /// periodic commit then covers everything appended up to the previous chunk
    /// and leaves this one for the next commit. The core cannot tell the two
    /// orders apart — it is told a byte count and a clock reading — and a commit
    /// always follows the appends it claims either way.
    /// </summary>
    public async Task OnAudioSendOpportunityAsync(
        Func<byte[], WebSocketMessageType, CancellationToken, Task> webSocketSendAsync,
        CancellationToken cancellationToken
    )
    {
        foreach (var frame in Require().AudioOpportunityFrames(_nowMs()))
        {
            await webSocketSendAsync(frame.Data, frame.Type, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _protocol?.Dispose();
            _protocol = null;
        }
    }

    /// <summary>
    /// The current protocol, built on first use.
    /// </summary>
    /// <remarks>
    /// Every caller below is on a path that only runs once a socket has been
    /// opened, so in the app the protocol always exists by then —
    /// <see cref="BuildWebSocketUri"/> built it and returning null from there is
    /// what stops the connect. The lazy build is for the smoke suite, which
    /// exercises the parsers and the commit gate with no socket at all.
    /// </remarks>
    private ILiveTranscriptionProtocol Require()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_protocol != null)
                return _protocol;

            _protocol = LiveTranscriptionProtocolFactory.Create(_liveConfig);
            return _protocol;
        }
    }

    /// <summary>
    /// This app's per-session settings onto the shared core's live config.
    /// <c>RemoveFillerWords</c> is deliberately not carried: it is applied to
    /// confirmed deltas after the fact and never reaches the wire.
    /// </summary>
    private static LiveTranscriptionConfig ToLiveConfig(
        LiveTranscriptionProvider provider,
        StreamingSessionConfig config
    ) => new(
        provider,
        ApiKey: config.ApiKey,
        LicenseKey: config.LicenseKey,
        DeviceId: config.DeviceId,
        Language: config.Language,
        Vocabulary: config.Vocabulary,
        Model: config.Model,
        FastFormatting: config.FastFormatting,
        // HyperWhisper Cloud only: the core derives the relay route
        // (/ws/streaming-{sttProvider}) and the auto-detect vocabulary gate
        // from it. Every other provider ignores it.
        CloudTier: config.CloudTier
    );

    private static StreamingStopAction Action(LiveStopAction action) => action switch
    {
        LiveStopAction.SendMessage => StreamingStopAction.SendMessage,
        LiveStopAction.Wait => StreamingStopAction.Wait,
        LiveStopAction.WaitForSessionComplete => StreamingStopAction.WaitForSessionComplete,
        _ => StreamingStopAction.Close
    };

    /// <summary>
    /// The shared event type onto this app's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StreamingProviderEvent"/> stays as it is: it is consumed
    /// across the whole streaming client and it already carries every field the
    /// core's superset does. The one field that does not survive the trip is
    /// HyperWhisper Cloud's <c>remaining_seconds</c>, which the core does not
    /// carry — see the warning arm below.
    /// </para>
    /// <para>
    /// Null means "nothing happened": the client's own contract for a frame it
    /// should not act on, which every deleted strategy also returned for an
    /// unrecognised message.
    /// </para>
    /// </remarks>
    private static StreamingProviderEvent? Event(LiveProtocolEvent value) => value.Kind switch
    {
        LiveProtocolEventKind.Started =>
            new StreamingProviderEvent.SessionStarted(value.SessionId),

        LiveProtocolEventKind.Partial =>
            new StreamingProviderEvent.PartialTranscript(value.Text ?? string.Empty),

        LiveProtocolEventKind.Final =>
            new StreamingProviderEvent.FinalTranscript(value.Text ?? string.Empty),

        // One arm on the shared side, two here. A completion that carries text
        // is xAI's `transcript.done`, which is both the last final and the end
        // of the session; splitting it would drop the trailing words. A
        // completion without text is HyperWhisper Cloud's `session_complete`,
        // which carries the credits.
        LiveProtocolEventKind.Complete when !string.IsNullOrWhiteSpace(value.Text) =>
            new StreamingProviderEvent.FinalTranscriptAndSessionComplete(
                value.Text!,
                value.DurationSeconds,
                value.CreditsUsed),

        LiveProtocolEventKind.Complete =>
            new StreamingProviderEvent.SessionComplete(value.DurationSeconds, value.CreditsUsed),

        LiveProtocolEventKind.Error =>
            new StreamingProviderEvent.Error(value.Text ?? string.Empty),

        // RemainingSeconds is left null. The core drops HyperWhisper Cloud's
        // `remaining_seconds`, so the client's "(N seconds remaining)" suffix no
        // longer appears - the wording the backend puts in `message` already
        // says it, and the backend does not send the field on any frame today.
        LiveProtocolEventKind.Warning =>
            new StreamingProviderEvent.Warning(value.Text ?? string.Empty),

        LiveProtocolEventKind.Metadata =>
            new StreamingProviderEvent.Metadata(value.Text ?? string.Empty),

        _ => null
    };

    /// <summary>
    /// This app's provider enum onto the shared core's. The two differ in one
    /// name only: Windows spells the vendor <c>Xai</c>, the core and the batch
    /// contract spell it <c>Grok</c>.
    /// </summary>
    internal static LiveTranscriptionProvider CoreProvider(StreamingTranscriptionProvider provider) => provider switch
    {
        StreamingTranscriptionProvider.Deepgram => LiveTranscriptionProvider.Deepgram,
        StreamingTranscriptionProvider.ElevenLabs => LiveTranscriptionProvider.ElevenLabs,
        StreamingTranscriptionProvider.OpenAI => LiveTranscriptionProvider.OpenAi,
        StreamingTranscriptionProvider.Xai => LiveTranscriptionProvider.Grok,
        StreamingTranscriptionProvider.GeminiTranscribe => LiveTranscriptionProvider.GeminiTranscribe,
        _ => LiveTranscriptionProvider.HyperWhisperCloud
    };
}
