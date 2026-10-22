// PARAKEET TRANSCRIPTION SERVICE — START FAILURE
// How a daemon that failed to start is classified (#1598) and reported (#854).

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <summary>
    /// The error text the parakeet-engine daemon sends as its startup
    /// <c>{"status":"error"}</c> line when <c>EngineSession.Create</c> throws, i.e.
    /// when no recognizer could be built from the model directory
    /// (tools/parakeet-engine/main.cpp, the x64 build: the sherpa-onnx C API returned
    /// null; tools/parakeet-engine-dotnet/Program.cs, the ARM64 build: any exception).
    /// </summary>
    internal const string DaemonModelLoadError = "Failed to load model";

    /// <summary>
    /// True when the daemon's startup error says it could not load the model's own
    /// files (#1598), so a re-download can fix it. Its other startup error,
    /// "Invalid arguments", is the app's fault and does not count.
    /// </summary>
    internal static bool IsModelLoadErrorResponse(string? error) =>
        string.Equals(error?.Trim(), DaemonModelLoadError, StringComparison.Ordinal);

    /// <summary>
    /// Stderr text that names a fault in one of the model's own files, written by
    /// sherpa-onnx's config check (a model file that "does not exist") or by ONNX
    /// Runtime when it cannot parse a model (#1598). Counts only on a line that also
    /// names the model directory, so the shipped silero_vad.onnx never counts.
    /// </summary>
    private static readonly string[] ModelFileFaultMarkers =
    {
        "does not exist",
        "Protobuf parsing failed",
        "INVALID_PROTOBUF",
        "INVALID_GRAPH",
        "No graph was found in the protobuf",
        "Load model from",
    };

    /// <summary>
    /// The message of the SEHException .NET raises when sherpa-onnx's native code
    /// throws (ONNX Runtime rejecting a damaged model) inside the recognizer
    /// constructor. The .NET engine (tools/parakeet-engine-dotnet, the ARM64 build)
    /// logs it with no path, so it counts on its own.
    /// </summary>
    private const string NativeLoadExceptionMarker = "External component has thrown an exception";

    /// <summary>
    /// True when the daemon's startup error is "Failed to load model" AND its stderr
    /// names a fault in this model's files (#1598), so a re-download can fix it.
    /// The daemon sends the same line for every failure to build the recognizer: a
    /// DllNotFoundException or TypeInitializationException for sherpa-onnx or
    /// onnxruntime, OutOfMemoryException, or a DirectML and CPU provider that both
    /// fail on a valid model. None of those name a model file on stderr, so none mark
    /// the model broken. "Invalid arguments" and the Nemotron validation errors
    /// (a pinned-size download already rules out a truncated vocab) never count.
    /// </summary>
    internal static bool IsModelFileLoadFailure(string? error, IReadOnlyList<string>? stderrLines, string? modelDirectory)
    {
        if (!IsModelLoadErrorResponse(error) || stderrLines == null) return false;

        var directory = NormalizeForMatch(modelDirectory);
        foreach (var raw in stderrLines)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (raw.Contains(NativeLoadExceptionMarker, StringComparison.OrdinalIgnoreCase)) return true;
            if (directory.Length == 0) continue;

            var line = NormalizeForMatch(raw);
            if (!line.Contains(directory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var marker in ModelFileFaultMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// True only for 0xC0000409, the fail-fast exit of a daemon whose ONNX Runtime
    /// threw on a damaged model file while it loaded the model (#1598: a truncated
    /// encoder.int8.onnx). Every other pre-READY exit (a missing DLL, a DLL init
    /// failure, an access violation from a GPU driver, OOM, a kill, an argument
    /// error, or a daemon still alive) is not something a re-download can fix, so
    /// it does not mark the model broken.
    /// </summary>
    internal static bool IsModelLoadCrashExitCode(int? exitCode) =>
        exitCode is { } code && unchecked((uint)code) == 0xC0000409;

    private static string FormatExitCode(int? exitCode) =>
        exitCode is { } code ? $"0x{unchecked((uint)code):X8}" : "unknown (still running)";

    // =========================================================================
    // START-FAILURE DIAGNOSTICS (#854)
    // =========================================================================

    /// <summary>How many of the daemon's last stderr lines a start failure carries.</summary>
    internal const int StartFailureTailLines = 20;

    /// <summary>The cap on the whole scrubbed stderr tail, in characters.</summary>
    internal const int StartFailureTailMaxChars = 2000;

    /// <summary>The <c>parakeet_failure</c> slugs, one per start-failure branch.</summary>
    internal const string StartFailureStdoutClosed = "stdout_closed";
    internal const string StartFailureReadyTimeout = "ready_timeout";
    internal const string StartFailureInvalidJson = "invalid_json";
    internal const string StartFailureDaemonError = "daemon_error";

    /// <summary>How long a failure branch waits for the daemon's exit code, then for
    /// its stderr to end, before it reports. Only the failure path waits.</summary>
    private const int StartFailureDiagnosticWaitMilliseconds = 500;

    /// <summary>
    /// The daemon's last stderr lines as one string: each line passed through
    /// <paramref name="redact"/> FIRST, then the whole joined tail capped to its last
    /// <see cref="StartFailureTailMaxChars"/> characters (a dying daemon writes its
    /// cause last). Redacting before the cap means a cut can never leave a piece of
    /// an identifier that no longer matches. <c>(none)</c> when stderr was empty.
    /// </summary>
    /// <param name="redact">
    /// <see cref="SentryService.RedactUserIdentifiers(string?)"/> in the app (the
    /// profile directory becomes <c>%USERPROFILE%</c>, local-app-data
    /// <c>%LOCALAPPDATA%</c>, the account name <c>%USER%</c>); the smoke tests pass
    /// its seam overload with fixed identifiers.
    /// </param>
    internal static string BuildStderrTail(IReadOnlyList<string>? lines, Func<string?, string> redact)
    {
        if (lines == null || lines.Count == 0) return "(none)";

        var start = Math.Max(0, lines.Count - StartFailureTailLines);
        var scrubbedLines = new List<string>(lines.Count - start);
        for (var i = start; i < lines.Count; i++)
        {
            scrubbedLines.Add(redact(lines[i]));
        }

        var tail = string.Join("\n", scrubbedLines);
        if (tail.Length <= StartFailureTailMaxChars) return tail;

        // "…" + the last (cap - 1) characters, never starting on half a surrogate pair.
        var cut = tail.Length - (StartFailureTailMaxChars - 1);
        if (char.IsLowSurrogate(tail[cut])) cut++;
        return "…" + tail.Substring(cut);
    }

    /// <summary>
    /// The fields one start failure carries, in the order the log line prints them.
    /// Every value is metadata: a fixed slug, a number, the model FOLDER name and a
    /// stderr tail already scrubbed by <see cref="BuildStderrTail"/>. Never a path,
    /// a transcript, audio, or text the user typed.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, object>> BuildStartFailureFields(
        string failure,
        int? exitCode,
        string engine,
        string modelId,
        string? languageHint,
        int readyTimeoutSeconds,
        long elapsedMilliseconds,
        int stderrLineCount,
        IReadOnlyList<string>? stderrTail,
        Func<string?, string> redact)
    {
        return new KeyValuePair<string, object>[]
        {
            new("parakeet_failure", failure),
            new("parakeet_exit_code", exitCode ?? -1),
            new("parakeet_engine", engine),
            new("parakeet_model_id", modelId),
            new("parakeet_language_hint", string.IsNullOrWhiteSpace(languageHint) ? "none" : languageHint),
            new("parakeet_ready_timeout_s", readyTimeoutSeconds),
            new("parakeet_elapsed_ms", elapsedMilliseconds),
            new("parakeet_stderr_line_count", stderrLineCount),
            new("parakeet_stderr_tail", BuildStderrTail(stderrTail, redact)),
        };
    }

    /// <summary>The fields as one log-line suffix: <c>key=value, ...</c>, with the
    /// tail's line breaks shown as <c> | </c> so the failure stays one log line.</summary>
    internal static string FormatStartFailureFields(IReadOnlyList<KeyValuePair<string, object>> fields) =>
        string.Join(", ", fields.Select(f =>
            $"{f.Key}={Convert.ToString(f.Value, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\n", " | ")}"));

    /// <summary>
    /// Writes one start failure's cause to the local log as the branch's ERROR line,
    /// and sends ONE Sentry diagnostic for it when the user allows error reporting.
    /// Diagnostics only: it never throws, so the branch still throws its own
    /// <see cref="TranscriptionException"/> unchanged.
    /// </summary>
    private static void ReportStartFailure(
        string logMessage,
        IReadOnlyList<KeyValuePair<string, object>> fields,
        Exception? exception = null)
    {
        // The daemon's own error text can sit in the message (daemon_error), so it is
        // redacted like the tail before it reaches the log.
        logMessage = SentryService.RedactUserIdentifiers(logMessage);
        try
        {
            var line = $"{logMessage} ({FormatStartFailureFields(fields)})";
            if (exception != null) LoggingService.Error(line, exception);
            else LoggingService.Error(line);
        }
        catch (Exception)
        {
            LoggingService.Error(logMessage);
        }

        try
        {
            if (!SettingsService.Instance.EnableErrorLogging) return;

            var extras = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (key, value) in fields) extras[key] = value;
            var failure = Convert.ToString(extras["parakeet_failure"], System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

            SentryService.CaptureDiagnosticEvent(
                message: "Parakeet daemon start failed",
                extras: extras,
                tags: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["component"] = "parakeet",
                    ["diagnostic_name"] = "parakeet_daemon_start_failed",
                    ["parakeet_failure"] = failure,
                },
                fingerprint: new[] { "parakeet-daemon-start-failed", failure },
                // One report per cause per session: a recording retries a failed load,
                // and the same dead model must not send an event every time.
                dedupeKey: $"parakeet-daemon-start-failed:{failure}:{extras["parakeet_model_id"]}:{extras["parakeet_exit_code"]}");
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: Start-failure report failed: {ex.GetType().Name}");
        }
    }
}
