using System.Net.WebSockets;
using System.Text;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// What one parsed provider message means.
///
/// This is the shared core's <c>HwLiveEvent</c> superset (issue #281), not the
/// six-case enum this file used to declare. <see cref="Warning"/> and
/// <see cref="Metadata"/> exist on the wire for the providers that send them,
/// and the Windows head's <c>StreamingProviderEvent</c> already has both — so
/// carrying them here is what lets phase 4 re-point Windows at this layer
/// instead of refactoring it a second time.
/// </summary>
internal enum LiveProtocolEventKind
{
    Started,
    Partial,
    Final,

    /// <summary>
    /// The provider finished. Carries <see cref="LiveProtocolEvent.Text"/> when
    /// the completion frame also carried the last words — xAI's
    /// <c>transcript.done</c> is both, and splitting it would drop the tail.
    /// </summary>
    Complete,
    Error,

    /// <summary>A non-fatal notice. Only HyperWhisper Cloud sends these.</summary>
    Warning,

    /// <summary>A frame worth logging and nothing else.</summary>
    Metadata,
    Ignore,
}

/// <param name="SessionId">
/// The provider's own identifier for the session, when it names one. Deepgram's
/// <c>request_id</c> is what its support asks for; HyperWhisper Cloud and OpenAI
/// each send their own. <c>null</c> for the two that do not (ElevenLabs, xAI).
/// </param>
/// <param name="DurationSeconds">
/// Billable audio seconds, reported once on completion. <c>0</c> everywhere else.
/// </param>
/// <param name="CreditsUsed">
/// BILLING DATA. HyperWhisper Cloud reports it on <c>session_complete</c>, after
/// the stop frame — which is why the stop path waits on that event rather than
/// closing on a timer. Providers that bill through their own account report
/// <c>0</c>.
/// </param>
internal sealed record LiveProtocolEvent(
    LiveProtocolEventKind Kind,
    string? Text = null,
    LiveTranscriptionFailureCode ErrorCode = LiveTranscriptionFailureCode.Unknown,
    string? SessionId = null,
    double DurationSeconds = 0,
    double CreditsUsed = 0);

internal sealed record LiveProtocolFrame(byte[] Data, WebSocketMessageType Type);

/// <summary>
/// One step of the stop path, run in order. Mirrors the core's
/// <c>HwLiveStopStep</c> and the Windows head's shipped
/// <c>StreamingStopAction</c> arm for arm, so phase 4's mapping is a rename.
/// </summary>
internal enum LiveStopAction
{
    /// <summary>Send <see cref="LiveStopStep.Payload"/> as a frame.</summary>
    SendMessage,

    /// <summary>Sleep for <see cref="LiveStopStep.WaitAfter"/>, unconditionally.</summary>
    Wait,

    /// <summary>
    /// Wait until the provider's session-complete event arrives or
    /// <see cref="LiveStopStep.WaitAfter"/> elapses, whichever comes first.
    /// Returns immediately if it already arrived.
    /// </summary>
    WaitForSessionComplete,

    /// <summary>Close the socket. Always the last step.</summary>
    Close,
}

/// <summary>
/// Replaces the old <c>StopFrames()</c> + <c>DrainTimeout</c> pair, which could
/// not express what these protocols actually do (issue #281): Deepgram needs
/// <c>Finalize</c> → wait 500 ms → <c>CloseStream</c>, and two providers wait on
/// an <em>event</em> rather than a duration. Both members are gone rather than
/// kept alongside this one — a frame is a <see cref="LiveStopAction.SendMessage"/>
/// and a drain is a trailing wait, so keeping them would be two sources of truth
/// for one behaviour.
///
/// Field-for-field the Windows head's <c>StreamingStopStep</c>.
/// </summary>
internal sealed record LiveStopStep(
    LiveStopAction Action,
    byte[]? Payload = null,
    WebSocketMessageType MessageType = WebSocketMessageType.Text,
    TimeSpan? WaitAfter = null);

internal interface ILiveTranscriptionProtocol : IDisposable
{
    LiveTranscriptionProvider Provider { get; }
    int SampleRate { get; }
    StreamingWebSocketConnectOptions ConnectOptions { get; }
    IReadOnlyList<LiveProtocolFrame> StartFrames { get; }

    /// <summary>
    /// Whether the session is live the moment the handshake completes, or only
    /// once the provider sends its own session-started message.
    ///
    /// Only Deepgram is <c>true</c>: its one session-shaped frame
    /// (<c>Metadata</c>) does not arrive until after audio has been sent, so a
    /// client that waited for it before sending would deadlock the first chunk —
    /// which is exactly what issue #100 was.
    ///
    /// This head never reads it: <see cref="LiveCloudTranscriptionService"/>
    /// sends audio as soon as the socket opens for every provider. The Windows
    /// client gates its <c>Connecting → Streaming</c> transition on it, and it is
    /// surfaced from the core's <c>HwLiveConnect</c> here rather than re-derived
    /// from the provider there, because a second "is this Deepgram" list is the
    /// duplication issue #281 exists to delete.
    /// </summary>
    bool SessionStartsOnOpen { get; }

    LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm);

    /// <param name="nowMs">
    /// A monotonic reading from the caller's clock. The protocol reads no clock
    /// of its own — that is what makes OpenAI's 1.2 s commit interval and
    /// Deepgram's 3 s keepalive testable without sleeping.
    /// </param>
    IReadOnlyList<LiveProtocolFrame> AudioOpportunityFrames(long nowMs);

    /// <summary>
    /// The ordered stop path. Run the steps in order; do not reorder them and do
    /// not collapse the waits.
    /// </summary>
    IReadOnlyList<LiveStopStep> StopSequence(long nowMs);

    LiveProtocolEvent Parse(ReadOnlyMemory<byte> message);
}

internal static class LiveTranscriptionProtocolFactory
{
    public static ILiveTranscriptionProtocol Create(LiveTranscriptionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new RustLiveProtocol(config);
    }
}

/// <summary>
/// The five live-streaming wire protocols, from the shared core (issue #281).
///
/// This replaces five hand-written protocol classes. Everything that decides
/// what goes on the wire — the query strings, the start frames, the framing, the
/// parsers, the keepalive, OpenAI's commit gate and every stop sequence — now
/// lives in <c>hw_net::live</c> and is shared with the Windows and macOS heads.
/// What stays here is transport-shaped and only that: the URI, the header
/// dictionary and the base64 of a PCM chunk.
///
/// The role is <see cref="LlmPostProcessing"/>'s: a thin adapter that translates
/// the core's value types into this assembly's, and nothing else.
///
/// AUDIO NEVER CROSSES THE FFI. The core hands back an <c>HwAudioFraming</c>
/// descriptor once, at connect time, and <see cref="EncodeAudio"/> does the
/// base64 and the concatenation here, on bytes this process already holds. The
/// core is told a byte COUNT and never the samples.
///
/// Disposable because the generated <c>HwLiveSession</c> is: it is a handle onto
/// a Rust <c>Arc</c>, and dropping the last managed reference without disposing
/// leaks it until the finalizer runs.
/// </summary>
internal sealed class RustLiveProtocol : ILiveTranscriptionProtocol
{
    private readonly HwLiveSession _session;
    private readonly string? _framePrefix;
    private readonly string? _frameSuffix;
    private bool _disposed;

    public RustLiveProtocol(LiveTranscriptionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Provider = config.Provider;
        _session = new HwLiveSession(new HwLiveConfig(
            SharedCoreBridge.CoreLiveProvider(config.Provider),
            config.ApiKey,
            config.LicenseKey,
            config.DeviceId,
            config.Language,
            // A term LIST, not the comma-joined string this head used to build:
            // joining is a per-provider wire decision (xAI repeats `keyterm=`,
            // HyperWhisper Cloud sends one `vocabulary=`) and now belongs to the
            // protocol. Sanitizing, de-duplication and every cap moved with it.
            [.. config.Vocabulary ?? []],
            config.Model,
            config.FastFormatting,
            // No base URL: this head has no custom-backend setting, so the core
            // uses the production relay. macOS's `#if DEBUG` staging host is what
            // the field exists for.
            null));

        HwLiveConnect connect;
        try
        {
            connect = _session.Connect();
        }
        catch (HwLiveException.MissingCredential)
        {
            // Disposal here and not in a `finally`: the happy path keeps the
            // session for the whole recording.
            _session.Dispose();
            throw LiveProtocolException.Unauthorized(config.Provider);
        }
        catch
        {
            _session.Dispose();
            throw;
        }

        SampleRate = (int)connect.sampleRate;
        SessionStartsOnOpen = connect.sessionStartsOnOpen;
        StartFrames = [.. connect.startFrames.Select(Frame)];
        ConnectOptions = new StreamingWebSocketConnectOptions(
            new Uri(connect.url),
            // The core's header list is ordered and carries only what the wire
            // protocol needs. Client-identity headers stay the platform's, as
            // they always were.
            connect.headers.ToDictionary(header => header.name, header => header.value, StringComparer.Ordinal),
            [.. connect.subprotocols]);

        if (connect.framing is HwAudioFraming.Base64Json json)
        {
            _framePrefix = json.@prefix;
            _frameSuffix = json.@suffix;
        }
    }

    public LiveTranscriptionProvider Provider { get; }
    public int SampleRate { get; }
    public bool SessionStartsOnOpen { get; }
    public StreamingWebSocketConnectOptions ConnectOptions { get; }
    public IReadOnlyList<LiveProtocolFrame> StartFrames { get; }

    /// <summary>
    /// Wrap one PCM chunk, and tell the core how much audio went out.
    ///
    /// The byte count is the only thing the core learns about audio, and only
    /// OpenAI's commit gate reads it — the call is free for the other four, so
    /// it is unconditional.
    ///
    /// The JSON envelope is a string concatenation rather than a serializer
    /// call, which is a deliberate wire change: <c>System.Text.Json</c>'s default
    /// encoder escapes <c>+</c> to <c>+</c>, and <c>+</c> is in the base64
    /// alphabet, so most frames used to carry a handful of six-character escapes.
    /// Both spellings are the same JSON string value and no provider can tell
    /// them apart.
    /// </summary>
    public LiveProtocolFrame EncodeAudio(ReadOnlySpan<byte> pcm)
    {
        _session.NoteAudio((ulong)pcm.Length);
        if (_framePrefix is null || _frameSuffix is null)
        {
            return new LiveProtocolFrame(pcm.ToArray(), WebSocketMessageType.Binary);
        }
        return new LiveProtocolFrame(
            Encoding.UTF8.GetBytes(string.Concat(_framePrefix, Convert.ToBase64String(pcm), _frameSuffix)),
            WebSocketMessageType.Text);
    }

    public IReadOnlyList<LiveProtocolFrame> AudioOpportunityFrames(long nowMs) =>
        [.. _session.ControlFrames(Clock(nowMs)).Select(Frame)];

    public IReadOnlyList<LiveStopStep> StopSequence(long nowMs) =>
        [.. _session.StopSequence(Clock(nowMs)).Select(Step)];

    /// <summary>
    /// Read one text message.
    ///
    /// A message that is not JSON — or is JSON the provider has only just started
    /// sending — is <see cref="LiveProtocolEventKind.Ignore"/>, not a failure.
    /// This head used to let <c>JsonDocument.Parse</c> throw and ended the
    /// session on a <c>Protocol</c> error; macOS and Windows have always
    /// swallowed it, and a provider adding a frame shape must not end a recording
    /// in progress.
    /// </summary>
    public LiveProtocolEvent Parse(ReadOnlyMemory<byte> message) =>
        Event(_session.Parse(Encoding.UTF8.GetString(message.Span)));

    /// <summary>
    /// Idempotent, and safe to call while the receive loop is still in flight.
    /// The generated binding holds a call counter, so a call already inside the
    /// FFI keeps the Rust <c>Arc</c> alive and one that starts afterwards throws
    /// <see cref="ObjectDisposedException"/> rather than dereferencing a freed
    /// pointer. That matters on the failure paths, where the socket is torn down
    /// and the receive loop unwinds concurrently with this call.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.Dispose();
    }

    /// <summary>
    /// The core takes an unsigned millisecond reading. A caller handing over a
    /// negative one is a bug in the caller's clock, not something the protocol
    /// can act on, so it reads as zero rather than wrapping to 18 exaseconds and
    /// silently disabling every interval.
    /// </summary>
    private static ulong Clock(long nowMs) => nowMs < 0 ? 0UL : (ulong)nowMs;

    private static LiveProtocolFrame Frame(HwLiveFrame frame) =>
        new(
            Encoding.UTF8.GetBytes(frame.data),
            frame.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text);

    private static LiveStopStep Step(HwLiveStopStep step) => step switch
    {
        HwLiveStopStep.SendText send =>
            new LiveStopStep(LiveStopAction.SendMessage, Encoding.UTF8.GetBytes(send.@text)),
        HwLiveStopStep.Wait wait =>
            new LiveStopStep(LiveStopAction.Wait, WaitAfter: TimeSpan.FromMilliseconds(wait.@ms)),
        HwLiveStopStep.WaitForSessionComplete complete =>
            new LiveStopStep(
                LiveStopAction.WaitForSessionComplete,
                WaitAfter: TimeSpan.FromMilliseconds(complete.@timeoutMs)),
        _ => new LiveStopStep(LiveStopAction.Close),
    };

    /// <summary>
    /// The core's event superset onto this assembly's.
    ///
    /// A provider error frame carries wording, and whether a reconnect could ever
    /// help is answered by <see cref="SharedCoreBridge.ClassifyLiveErrorMessage"/>
    /// reading it — see <c>LiveCloudTranscriptionService.HandleEvent</c>. That
    /// covers four of the five providers and it is what issue #281 adds here.
    ///
    /// ElevenLabs is the fifth, and it is the exception the
    /// <c>HwLiveEvent.Error.kind</c> field exists for. Its error frames carry a
    /// machine-readable kind and NO wording, so the three sentences the core
    /// answers with are the core's own — classifying them is the core grading
    /// its own prose, and it grades one of the three the way macOS needs and
    /// this head does not: "ElevenLabs rate limit reached. Please try again in a
    /// moment." matches none of the twenty terminal markers, so it reads as
    /// transient. This head shipped <c>Unauthorized</c> / <c>QuotaExceeded</c> /
    /// <c>RateLimited</c> for those three kinds and refused a reconnect for all
    /// of them, and none of the three codes is in
    /// <c>LiveStreamingSessionController.CanReconnect</c>'s allowed set. So the
    /// kind is carried across the FFI and mapped back, and a key at its
    /// concurrent-session limit does not earn two more connects into the same
    /// limit at 250 ms and 500 ms.
    ///
    /// An earlier revision of this comment claimed "nothing outside this
    /// assembly ever read the three codes". That was false:
    /// <c>LiveStreamingSessionController</c> is in <c>HyperWhisper.LiveStreaming</c>
    /// and reads <c>Failure.Code</c>.
    ///
    /// The Windows head reads the message and ignores the kind — see
    /// <c>StreamingTranscriptionClient</c>, which deliberately declines to
    /// classify because its receive loop already ends the session on every
    /// provider error frame. Carrying the kind leaves that decision where it is.
    /// </summary>
    private static LiveProtocolEvent Event(HwLiveEvent value) => value switch
    {
        HwLiveEvent.SessionStarted started =>
            new LiveProtocolEvent(LiveProtocolEventKind.Started, SessionId: started.@sessionId),
        HwLiveEvent.PartialTranscript partial =>
            new LiveProtocolEvent(LiveProtocolEventKind.Partial, partial.@text),
        HwLiveEvent.FinalTranscript final =>
            new LiveProtocolEvent(LiveProtocolEventKind.Final, final.@text),
        HwLiveEvent.FinalTranscriptAndSessionComplete both =>
            new LiveProtocolEvent(
                LiveProtocolEventKind.Complete,
                both.@text,
                DurationSeconds: both.@durationSeconds,
                CreditsUsed: both.@creditsUsed),
        HwLiveEvent.SessionComplete complete =>
            new LiveProtocolEvent(
                LiveProtocolEventKind.Complete,
                DurationSeconds: complete.@durationSeconds,
                CreditsUsed: complete.@creditsUsed),
        HwLiveEvent.Error error =>
            new LiveProtocolEvent(
                LiveProtocolEventKind.Error,
                error.@message,
                error.@kind switch
                {
                    HwLiveErrorKind.Unauthorized => LiveTranscriptionFailureCode.Unauthorized,
                    HwLiveErrorKind.QuotaExceeded => LiveTranscriptionFailureCode.QuotaExceeded,
                    HwLiveErrorKind.RateLimited => LiveTranscriptionFailureCode.RateLimited,
                    // The four providers that send wording. The reconnect
                    // decision for these is IsTerminal, from the message.
                    _ => LiveTranscriptionFailureCode.ProviderUnavailable,
                }),
        HwLiveEvent.Warning warning =>
            new LiveProtocolEvent(LiveProtocolEventKind.Warning, warning.@message),
        HwLiveEvent.Metadata metadata =>
            new LiveProtocolEvent(LiveProtocolEventKind.Metadata, metadata.@raw),
        _ => new LiveProtocolEvent(LiveProtocolEventKind.Ignore),
    };
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
