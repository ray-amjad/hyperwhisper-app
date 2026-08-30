using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public enum PortableCursorContext
{
    Unknown,
    StartOfSentence,
    MidSentence,
}

/// <summary>
/// Stable public surface over the generated UniFFI binding. Platform projects
/// consume this assembly instead of compiling private copies of the binding.
/// </summary>
public static partial class SharedCoreBridge
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
}
