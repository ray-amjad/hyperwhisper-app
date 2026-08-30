using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// What a live streaming provider's error frame means for the reconnect path.
/// Mirrors the core's <c>HwLiveErrorOutcome</c> (issue #281).
/// </summary>
public enum PortableLiveErrorOutcome
{
    /// <summary>
    /// Reconnecting cannot help — the account, key, quota or permission is the
    /// problem. Mark the provider's follow-up close as expected and surface the
    /// message as it stands.
    /// </summary>
    Terminal,

    /// <summary>The failure may clear on its own; keep the reconnect path.</summary>
    Transient,
}

/// <summary>
/// Why a server refused a WebSocket upgrade outright, when the refusal is one
/// the user has to act on. Mirrors the core's <c>HwLiveUpgradeRefusal</c>.
/// </summary>
public enum PortableLiveUpgradeRefusal
{
    /// <summary>HTTP 402 — no balance to open a session with.</summary>
    InsufficientCredits,

    /// <summary>HTTP 401 / 403 — the key is missing, wrong, revoked or not permitted.</summary>
    Unauthorized,
}

public static partial class SharedCoreBridge
{
    // -----------------------------------------------------------------------
    // Live streaming (issue #281)
    //
    // Seven session-free functions the core owns for all three heads. The
    // terminal-error policy behind the first two shipped on macOS only, and the
    // two halves of it do NOT reach the same heads:
    //
    //   * Mid-session, via ClassifyLiveErrorMessage. THIS head gains it, and it
    //     is the only one that does: LiveCloudTranscriptionService is the single
    //     non-test caller in the repo. A "Credit balance exhausted" frame from
    //     the default provider now stops driving a reconnect that can only fail
    //     the same way.
    //
    //     Windows deliberately does NOT call it. StreamingTranscriptionClient
    //     moves to Error on EVERY provider error frame and its receive loop ends
    //     the session there, terminal or not, so it has no doomed-reconnect
    //     fan-out to suppress — wiring the classifier in would LOOSEN
    //     termination, not tighten it, because a transient frame would start
    //     keeping its reconnect. That is a behaviour change on a shipped path
    //     and it belongs to the client rework, not to issue #281's
    //     single-sourcing. The reasoning is recorded at the client's
    //     `case StreamingProviderEvent.Error` arm.
    //
    //   * Pre-session, via LiveUpgradeRefusal below — the relay refusing the
    //     WebSocket upgrade outright. Windows gains it: its
    //     TerminalUpgradeMessage sets ClientWebSocket.Options
    //     .CollectHttpResponseDetails and reads HttpStatusCode off the socket.
    //
    //     NOT COVERED on Linux. LiveUpgradeRefusal has no Linux caller and
    //     cannot have one today: ClientStreamingWebSocket.ConnectAsync never
    //     sets CollectHttpResponseDetails, and IStreamingWebSocket carries no
    //     HTTP status at all, so the status the refusal arrives on is
    //     unreachable from LiveCloudTranscriptionService. This is the real case
    //     it costs: HyperWhisper Cloud requires 30 seconds of balance and
    //     refuses in middleware (hyperwhisper-cloud/src/middleware/credits.ts →
    //     insufficientCreditsResponse, src/lib/responses.ts:48) with a 402
    //     before any socket exists, so a Linux user out of credits gets a bare
    //     transport failure and the ordinary reconnect, where Windows and macOS
    //     get "add more credits in Settings" and stop.
    //
    //     Closing it needs two changes, neither of them here: set
    //     CollectHttpResponseDetails in ClientStreamingWebSocket.ConnectAsync,
    //     and widen IStreamingWebSocket to surface the failed upgrade's status
    //     (a fake socket has no ClientWebSocket to read it from, so it has to be
    //     on the interface).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Classifies the message payload of a streaming provider's error frame.
    /// Unrecognised wording — including an empty message — is
    /// <see cref="PortableLiveErrorOutcome.Transient"/>, so a payload nobody has
    /// seen yet keeps today's reconnect behaviour.
    /// </summary>
    public static PortableLiveErrorOutcome ClassifyLiveErrorMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return HyperwhisperCoreMethods.LiveClassifyErrorMessage(message) switch
        {
            HwLiveErrorOutcome.Terminal => PortableLiveErrorOutcome.Terminal,
            _ => PortableLiveErrorOutcome.Transient,
        };
    }

    /// <summary>
    /// Classifies the HTTP status of a WebSocket upgrade that never reached
    /// 101. <c>null</c> means the ordinary reconnect path still applies — 429,
    /// 5xx and a proxy mangling the upgrade all keep it.
    /// </summary>
    /// <param name="status">
    /// The status carried by the response that came back instead of a
    /// <c>101 Switching Protocols</c>. Takes an <see cref="int"/> because that
    /// is what the .NET WebSocket stacks hand over; a value outside
    /// <see cref="ushort"/> cannot be an HTTP status and takes the same "no
    /// refusal" answer every other unrecognised status gets.
    /// </param>
    public static PortableLiveUpgradeRefusal? LiveUpgradeRefusal(int status)
    {
        if (status is < ushort.MinValue or > ushort.MaxValue)
        {
            return null;
        }

        return HyperwhisperCoreMethods.LiveUpgradeRefusal((ushort)status) switch
        {
            HwLiveUpgradeRefusal.InsufficientCredits => PortableLiveUpgradeRefusal.InsufficientCredits,
            HwLiveUpgradeRefusal.Unauthorized => PortableLiveUpgradeRefusal.Unauthorized,
            _ => null,
        };
    }

    /// <summary>
    /// Whether a WebSocket close code is one of the RFC 6455 §7.4.1
    /// non-recoverable codes (1002, 1003, 1007, 1008, 1009, 1011). A provider
    /// that signals an unrecoverable session with a private close code combines
    /// it with this answer rather than replacing it.
    /// </summary>
    public static bool IsTerminalLiveCloseCode(int closeCode) =>
        closeCode is >= ushort.MinValue and <= ushort.MaxValue
        && HyperwhisperCoreMethods.LiveIsTerminalCloseCode((ushort)closeCode);

    /// <summary>
    /// Normalizes a language selection to the primary subtag a provider wants.
    /// <c>null</c> means "omit the language parameter entirely" and covers no
    /// selection, a blank string and the app's <c>"auto"</c> sentinel alike.
    /// </summary>
    public static string? NormalizeLiveLanguage(string? code) =>
        HyperwhisperCoreMethods.LiveNormalizeLanguage(code);

    /// <summary>
    /// The PCM sample rate, in hertz, the provider's socket expects. The
    /// capture graph is configured from this before a session opens.
    /// </summary>
    public static int LiveRequiredSampleRate(LiveTranscriptionProvider provider) =>
        (int)HyperwhisperCoreMethods.LiveRequiredSampleRate(CoreLiveProvider(provider));

    /// <summary>
    /// Whether the provider's live API takes a custom-vocabulary parameter at
    /// all. <c>false</c> means the terms are dropped before the socket opens.
    /// </summary>
    public static bool LiveSupportsVocabulary(LiveTranscriptionProvider provider) =>
        HyperwhisperCoreMethods.LiveSupportsVocabulary(CoreLiveProvider(provider));

    /// <summary>
    /// Whether the provider honours custom vocabulary while the language is left
    /// on auto-detect. A SECOND question from
    /// <see cref="LiveSupportsVocabulary"/>: Deepgram Nova-3 accepts
    /// <c>keyterm</c> only in monolingual mode and silently ignores it
    /// otherwise, so its settings surfaces warn — while Gemini and xAI accept
    /// theirs either way, and warning about those would be wrong.
    /// </summary>
    /// <param name="cloudTier">
    /// Read for <see cref="LiveTranscriptionProvider.HyperWhisperCloud"/> only,
    /// where the answer belongs to whichever vendor the relay will forward to.
    /// <c>null</c> means the default tier.
    /// </param>
    public static bool LiveSupportsVocabularyWithoutLanguage(
        LiveTranscriptionProvider provider,
        string? cloudTier = null) =>
        HyperwhisperCoreMethods.LiveSupportsVocabularyWithoutLanguage(
            CoreLiveProvider(provider), cloudTier);

    /// <summary>
    /// Whether a session-complete event ends the session even when the client
    /// has NOT asked to stop yet.
    ///
    /// <c>false</c> for Gemini alone: <c>generationComplete</c> is a TURN
    /// boundary, fired at each pause in speech, so a terminal reading silently
    /// ends a live dictation at the first one and the last utterance's final
    /// never arrives.
    /// </summary>
    public static bool LiveCompleteEndsSessionBeforeStop(LiveTranscriptionProvider provider) =>
        HyperwhisperCoreMethods.LiveCompleteEndsSessionBeforeStop(CoreLiveProvider(provider));

    /// <summary>
    /// How long to hold the audio pump waiting for the provider's
    /// session-started frame, in milliseconds. <c>0</c> means send from the
    /// moment the socket opens, which is every provider but Gemini — whose
    /// server discards audio that arrives before <c>setupComplete</c>.
    /// </summary>
    public static int LiveStartTimeoutMs(LiveTranscriptionProvider provider) =>
        (int)HyperwhisperCoreMethods.LiveStartTimeoutMs(CoreLiveProvider(provider));

    /// <summary>
    /// The human-readable provider label stored on a history entry. The
    /// " (Streaming)" suffix is what distinguishes a live session from the same
    /// vendor's batch transcription.
    /// </summary>
    public static string LiveProviderLabel(LiveTranscriptionProvider provider) =>
        HyperwhisperCoreMethods.LiveProviderLabel(CoreLiveProvider(provider));

    /// <summary>
    /// Maps the .NET live-provider enum onto the core's. The two local engines
    /// have no arm on purpose: Parakeet and Nemotron are not WebSocket
    /// protocols and share none of this, which is the same line
    /// <c>LiveTranscriptionProtocolFactory.Create</c> draws.
    ///
    /// Internal rather than private because <see cref="RustLiveProtocol"/> builds
    /// an <c>HwLiveConfig</c> from the same enum and a second copy of this switch
    /// is exactly the drift this issue exists to delete.
    /// </summary>
    internal static HwLiveProvider CoreLiveProvider(LiveTranscriptionProvider provider) => provider switch
    {
        LiveTranscriptionProvider.Deepgram => HwLiveProvider.Deepgram,
        LiveTranscriptionProvider.ElevenLabs => HwLiveProvider.ElevenLabs,
        LiveTranscriptionProvider.OpenAi => HwLiveProvider.OpenAi,
        LiveTranscriptionProvider.Grok => HwLiveProvider.Grok,
        LiveTranscriptionProvider.GeminiTranscribe => HwLiveProvider.GeminiTranscribe,
        LiveTranscriptionProvider.HyperWhisperCloud => HwLiveProvider.HyperWhisperCloud,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a WebSocket streaming provider"),
    };
}
