using System.Diagnostics;
using Sentry;

namespace HyperWhisper.Services;

/// <summary>
/// Why one auto-paste attempt ended the way it did.
///
/// The slugs behind these values are stable and low-cardinality. They become the
/// Sentry message and the <c>paste_outcome</c> tag, so a query can count each
/// failure mode over a release without a text search.
/// </summary>
/// <remarks>
/// This mirrors the macOS <c>AccessibilityHelper.PasteOutcome</c> added for the
/// same step of the same flow, so both platforms answer "why did the transcript
/// not arrive" with the same vocabulary.
/// </remarks>
public enum PasteOutcome
{
    /// <summary>Ctrl+V was posted into the intended window.</summary>
    Pasted,

    /// <summary>Auto-paste is off, so the transcript was copied and nothing else.</summary>
    ClipboardOnly,

    /// <summary>The flow asked to deliver an empty transcript. Always a defect.</summary>
    EmptyText,

    /// <summary>Writing the clipboard threw. The transcript reached nothing at all.</summary>
    ClipboardSetFailed,

    /// <summary>No target window was captured, so the transcript stayed on the clipboard.</summary>
    NoTargetWindow,

    /// <summary>A password field is focused. A deliberate refusal, not a fault.</summary>
    SecureFieldSkipped,

    /// <summary>Permission and focus were fine and the keystroke still threw. Always a defect.</summary>
    KeystrokeFailed
}

/// <summary>
/// Metadata for one auto-paste attempt.
///
/// PRIVACY: this type must never hold the transcript. <see cref="CharacterCount"/>
/// is a count, not the text, and there is no field for the text itself.
/// </summary>
internal sealed class PasteAttempt
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Process name of the window the paste targets, when it is known.</summary>
    public string? TargetProcessName { get; set; }

    /// <summary>Chromium/native classification, which selects the paste fast path.</summary>
    public string TargetAppKind { get; set; } = "unknown";

    /// <summary>True when recording start captured a foreground window.</summary>
    public bool HadCapturedTarget { get; set; }

    /// <summary>True when that window handle is still a live window at paste time.</summary>
    public bool TargetWindowAlive { get; set; }

    /// <summary>True when the target window is minimized at paste time.</summary>
    public bool TargetWindowMinimized { get; set; }

    /// <summary>True when the focus poll confirmed focus before the paste.</summary>
    public bool FocusReady { get; set; }

    /// <summary>How long the focus poll ran, in ms.</summary>
    public int FocusWaitMs { get; set; }

    /// <summary>Length of the text to deliver. A count only — never the text.</summary>
    public int CharacterCount { get; set; }

    /// <summary>Whether the clipboard write asked Windows to hide the entry from Win+V.</summary>
    public bool HideFromClipboardHistory { get; set; }

    public int ElapsedMs => (int)_stopwatch.ElapsedMilliseconds;
}

/// <summary>
/// Production visibility for the last step of the record → transcribe → paste flow.
///
/// Auto-paste had no error reporting at all: every way it can fail returned a
/// coarse <see cref="Models.SmartPasteResult"/> and wrote one line into the log
/// file on the user's own machine. A transcript that never reached the target
/// app was therefore invisible in production, while the transcription step in
/// front of it holds six live Sentry issues.
///
/// PRIVACY: nothing here touches the transcript. Every reported value is a
/// count, a boolean, a duration or a fixed slug, plus the target process name.
/// </summary>
internal static class SmartPasteDiagnostics
{
    private static readonly HashSet<string> _reportedSlugs = new(StringComparer.Ordinal);
    private static readonly object _reportedLock = new();

    /// <summary>
    /// Claims the one report this app run allows for a slug.
    /// </summary>
    /// <returns>True the first time a slug is seen, false afterwards.</returns>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static bool MarkReportedThisRun(string slug)
    {
        lock (_reportedLock)
        {
            return _reportedSlugs.Add(slug);
        }
    }

    /// <summary>
    /// The stable slug for an outcome. An added outcome without an arm here
    /// throws rather than reporting under another outcome's identity; the smoke
    /// tests walk every enum value, so that fails in CI and not in production.
    /// </summary>
    internal static string Slug(PasteOutcome outcome) => outcome switch
    {
        PasteOutcome.Pasted => "pasted",
        PasteOutcome.ClipboardOnly => "clipboard_only",
        PasteOutcome.EmptyText => "empty_text",
        PasteOutcome.ClipboardSetFailed => "clipboard_set_failed",
        PasteOutcome.NoTargetWindow => "no_target_window",
        PasteOutcome.SecureFieldSkipped => "secure_field_skipped",
        PasteOutcome.KeystrokeFailed => "keystroke_failed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome),
            outcome,
            "No slug is defined for this paste outcome.")
    };

    /// <summary>
    /// True when the transcript did not reach the target window AND the cause is
    /// a defect or a broken environment rather than a deliberate refusal.
    ///
    /// <see cref="PasteOutcome.SecureFieldSkipped"/> stays out on purpose: it is
    /// normal (the user is typing a password), it leaves the transcript on the
    /// clipboard, and it is frequent enough to flood the issue stream.
    /// </summary>
    internal static bool IsReportable(PasteOutcome outcome) => outcome switch
    {
        PasteOutcome.EmptyText => true,
        PasteOutcome.ClipboardSetFailed => true,
        PasteOutcome.NoTargetWindow => true,
        PasteOutcome.KeystrokeFailed => true,
        _ => false
    };

    /// <summary>
    /// True when the outcome describes a defect in the app rather than the state
    /// of the user's desktop. Drives the Sentry level: a failed clipboard write
    /// or keystroke is an error, a missing target window is a warning.
    /// </summary>
    internal static bool IsDefect(PasteOutcome outcome) =>
        outcome is PasteOutcome.ClipboardSetFailed or PasteOutcome.KeystrokeFailed;

    /// <summary>
    /// Records the end of one auto-paste attempt.
    ///
    /// Every attempt writes one log line. A reportable failure also sends one
    /// Sentry event behind the <c>EnableErrorLogging</c> opt-in.
    ///
    /// This method only logs. It returns nothing, it throws nothing, and it must
    /// never change what the caller returns.
    /// </summary>
    internal static void Report(PasteOutcome outcome, PasteAttempt attempt, Exception? exception = null)
    {
        var slug = Slug(outcome);
        var summary =
            $"outcome={slug} " +
            $"targetProcess={attempt.TargetProcessName ?? "unknown"} " +
            $"targetKind={attempt.TargetAppKind} " +
            $"capturedTarget={attempt.HadCapturedTarget} " +
            $"targetAlive={attempt.TargetWindowAlive} " +
            $"targetMinimized={attempt.TargetWindowMinimized} " +
            $"focusReady={attempt.FocusReady} " +
            $"focusWaitMs={attempt.FocusWaitMs} " +
            $"chars={attempt.CharacterCount} " +
            $"hiddenFromHistory={attempt.HideFromClipboardHistory} " +
            $"elapsedMs={attempt.ElapsedMs}";

        if (!IsReportable(outcome))
        {
            // Debug, not Info: the streaming path pastes once per final segment,
            // and the caller already writes its own Info line per delivery. A
            // second Info line per segment would bury the rest of the log.
            LoggingService.Debug($"SmartPasteService: Auto-paste finished · {summary}");
            return;
        }

        if (exception != null)
        {
            LoggingService.Error($"SmartPasteService: Auto-paste failed · {summary}", exception);
        }
        else
        {
            LoggingService.Error($"SmartPasteService: Auto-paste failed · {summary}");
        }

        // Respect the user's opt-in before anything leaves the machine.
        if (!SettingsService.Instance.EnableErrorLogging)
        {
            return;
        }

        // The streaming path pastes once per final transcript segment, so a
        // broken target would send one event per sentence for the whole session.
        // Report each outcome once per app run instead. Sentry's event count is
        // therefore a count of affected app runs, not of attempts — the extra
        // below states that, so nobody reads the number as an attempt count.
        if (!MarkReportedThisRun(slug))
        {
            return;
        }

        var extras = new Dictionary<string, object>
        {
            ["paste_outcome"] = slug,
            ["paste_target_process"] = attempt.TargetProcessName ?? "unknown",
            ["paste_target_app_kind"] = attempt.TargetAppKind,
            ["paste_had_captured_target"] = attempt.HadCapturedTarget,
            ["paste_target_window_alive"] = attempt.TargetWindowAlive,
            ["paste_target_window_minimized"] = attempt.TargetWindowMinimized,
            ["paste_focus_ready"] = attempt.FocusReady,
            ["paste_focus_wait_ms"] = attempt.FocusWaitMs,
            ["paste_character_count"] = attempt.CharacterCount,
            ["paste_hide_from_clipboard_history"] = attempt.HideFromClipboardHistory,
            ["paste_elapsed_ms"] = attempt.ElapsedMs,
            ["paste_reported_once_per_run"] = true
        };

        // The target process is an extra, not a tag: a tag is faceted, and .exe
        // names are effectively unbounded on Windows. The app kind carries the
        // faceting instead, and it is the axis the paste path actually branches on.
        var tags = new Dictionary<string, string>
        {
            ["component"] = "auto_paste",
            ["paste_outcome"] = slug,
            ["paste_target_app_kind"] = attempt.TargetAppKind
        };

        var fingerprint = new[] { "auto-paste", slug, attempt.TargetAppKind };

        if (exception != null)
        {
            SentryService.Capture(
                exception,
                message: $"Auto-paste failed: {slug}",
                extras: extras,
                tags: tags,
                fingerprint: fingerprint,
                level: IsDefect(outcome) ? SentryLevel.Error : SentryLevel.Warning);
            return;
        }

        // No exception to attach — report it through the diagnostic-event path,
        // which is the same shape the no-speech diagnostic already uses.
        SentryService.CaptureDiagnosticEvent(
            message: $"Auto-paste failed: {slug}",
            extras: extras,
            tags: tags,
            fingerprint: fingerprint);
    }
}
