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

/// <summary>
/// One row of the user's vocabulary, as the two vocabulary passes below want it.
/// <paramref name="Replacement"/> is null (or empty) for a spelling-hint row —
/// only those are corrected towards by
/// <see cref="SharedCoreBridge.ApplyPhoneticVocabulary"/>. A row that carries a
/// replacement belongs to
/// <see cref="SharedCoreBridge.ApplySubstringVocabulary"/> instead.
///
/// Deliberately NOT <c>PortableVocabularyReplacement</c> (HyperWhisper.SpeechOutput):
/// that record requires a non-null replacement, and the whole point of the
/// phonetic pass is the rows that have none.
/// </summary>
public sealed record PortableVocabularyEntry(string Word, string? Replacement);

/// <summary>One phonetic correction, for the caller to log.</summary>
public sealed record PortablePhoneticMatch(string Token, string Replacement);

/// <summary>
/// The phonetic pass's answer for one transcription:
/// <paramref name="Text"/> is the corrected transcript,
/// <paramref name="Matches"/> is every correction in order, and
/// <paramref name="EntryCount"/> is how many vocabulary rows survived the core's
/// build filters and were actually matched against.
/// </summary>
public sealed record PortablePhoneticResult(
    string Text,
    IReadOnlyList<PortablePhoneticMatch> Matches,
    uint EntryCount);

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
/// The persisted status of a transcript row, as the home statistics see it.
/// Mirrors the core's <c>HwTranscriptStatus</c>.
/// </summary>
public enum PortableStatsTranscriptStatus
{
    Processing,
    Completed,
    Failed,
}

/// <summary>
/// One persisted transcript, projected down to what the home statistics need
/// (issue #285).
///
/// <para><c>CreatedAtLocalEpochSeconds</c> is the row's instant ALREADY SHIFTED
/// into the calendar time zone. The host owns that conversion because the host
/// owns the time-zone database, and doing it per row is what keeps DST correct.
/// Every calendar boundary above it — Monday, the 1st, January 1st — is
/// computed in the core.</para>
///
/// <para><c>WordCount</c> is counted by the host from the full text. There is
/// no persisted word count on any of the three stores, so word counting stays
/// native.</para>
/// </summary>
public sealed record PortableStatsTranscript(
    long CreatedAtLocalEpochSeconds,
    int WordCount,
    double DurationSeconds,
    PortableStatsTranscriptStatus Status);

/// <summary>One period's totals and derived figures. Mirrors <c>HwPeriodStats</c>.</summary>
public sealed record PortablePeriodStats(
    int WordCount,
    double DurationSeconds,
    int AverageWordsPerMinute,
    double EstimatedTypingMinutes,
    double EstimatedTimeSavedMinutes);

/// <summary>
/// Everything the three home strips render, plus the periods the statistics
/// pages use. Mirrors <c>HwHomeStatsSnapshot</c>.
/// </summary>
public sealed record PortableHomeStats(
    PortablePeriodStats ThisWeek,
    PortablePeriodStats ThisMonth,
    PortablePeriodStats ThisYear,
    PortablePeriodStats AllTime,
    int TypingSpeedWordsPerMinute,
    int AverageWordsPerMinute,
    int SavedThisWeekMinutes);

/// <summary>
/// One language the pickers can offer, as the shared catalog knows it (issue
/// #285). Mirrors the core's <c>HwLanguage</c>.
///
/// <para><c>Code</c> is always the canonical BCP-47 tag — <c>en-GB</c>, not
/// <c>en_gb</c> — so it is safe to persist and to compare against another
/// canonical code.</para>
///
/// <para>A null <c>DisplayName</c> means the catalog does not know the code,
/// and the host must localize it with its own system database. That split is
/// deliberate rather than a gap: the catalog carries English names for the
/// codes the app itself offers, and everything else — a code a provider
/// advertised that we have never listed — is exactly the case the platform
/// frameworks answer better than a table would. It is where
/// <c>Locale.localizedString(forIdentifier:)</c> lives on macOS, and
/// <c>CultureInfo</c> on the .NET heads. Fall back to <c>Code</c> when there is
/// no system name either; a raw tag reads better than an empty row.</para>
/// </summary>
public sealed record PortableLanguage(string Code, string? DisplayName);

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

    /// <summary>
    /// Canonicalize ONE universal-v2 mode object's five cloud-routing fields:
    /// the cloudProvider catalog fold, the legacy model-alias tables, the
    /// present-only cloudAccuracyTier / cloudPostProcessingModel migration
    /// (including the platformExtensions.windows override) and the
    /// cloudTranscriptionDomain gate. Windows' UniversalBackupMapper calls the
    /// same core function, so both non-macOS importers agree.
    /// </summary>
    /// <remarks>
    /// A field no source supplied comes back ABSENT, not defaulted — the caller
    /// applies its own Mode entity default. Stamping the core's own defaults here
    /// would regress both heads, whose shared native pair is elevenLabsScribeV2 /
    /// anthropic:claude-haiku-4-5.
    /// </remarks>
    public static string NormalizeUniversalMode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return HyperwhisperCoreMethods.NormalizeUniversalModeJson(json);
    }

    /// <summary>
    /// Map the Linux settings store (flat, dotted keys) into the universal-v2
    /// shared <c>settings</c> block.
    /// </summary>
    /// <remarks>
    /// The whole store may be passed in: only keys with a row in the core's
    /// <c>LINUX_*_PAIRS</c> tables are promoted, so Linux-only and device-local
    /// keys cannot reach an exported backup through here. The result is always
    /// COMPLETE — an absent key is emitted with the backup path's own default,
    /// which is what makes an untouched profile export all 23 shared keys.
    /// </remarks>
    public static string LinuxSettingsToUniversal(string linuxJson)
    {
        ArgumentNullException.ThrowIfNull(linuxJson);
        return HyperwhisperCoreMethods.LinuxSettingsToUniversalSettingsJson(linuxJson);
    }

    /// <summary>
    /// Inverse of <see cref="LinuxSettingsToUniversal"/>: the universal-v2 shared
    /// <c>settings</c> block into the flat dotted keys the Linux settings store
    /// holds.
    /// </summary>
    /// <remarks>
    /// PRESENT-ONLY and null-dropping, reproducing the shipping
    /// <c>ApplySharedSettings</c>/<c>CopyCategory</c> allowlist: unknown keys and
    /// unknown categories are dropped, and an explicit JSON <c>null</c> leaves the
    /// live value alone. The caller deep-merges this over its own baseline
    /// snapshot before writing it back.
    /// </remarks>
    public static string UniversalSettingsToLinuxSettings(string universalJson)
    {
        ArgumentNullException.ThrowIfNull(universalJson);
        return HyperwhisperCoreMethods.UniversalSettingsToLinuxSettingsJson(universalJson);
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
    /// Beider-Morse phonetic vocabulary matching over a WHOLE transcript, in one
    /// call. Whole-word (<c>\b</c>-anchored), case-insensitive, literal
    /// replacement, diacritic-SENSITIVE.
    ///
    /// Rows that carry a replacement, and words of 2 Unicode scalars or fewer,
    /// are skipped; a token that already equals ANY vocabulary word is left
    /// alone. The core returns every correction rather than logging it, so each
    /// head logs through its own logger (issue #283).
    ///
    /// One call, not one per word. The retired native matchers encoded a single
    /// word per call — ~340 round trips for a 40-entry vocabulary over a
    /// 300-word transcript — which is why Windows carried both a cached matcher
    /// object and a per-call token memo. Neither is needed now.
    /// </summary>
    public static PortablePhoneticResult ApplyPhoneticVocabulary(
        string text,
        IReadOnlyList<PortableVocabularyEntry>? entries)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = HyperwhisperCoreMethods.PhoneticApplyVocabulary(text, ToNativeEntries(entries));
        return new PortablePhoneticResult(
            result.text,
            result.matches
                .Select(match => new PortablePhoneticMatch(match.token, match.replacement))
                .ToList(),
            result.entryCount);
    }

    /// <summary>
    /// The on-device providers' vocabulary pass: unanchored substring
    /// replacement, case-insensitive AND diacritic-insensitive, over the rows
    /// that DO carry a replacement, in list order.
    ///
    /// Deliberately NOT <see cref="ApplyHardenedReplacement"/>. That one anchors
    /// on <c>\b…\b</c> and is diacritic-sensitive, and it runs later over the
    /// pipeline's own vocabulary list. This one runs first, inside the provider,
    /// over its raw output — the split macOS made explicit in
    /// <c>VocabularyProcessor.swift</c> after finding four identical copies of
    /// it. Windows and Linux never had it; issue #283 gives it to them.
    ///
    /// Text outside a match comes back byte-identical: its case, its accents and
    /// its normalization form are untouched.
    /// </summary>
    public static string ApplySubstringVocabulary(
        string text,
        IReadOnlyList<PortableVocabularyEntry>? entries)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ApplySubstringVocabulary(text, ToNativeEntries(entries));
    }

    private static List<HwVocabularyEntry> ToNativeEntries(
        IReadOnlyList<PortableVocabularyEntry>? entries)
        => entries?
            // A null row cannot cross the FFI, and the core would drop it anyway
            // once its word sanitized to nothing.
            .Where(entry => entry is not null)
            .Select(entry => new HwVocabularyEntry(entry.Word ?? string.Empty, entry.Replacement))
            .ToList() ?? [];

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

    /// <summary>
    /// The home statistics for a whole transcript history, in one call (issue
    /// #285). Weekly, monthly, yearly and all-time totals, the average speaking
    /// speed, and the clamped "saved this week" figure the home strip renders.
    ///
    /// <para>The core filters to <see cref="PortableStatsTranscriptStatus.Completed"/>
    /// itself, normalises every non-finite or negative duration to zero, starts
    /// the week on the local-time-zone Monday, rounds half away from zero, and
    /// clamps <c>SavedThisWeekMinutes</c> to
    /// <see cref="SavedThisWeekMinutesCeiling"/>. There is no error case: a
    /// typing speed of zero or less zeroes the saving figures rather than
    /// failing.</para>
    ///
    /// <para>One call per recompute, not one per row. Every head already
    /// materialises the whole row set to do this.</para>
    /// </summary>
    public static PortableHomeStats CalculateHomeStatistics(
        IReadOnlyList<PortableStatsTranscript>? transcripts,
        int typingSpeedWordsPerMinute,
        long nowLocalEpochSeconds)
    {
        var snapshot = HyperwhisperCoreMethods.StatsCalculateHome(
            ToNativeTranscripts(transcripts), typingSpeedWordsPerMinute, nowLocalEpochSeconds);
        return new PortableHomeStats(
            ToPortablePeriod(snapshot.thisWeek),
            ToPortablePeriod(snapshot.thisMonth),
            ToPortablePeriod(snapshot.thisYear),
            ToPortablePeriod(snapshot.allTime),
            snapshot.typingSpeedWordsPerMinute,
            snapshot.averageWordsPerMinute,
            snapshot.savedThisWeekMinutes);
    }

    /// <summary>
    /// The ceiling the displayed "saved this week" figure is clamped to: one
    /// week of minutes. Read it rather than restating <c>7 * 24 * 60</c>, which
    /// is exactly how the constant drifted onto two platforms and off the third.
    /// </summary>
    public static int SavedThisWeekMinutesCeiling => HyperwhisperCoreMethods.StatsSavedMinutesCeiling();

    private static List<HwStatsTranscript> ToNativeTranscripts(
        IReadOnlyList<PortableStatsTranscript>? transcripts)
        => transcripts?
            // A null row cannot cross the FFI, and a row the store never
            // completed would be filtered out on the far side anyway.
            .Where(transcript => transcript is not null)
            .Select(transcript => new HwStatsTranscript(
                transcript.CreatedAtLocalEpochSeconds,
                // A negative count is not representable across the FFI. It is
                // not reachable from a real store either — clamp rather than
                // throw, because this runs on the home view's render path.
                (uint)Math.Max(0, transcript.WordCount),
                transcript.DurationSeconds,
                transcript.Status switch
                {
                    PortableStatsTranscriptStatus.Completed => HwTranscriptStatus.Completed,
                    PortableStatsTranscriptStatus.Failed => HwTranscriptStatus.Failed,
                    PortableStatsTranscriptStatus.Processing => HwTranscriptStatus.Processing,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(transcripts), transcript.Status, null),
                }))
            .ToList() ?? [];

    private static PortablePeriodStats ToPortablePeriod(HwPeriodStats stats) =>
        new(
            // The core saturates its word total at uint.MaxValue rather than
            // trapping; saturate again on the way down to int for the same
            // reason. Both are wrong numbers, and both beat a crash on the home
            // view.
            (int)Math.Min(stats.wordCount, int.MaxValue),
            stats.durationSeconds,
            stats.averageWordsPerMinute,
            stats.estimatedTypingMinutes,
            stats.estimatedTimeSavedMinutes);

    /// <summary>
    /// Canonicalize a BCP-47 tag (issue #285): <c>en_gb</c> becomes
    /// <c>en-GB</c>, <c>ZH-HANT</c> becomes <c>zh-Hant</c>, surrounding
    /// whitespace is trimmed, and an empty or whitespace-only tag becomes
    /// <c>auto</c>.
    ///
    /// <para>The one spelling rule for every code the app stores or compares.
    /// Canonicalizing before a lookup is what makes a stored <c>en_GB</c> —
    /// which the Windows picker used to match against nothing and render as the
    /// raw tag — resolve to a real row.</para>
    ///
    /// <para>Note that a five-character subtag lowercases, which is why the
    /// LatAm Spanish key is <c>es-latam</c> and not <c>es-LATAM</c>.</para>
    /// </summary>
    public static string CanonicalizeLanguageCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return HyperwhisperCoreMethods.LanguageCanonicalize(code);
    }

    /// <summary>
    /// The canonical tag to persist. Differs from
    /// <see cref="CanonicalizeLanguageCode"/> in exactly one place: a null,
    /// empty or whitespace-only code becomes <c>en</c> rather than <c>auto</c>,
    /// because a mode with no stored language transcribes as English.
    /// </summary>
    public static string CanonicalLanguageCode(string? code) =>
        HyperwhisperCoreMethods.LanguageCanonicalCode(code);

    /// <summary>
    /// The 2-letter ISO 639 code, for the APIs that refuse anything longer.
    /// <c>auto</c> survives as itself; a null code becomes <c>en</c>; a code
    /// that is not two letters to begin with (<c>eng</c>, <c>yue</c>) is handed
    /// back unchanged rather than truncated.
    /// </summary>
    public static string NormalizeLanguageCode(string? code) =>
        HyperwhisperCoreMethods.LanguageNormalize(code);

    /// <summary>
    /// Whether a code means English, region and script variants included
    /// (<c>en-GB</c>, <c>en_us</c>). A null or absent code counts as English,
    /// matching <see cref="CanonicalLanguageCode"/>'s default; an empty string
    /// does not, because an empty stored value is an explicit "automatic".
    /// </summary>
    public static bool IsEnglishLanguage(string? code) =>
        HyperwhisperCoreMethods.LanguageIsEnglish(code);

    /// <summary>
    /// Look one code up, canonicalizing it first. Null means the catalog does
    /// not know it — use <see cref="CanonicalizeLanguageCode"/> for the tag and
    /// localize the name natively. See <see cref="PortableLanguage"/> for why
    /// that half stays on the host.
    ///
    /// <para>Named for the core function rather than for the .NET type of the
    /// same name in the Windows head (<c>HyperWhisper.Models.LanguageInfo</c>).
    /// That type is in a different assembly and a different namespace, so there
    /// is no ambiguity — and it is now a facade over this call, so the shared
    /// name is the accurate one.</para>
    /// </summary>
    public static PortableLanguage? LanguageInfo(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var native = HyperwhisperCoreMethods.LanguageInfo(code);
        return native is null ? null : ToPortableLanguage(native);
    }

    /// <summary>
    /// Every language the pickers offer, in picker order: <c>auto</c> first,
    /// then the popular codes in <see cref="PopularLanguageCodes"/> order, then
    /// the rest alphabetically by display name.
    ///
    /// <para>One FFI call returns the whole list, so bind it once into a static
    /// rather than calling it per row.</para>
    /// </summary>
    public static IReadOnlyList<PortableLanguage> AllLanguages() =>
        // A UniFFI sequence return is never null, but the generated signature
        // does not say so; `?? []` keeps a picker binding off a null reference.
        HyperwhisperCoreMethods.LanguageAll()?.Select(ToPortableLanguage).ToList() ?? [];

    /// <summary>
    /// The codes the pickers float to the top, in the order they appear there.
    /// Does not include <c>auto</c>, which sorts above them all.
    /// </summary>
    public static IReadOnlyList<string> PopularLanguageCodes() =>
        // As with AllLanguages: never null in practice, defended anyway.
        HyperwhisperCoreMethods.LanguagePopularCodes() ?? [];

    /// <summary>
    /// Canonical rows for a provider's advertised code list, deduplicated, in
    /// the order given. An unknown code keeps its canonical form and comes back
    /// with a null <see cref="PortableLanguage.DisplayName"/> — it is still a
    /// row, so a provider that advertises something we have never listed shows
    /// up in the picker instead of vanishing from it.
    /// </summary>
    public static IReadOnlyList<PortableLanguage> ResolveLanguages(IEnumerable<string>? codes) =>
        HyperwhisperCoreMethods.LanguageResolve(
            // A null code cannot cross the FFI, and neither can a null list.
            codes?.Where(code => code is not null).ToList() ?? [])
            ?.Select(ToPortableLanguage).ToList() ?? [];

    /// <summary>
    /// Move <c>auto</c> to the front of a list if it is present and not already
    /// there, leaving every other row in its given order. What a
    /// provider-filtered picker calls after <see cref="ResolveLanguages"/>, so
    /// "Automatic" stays the first entry however the provider ordered its list.
    /// </summary>
    public static IReadOnlyList<PortableLanguage> PrioritizeAutomaticLanguage(
        IEnumerable<PortableLanguage>? languages) =>
        HyperwhisperCoreMethods.LanguagePrioritizeAutomatic(
            languages?
                // A null row cannot cross the FFI.
                .Where(language => language is not null)
                .Select(language => new HwLanguage(language.Code, language.DisplayName))
                .ToList() ?? [])
            ?.Select(ToPortableLanguage).ToList() ?? [];

    private static PortableLanguage ToPortableLanguage(HwLanguage language) =>
        new(language.code, language.displayName);

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
