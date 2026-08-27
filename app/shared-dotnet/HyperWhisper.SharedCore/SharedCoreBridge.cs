using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public sealed record BackupValidationFailure(string Path, string Message);

public sealed record PortablePromptContext(
    string Preset,
    string CustomInstructions,
    string EnglishSpelling,
    string Language,
    string UserSystemPrompt,
    IReadOnlyList<string> VocabularyWords,
    bool Punctuation,
    bool Capitalization,
    bool ProfanityFilter,
    string Time,
    string Timezone,
    string Locale,
    string ComputerName,
    string AppType = "other",
    string AppName = "",
    string Category = "",
    string Description = "",
    string TextFormat = "",
    string BrowserHost = "",
    string BrowserTabTitle = "",
    string FocusedElement = "",
    string FocusedContent = "",
    string ScreenOcrText = "",
    string AppTypeConfidence = "unknown",
    string AppTypeSource = "default",
    bool HasApplicationContext = false);

public sealed record PortablePostProcessingPrompt(string SystemPrompt, string SystemInfo);

public enum PortableLlmWireProtocol
{
    OpenAiChat,
    AnthropicMessages,
}

public sealed record PortableCompletionEvaluation(
    string Text,
    bool Accepted,
    string? Failure);

public enum PortableCursorContext
{
    Unknown,
    StartOfSentence,
    MidSentence,
}

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

/// <summary>
/// Stable public surface over the generated UniFFI binding. Platform projects
/// consume this assembly instead of compiling private copies of the binding.
/// </summary>
public static class SharedCoreBridge
{
    public static bool ContainsCjk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ContainsCjk(text);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is primarily written in a continuous
    /// script — one with no spaces between words. <see cref="ContainsCjk"/> plus
    /// Thai.
    /// <para>
    /// This is the text-side half of the segment-join policy (issue #286), and
    /// the only detector <see cref="SegmentSeparator"/> uses. It is deliberately
    /// not <see cref="ContainsCjk"/>: Thai is in the no-space LANGUAGE table but
    /// is not CJK, so deciding an <c>"auto"</c> join from CJK alone joined Thai
    /// with spaces while <c>SegmentSeparator("th", …)</c> joined it without —
    /// the same defect #286 fixed for Japanese, left standing for Thai.
    /// <c>ContainsCjk</c> keeps its own meaning for its own callers.
    /// </para>
    /// </summary>
    public static bool IsContinuousScript(string? text) =>
        HyperwhisperCoreMethods.IsContinuousScript(text ?? string.Empty);

    public static string NormalizeAppType(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var appType = HyperwhisperCoreMethods.AppTypeFromRaw(value);
        return HyperwhisperCoreMethods.AppTypePromptValue(appType);
    }

    public static string AppendTrailingSpace(string text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        return HyperwhisperCoreMethods.AppendTrailingSpace(text, language);
    }

    /// <summary>
    /// Whether <paramref name="language"/> is written without spaces between
    /// words, so consecutive transcription segments join with <c>""</c> rather
    /// than <c>" "</c>.
    /// <para>
    /// The single source of truth for the CJK join policy (issue #286). Callers
    /// that used to keep a private <c>ja|zh|ko|yue</c> table — the parakeet
    /// daemon, the Linux live-delivery path — read it from here, which also
    /// gains them <c>th</c> and the explicit <c>zh-TW</c> / <c>zh-Hans</c> /
    /// <c>zh-Hant</c> spellings.
    /// </para>
    /// <para>
    /// Case-insensitive, whitespace-tolerant, and a regional variant falls back
    /// to its two-character prefix (<c>"zh-CN"</c> → <c>"zh"</c>). A null,
    /// empty or <c>"auto"</c> language is <b>not</b> no-space: with no declared
    /// language there is nothing to decide on, and text-based detection is
    /// <see cref="ContainsCjk"/>'s job.
    /// </para>
    /// </summary>
    public static bool IsNoSpaceLanguage(string? language) =>
        HyperwhisperCoreMethods.IsNoSpaceLanguage(language ?? string.Empty);

    /// <summary>
    /// Whether <paramref name="language"/> declares nothing to decide a join
    /// policy from — null, blank, or the literal <c>"auto"</c>. Mirrors STEP 3 of
    /// the Rust <c>append_trailing_space</c>, which treats an empty code and
    /// <c>"auto"</c> identically.
    /// </summary>
    public static bool IsAutomaticLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language)
        || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to put at the boundary between <paramref name="precedingText"/> — the
    /// text already accumulated — and <paramref name="nextSegment"/>: <c>""</c>
    /// for a continuous script, <c>" "</c> otherwise. The one join policy for
    /// every caller that concatenates segments (issue #286) — resolve it once per
    /// boundary and build every sink's string from that single value.
    /// <para>
    /// <see cref="IsNoSpaceLanguage"/> alone is not enough, because the language
    /// the hosts actually pass is usually <c>"auto"</c>: it is the default of the
    /// Linux <c>StreamingLanguage</c> setting, what <c>ModelLibraryViewModel</c>
    /// resets that setting to whenever the picked model cannot do the chosen
    /// language, and what <c>ParakeetDaemonLiveTranscriber.NormalizeLanguage</c>
    /// sends the daemon for a mode with no language. <c>"auto"</c> is not a
    /// no-space language by design, so deciding from the language alone would
    /// join a Japanese dictation with spaces and #286's fix would never fire in
    /// the configuration almost everyone runs.
    /// </para>
    /// <para>
    /// The <c>"auto"</c> fallback is the one <c>append_trailing_space</c> already
    /// uses — detect the script in the text — but applied to BOTH sides of the
    /// boundary, and it is <see cref="IsContinuousScript"/> rather than
    /// <see cref="ContainsCjk"/> so Thai decides the same way its language code
    /// does. Testing only the segment AFTER the boundary is wrong in four ways
    /// that all show up in real streams, because a single segment often carries
    /// no script evidence at all:
    /// <list type="bullet">
    /// <item>an empty or whitespace-only segment — several streaming providers
    /// emit them as finals — scores as "not continuous" and wedges a space into
    /// the middle of a Japanese dictation;</item>
    /// <item>so does a punctuation-only segment (<c>"。"</c>), and so does a
    /// digit-heavy one (<c>"2024年"</c> is 1 CJK character in 5);</item>
    /// <item>it is asymmetric — <c>["これは", "OK", "です"]</c> would put a space
    /// on the left of the Latin run and none on the right;</item>
    /// <item>and it makes the result depend on where the boundaries fell, so the
    /// daemon and the host could still disagree whenever their VAD split the same
    /// audio differently — the very divergence #286 exists to close.</item>
    /// </list>
    /// The accumulated text is the primary signal (it is what
    /// <c>append_trailing_space</c> tests, and it carries the whole session's
    /// evidence); the next segment is the secondary one, so the first boundary of
    /// a stream still resolves correctly.
    /// </para>
    /// </summary>
    public static string SegmentSeparator(string? language, string? precedingText, string? nextSegment)
    {
        if (!IsAutomaticLanguage(language))
        {
            return IsNoSpaceLanguage(language) ? string.Empty : " ";
        }

        // Either side being continuous-script joins without a space. Short-circuits
        // on the preceding text: each call crosses the FFI boundary.
        return IsContinuousScript(precedingText) || IsContinuousScript(nextSegment)
            ? string.Empty
            : " ";
    }

    /// <summary>
    /// Concatenate <paramref name="segments"/> under the shared join policy — the
    /// production loop behind every batch join (the parakeet daemon's VAD
    /// segments), resolving <see cref="SegmentSeparator"/> once per boundary
    /// against the text accumulated so far.
    /// <para>
    /// Segments that are null, empty or whitespace-only are skipped rather than
    /// separated: they carry no words, and emitting a separator for one would put
    /// a bare space in the middle of the result.
    /// </para>
    /// </summary>
    public static string JoinSegments(string? language, IEnumerable<string?> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var joined = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append(SegmentSeparator(language, joined.ToString(), segment));
            }

            joined.Append(segment);
        }

        return joined.ToString();
    }

    public static string ApplyAutocapitalize(string text, PortableCursorContext context)
    {
        ArgumentNullException.ThrowIfNull(text);
        var native = context switch
        {
            PortableCursorContext.Unknown => CursorContext.Unknown,
            PortableCursorContext.StartOfSentence => CursorContext.StartOfSentence,
            PortableCursorContext.MidSentence => CursorContext.MidSentence,
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null),
        };
        return HyperwhisperCoreMethods.ApplyAutocapitalize(text, native);
    }

    public static IReadOnlyList<BackupValidationFailure> ValidateBackup(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.ValidateBackupJson(json)
            .Select(error => new BackupValidationFailure(error.path, error.message))
            .ToArray();
    }

    public static PortablePostProcessingPrompt BuildPostProcessingPrompt(PortablePromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var native = new PromptContext(
            PresetFromRaw(context.Preset),
            context.CustomInstructions ?? string.Empty,
            EnglishSpellingFromRaw(context.EnglishSpelling),
            context.Language ?? string.Empty,
            context.UserSystemPrompt ?? string.Empty,
            AppTypeFromRaw(context.AppType),
            Bound(context.AppName, 256),
            Bound(context.Category, 128),
            Bound(context.Description, 500),
            Bound(context.TextFormat, 64),
            Bound(context.BrowserHost, 253),
            Bound(context.BrowserTabTitle, 500),
            Bound(context.FocusedElement, 128),
            BoundWithEllipsis(context.FocusedContent, 100),
            Bound(context.ScreenOcrText, 2000),
            Bound(context.AppTypeConfidence, 32, "unknown"),
            Bound(context.AppTypeSource, 64, "default"),
            context.HasApplicationContext,
            context.VocabularyWords?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? [],
            context.Time ?? string.Empty,
            context.Timezone ?? string.Empty,
            context.Locale ?? string.Empty,
            context.ComputerName ?? string.Empty,
            context.Punctuation,
            context.Capitalization,
            context.ProfanityFilter);
        return new PortablePostProcessingPrompt(
            HyperwhisperCoreMethods.BuildSystemPrompt(native),
            HyperwhisperCoreMethods.BuildSystemInfo(native));
    }

    public static PortableCompletionEvaluation EvaluateLlmResponseJson(
        PortableLlmWireProtocol wireProtocol,
        string responseJson,
        string original)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        ArgumentNullException.ThrowIfNull(original);
        var native = HyperwhisperCoreMethods.EvaluateLlmResponseJson(
            wireProtocol switch
            {
                PortableLlmWireProtocol.OpenAiChat => WireProtocol.OpenAiChat,
                PortableLlmWireProtocol.AnthropicMessages => WireProtocol.AnthropicMessages,
                _ => throw new ArgumentOutOfRangeException(nameof(wireProtocol), wireProtocol, null),
            },
            responseJson,
            original);
        return new PortableCompletionEvaluation(
            native.text,
            native.accepted,
            native.failure.ToString());
    }

    public static string RemoveTrailingPeriod(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveTrailingPeriod(text);
    }

    public static string RemoveFillerWords(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveFillerWords(text, language);
    }

    /// <summary>
    /// The canonical vocabulary-egress normalization: sanitize each term (strip
    /// <c>&lt;</c>/<c>&gt;</c>, collapse whitespace runs, cap at the core's
    /// 80-character term limit), drop the ones that sanitize to nothing,
    /// de-duplicate case-insensitively keeping first-seen order and casing, and
    /// optionally stop after <paramref name="limit"/> terms.
    ///
    /// <paramref name="limit"/> is <c>null</c> for "no cap"; <c>0</c> means zero
    /// terms, exactly like <c>.Take(0)</c>. Each call site owns its own cap and
    /// its own join separator — only this rule is shared.
    /// </summary>
    public static IReadOnlyList<string> NormalizeVocabularyTerms(
        IReadOnlyList<string>? words,
        uint? limit)
        => HyperwhisperCoreMethods.NormalizeVocabularyTerms(
            // Guarding empties here keeps a null/blank row out of the FFI call;
            // the core drops them anyway once they sanitize to nothing.
            words?.Where(word => !string.IsNullOrEmpty(word)).ToList() ?? [],
            limit);

    public static string ProcessVoiceCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ProcessVoiceCommands(text);
    }

    public static string ApplyHardenedReplacement(string text, string word, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(replacement);
        return HyperwhisperCoreMethods.ApplyHardenedReplacement(text, word, replacement);
    }

    /// <summary>
    /// The <c>Mode.EnglishSpelling</c> token to SEED into a brand-new mode, from
    /// an ISO 3166-1 alpha-2 region code. Trimming, case folding and the region
    /// table are the core's; an unknown, empty or null code gives "american".
    ///
    /// This is a seeding call and is NOT the inverse of the prompt path's
    /// spelling parse: an empty stored value there means "the user never chose"
    /// and suppresses the spelling instruction entirely, which is never a thing
    /// to seed. The core never answers <c>HwEnglishSpelling.None</c> here, so
    /// this needs no "american" fallback of its own.
    /// </summary>
    public static string EnglishSpellingForRegion(string? regionCode) =>
        HyperwhisperCoreMethods.EnglishSpellingRawValue(
            HyperwhisperCoreMethods.EnglishSpellingForRegion(regionCode));

    public static string CanonicalCloudSttTier(string? value) =>
        HyperwhisperCoreMethods.MigrateCloudAccuracyTier(value);

    public static string? CloudSttProvider(string tierId) =>
        HyperwhisperCoreMethods.CloudSttProvider(tierId);

    /// <summary>
    /// Cloud-tier entry ids HyperWhisper Cloud can also serve LIVE, in catalog
    /// order — the eligible set for the streaming cloud-tier picker.
    ///
    /// Catalog-derived (<c>cloudTierEligible</c> AND some model with
    /// <c>streaming: true</c>), never a hand-kept list. Note this is NOT the
    /// entry-level <c>features.streaming</c> hint, which is true for six vendors
    /// we serve no WebSocket route for.
    /// </summary>
    public static IReadOnlyList<string> StreamingCloudSttTiers() =>
        HyperwhisperCoreMethods.CloudSttStreamingCloudTierEntryIds();

    public static string? CloudSttDefaultModel(string tierId) =>
        HyperwhisperCoreMethods.CloudSttDefaultModelId(tierId);

    public static bool CloudSttContainsModel(string tierId, string modelId) =>
        HyperwhisperCoreMethods.CloudSttModels(tierId)
            .Any(model => string.Equals(model.id, modelId, StringComparison.Ordinal));

    /// <summary>
    /// Model ids HyperWhisper Cloud serves ONLY over its live WebSocket route.
    /// A pre-recorded POST carrying one of these is an HTTP 400 from the
    /// upstream vendor, on every dictation, for as long as the mode keeps it.
    ///
    /// NOT derivable from the per-model <c>streaming</c> flag, despite how that
    /// reads. <c>streaming: true</c> means "HyperWhisper Cloud routes this model
    /// live", and <c>deepgramNova3</c> carries it on BOTH <c>nova-3-general</c>
    /// and <c>nova-3-medical</c> — the default pre-recorded models. Filtering on
    /// that flag would delete Deepgram's default dictation model.
    ///
    /// The catalog has no live-only field, so this is the shared-.NET mirror of
    /// the same literal the other heads keep:
    /// <c>CloudSttCatalog.LiveOnlyModelIds</c> (Windows),
    /// <c>CloudSTTCatalog.liveOnlyModelIds</c> (macOS). All three are pinned
    /// against <c>shared-conformance/live-only-models.json</c> so they cannot
    /// drift apart.
    /// </summary>
    public static IReadOnlySet<string> LiveOnlyCloudSttModelIds { get; } =
        new HashSet<string>(
            ["gemini-3.5-transcribe-live", "gpt-live-transcribe"],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="modelId"/> is one of
    /// <see cref="LiveOnlyCloudSttModelIds"/> (trimmed, case-insensitive).
    /// False for null/blank — "no model chosen" resolves to the tier default,
    /// which is never live-only.
    /// </summary>
    public static bool IsLiveOnlyCloudSttModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && LiveOnlyCloudSttModelIds.Contains(modelId.Trim());

    /// <summary>
    /// Tier membership for a PRE-RECORDED request: the model must be in the
    /// tier AND not live-only. Plain <see cref="CloudSttContainsModel"/> accepts
    /// a live-only id, because it genuinely IS a model of the tier — the Linux
    /// model box is a bare text field, and a backup restore or a Local API write
    /// can put one there on any platform. Callers that route a file or a
    /// dictation must use this one and fall back to the tier default.
    /// </summary>
    public static bool CloudSttContainsDictationModel(string tierId, string modelId) =>
        !IsLiveOnlyCloudSttModel(modelId) && CloudSttContainsModel(tierId, modelId);

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

    private static Preset PresetFromRaw(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "message" => Preset.Message,
        "mail" => Preset.Mail,
        "note" => Preset.Note,
        "meeting" => Preset.Meeting,
        "code" => Preset.Code,
        "custom" => Preset.Custom,
        _ => Preset.Hyper,
    };

    private static HwAppType AppTypeFromRaw(string? value) =>
        HyperwhisperCoreMethods.AppTypeFromRaw(value ?? string.Empty);

    private static string Bound(string? value, int maxCharacters, string fallback = "")
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return value.Length <= maxCharacters ? value : value[..maxCharacters];
    }

    private static string BoundWithEllipsis(string? value, int maxSourceCharacters)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxSourceCharacters
            ? value
            : string.Concat(value.AsSpan(0, maxSourceCharacters), "...");
    }

    private static HwEnglishSpelling EnglishSpellingFromRaw(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "american" => HwEnglishSpelling.American,
            "british" => HwEnglishSpelling.British,
            "australian" => HwEnglishSpelling.Australian,
            "canadian" => HwEnglishSpelling.Canadian,
            _ => HwEnglishSpelling.None,
        };
}
