using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Sentry;

namespace HyperWhisper.Services;

/// <summary>
/// SENTRY SERVICE
///
/// Lightweight wrapper around Sentry SDK with privacy-safe defaults.
/// Provides error tracking, performance monitoring, and crash reporting.
///
/// FEATURES:
/// - Performance tracing (100% sampling) for identifying slow operations
/// - Release health tracking for crash-free session monitoring
/// - Custom spans for instrumenting transcription pipeline
/// - Device/system tags for filtering issues by hardware
/// - User action breadcrumbs for understanding flows before errors
/// - Privacy sanitization (breadcrumbs stripped, transcripts redacted)
///
/// MATCHING MACOS IMPLEMENTATION:
/// This implementation mirrors SentryService.swift from the macOS app.
/// Same DSN, same configuration, same privacy features.
///
/// CONFIGURATION:
/// - DSN: Shared with macOS app (single Sentry project for both platforms)
/// - Environment: development (DEBUG) / production (RELEASE)
/// - Traces sample rate: 100%
/// - App hang detection: 10 second timeout
/// </summary>
public static class SentryService
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>
    /// Sentry DSN (Data Source Name). Resolved at runtime from, in order:
    /// the SENTRY_DSN environment variable (local/dev override), then a value
    /// baked into the assembly at build time via AssemblyMetadata (release
    /// builds — injected from the SENTRY_DSN env var by the csproj). Never
    /// committed to source. Empty by default — Initialize() no-ops on a blank
    /// DSN, so the open-source build simply runs without error tracking.
    /// </summary>
    private static string SentryDsn =>
        Environment.GetEnvironmentVariable("SENTRY_DSN")
        ?? Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SentryDsn")?.Value
        ?? string.Empty;

    // =========================================================================
    // STATE
    // =========================================================================

    private static bool _isInitialized = false;
    private static IDisposable? _sentryInstance = null;
    private static readonly object _diagnosticLock = new();
    private static readonly HashSet<string> _capturedDiagnosticKeys = new(StringComparer.Ordinal);

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Initialize Sentry with default configuration.
    /// Call this early in app startup (after logging, before main UI).
    ///
    /// Configuration matches macOS SentryService.swift:
    /// - 100% performance tracing
    /// - 10-second app hang timeout
    /// - Privacy sanitization in beforeSend
    /// - Device/system tags for filtering
    /// </summary>
    public static void Initialize()
    {
        Initialize(SentryDsn, null);
    }

    /// <summary>
    /// Initialize with explicit DSN (for testing or custom configuration).
    /// No-op if DSN is null/empty or already initialized.
    /// </summary>
    /// <param name="dsn">Sentry DSN URL</param>
    /// <param name="environment">Optional environment override (development/production)</param>
    public static void Initialize(string? dsn, string? environment = null)
    {
        if (_isInitialized)
        {
            LoggingService.Debug("SentryService: Already initialized, skipping");
            return;
        }

        if (string.IsNullOrWhiteSpace(dsn))
        {
            LoggingService.Debug("SentryService: No DSN provided, skipping initialization");
            return;
        }

        try
        {
            // Get version info from assembly
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var buildNumber = assembly.GetName().Version?.Revision.ToString() ?? "0";

            // Resolve environment: explicit > conditional compilation > fallback
            var resolvedEnv = !string.IsNullOrWhiteSpace(environment)
                ? environment
#if DEBUG
                : "development";
#else
                : "production";
#endif

            LoggingService.Info($"SentryService: Initializing with environment={resolvedEnv}, release=hyperwhisper@{version}");

            _sentryInstance = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Environment = resolvedEnv;
                options.Release = $"hyperwhisper@{version}";

#if DEBUG
                options.Debug = true;
#endif

                // PERFORMANCE TRACING
                // Sample 100% of transactions to capture all performance data
                // This lets us see slow transcriptions, API calls, and UI operations
                // For a small user base, 100% is fine; reduce if Sentry quota becomes an issue
                options.TracesSampleRate = 1.0;

                // PROFILING
                // CPU profiling for slow operations - helps identify code bottlenecks
                // Attached to sampled transactions
                options.ProfilesSampleRate = 1.0;

                // RELEASE HEALTH
                // Tracks crash-free sessions per release
                // Enables "Release Health" dashboard in Sentry showing:
                // - Crash-free session % (e.g., "2.10 has 99.2% crash-free")
                // - Adoption rate (how many users upgraded)
                // - Session count per release
                options.AutoSessionTracking = true;

                // Follow docs: include IP (PII); gated by EnableErrorLogging setting
                options.SendDefaultPii = true;

                // Attach stack traces to messages/errors for better debugging
                options.AttachStacktrace = true;

                // PRIVACY SANITIZATION
                // Scrub potentially sensitive data from error events
                options.SetBeforeSend((sentryEvent, hint) =>
                {
                    // Note: Breadcrumbs are read-only in C# SDK, but we don't add any
                    // with sensitive data, and the beforeSend hook provides extra protection.
                    // If needed, breadcrumbs could be disabled entirely via options.MaxBreadcrumbs = 0

                    // Drop any suspicious extras (transcript, text, prompt), and rewrite
                    // the user's Windows identifiers out of every extra that survives.
                    // The redacted branch is never re-examined, which is what makes the
                    // "not already [redacted]" rule structural rather than a second check.
                    if (sentryEvent.Extra != null)
                    {
                        var sanitizedExtras = new Dictionary<string, object?>();
                        foreach (var kvp in sentryEvent.Extra)
                        {
                            sanitizedExtras[kvp.Key] = IsRedactedExtraKey(kvp.Key)
                                ? "[redacted]"
                                : kvp.Value is string extraText
                                    ? RedactUserIdentifiers(extraText)
                                    : kvp.Value;
                        }
                        // Clear and re-add sanitized extras
                        foreach (var kvp in sanitizedExtras)
                        {
                            sentryEvent.SetExtra(kvp.Key, kvp.Value);
                        }
                    }

                    // The exception message itself (HYPERWHISPER-Y5 / -YF / -Z1). The
                    // filter above only ever looked at extras and matched on the KEY, so
                    // a FileLoadException carried the install path - and the user's
                    // account name with it - into the Sentry issue TITLE.
                    var sentryExceptions = sentryEvent.SentryExceptions?.ToList();
                    if (sentryExceptions != null)
                    {
                        foreach (var sentryException in sentryExceptions)
                        {
                            if (sentryException.Value != null)
                            {
                                sentryException.Value = RedactUserIdentifiers(sentryException.Value);
                            }
                        }
                        sentryEvent.SentryExceptions = sentryExceptions;
                    }

                    // CaptureMessage events. Capture() puts the caller's text in the
                    // error_message EXTRA instead, so Message is null on the events in
                    // this issue - the extras branch above is what covers those. Each
                    // property is rewritten only when it is non-null, so a null Formatted
                    // stays null rather than becoming "".
                    var message = sentryEvent.Message;
                    if (message != null)
                    {
                        if (message.Formatted != null)
                        {
                            message.Formatted = RedactUserIdentifiers(message.Formatted);
                        }

                        if (message.Message != null)
                        {
                            message.Message = RedactUserIdentifiers(message.Message);
                        }

                        sentryEvent.Message = message;
                    }

                    // Every step above is total: RedactUserIdentifiers never throws for
                    // any input, and nothing here indexes or parses. A throw inside
                    // beforeSend costs the whole event, so keep it that way.
                    return sentryEvent;
                });

                // Disable breadcrumbs to avoid leaking text content via logs
                // This matches the macOS implementation which strips breadcrumbs before sending
                options.MaxBreadcrumbs = 0;
            });

            // DEVICE/SYSTEM TAGS
            // Set global tags for filtering issues by hardware/software configuration
            // These tags appear on every event, making it easy to filter in Sentry UI
            SentrySdk.ConfigureScope(scope =>
            {
                // Windows version (e.g., "10.0.22631.0")
                scope.SetTag("windows_version", Environment.OSVersion.VersionString);

                // Build number for precise version tracking
                scope.SetTag("build_number", buildNumber);

                // CPU architecture - helps identify x64 vs ARM64 issues
                scope.SetTag("architecture", RuntimeInformation.ProcessArchitecture.ToString());

                // Processor count - helps identify performance issues on low-core machines
                scope.SetTag("cpu_cores", Environment.ProcessorCount.ToString());
            });

            _isInitialized = true;
            LoggingService.Info("SentryService: Initialization complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("SentryService: Failed to initialize", ex);
            // Don't throw - Sentry failing shouldn't crash the app
        }
    }

    /// <summary>
    /// Whether the Sentry privacy filter replaces this extra's value with <c>"[redacted]"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The match is on the KEY only, and it is a substring match, so it cannot tell a
    /// transcript from a field that merely has "transcript" in its name. That is the
    /// safe direction for a privacy filter and it stays exactly as it was — but it
    /// also means a diagnostic field named <c>transcription_provider_display_name</c>
    /// or <c>backend_empty_transcript_without_flag</c> arrives at Sentry as
    /// <c>"[redacted]"</c>, and the call site gets no warning. Three fields of the
    /// Windows no-speech diagnostic were lost that way for every event of
    /// HYPERWHISPER-PA/-RM/-XR.
    /// </para>
    /// <para>
    /// "path" is here as a backstop, not as the fix. Recordings, models and
    /// user-picked media all live under the user's profile, so any full path carries
    /// their Windows account name. A call site must still send a description of the
    /// file rather than the file (<see cref="LoggingService.DescribePath"/>); this
    /// only stops the next one that forgets.
    /// </para>
    /// <para>
    /// Name a metadata field so it does not collide. The smoke tests assert this for
    /// every key the no-speech diagnostic emits, so a colliding name fails in CI
    /// rather than going quiet in production.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static bool IsRedactedExtraKey(string key)
    {
        var keyLower = key.ToLowerInvariant();
        return keyLower.Contains("transcript")
            || keyLower.Contains("text")
            || keyLower.Contains("prompt")
            || keyLower.Contains("path");
    }

    /// <summary>
    /// Replaces the signed-in user's Windows identifiers - profile directory,
    /// local-app-data directory and bare account name - with fixed tokens, and
    /// leaves the rest of the text alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HYPERWHISPER-Y5/-YF/-Z1 put three real Windows account names in the Sentry
    /// issue TITLE. A <c>FileLoadException</c> message embeds the full install path,
    /// that path holds the account name, and the privacy filter in
    /// <c>SetBeforeSend</c> only ever rewrote <c>Extra</c> - matching on the KEY, so
    /// the exception's own message was never examined. The rule was already written
    /// down at <see cref="OptionalAssemblyGuard"/>: never the exception message and
    /// never <c>FileLoadException.FileName</c>, because both carry the installed
    /// path and that path holds the user's account name. This is what keeps it on
    /// the unhandled path, which does not go through that guard.
    /// </para>
    /// <para>
    /// The identifiers are read from the process rather than matched by pattern. The
    /// sentence around the path is localized by Windows - the German form of "an
    /// Application Control policy has blocked this file" is what HYPERWHISPER-Y5
    /// actually carries - but the PATH is not localized, so matching the literal
    /// directory is locale-proof. A <c>C:\Users\[^\\]+</c> regex is not: it misses a
    /// redirected profile and it misses a non-<c>C:</c> drive.
    /// </para>
    /// <para>
    /// Order is load-bearing, and it is the reason for the private helper below. The
    /// bare account name is a SUBSTRING of both directories (<c>C:\Users\bob</c>
    /// contains <c>bob</c>), so replacing the name first would leave
    /// <c>C:\Users\%USER%\AppData\...</c>, the directory steps would then match
    /// nothing, and <c>C:\Users\</c> would survive. The two long, specific prefixes
    /// go first and the bare name goes LAST. Between the two directories the order is
    /// not load-bearing: local-app-data normally sits inside the profile, so the
    /// second step is a no-op on the common path. It is still not dead code - it is
    /// what catches a local-app-data directory redirected to another drive, which the
    /// profile prefix never matches. That directory is read from the environment
    /// rather than from the special-folder API, because only <c>AppPaths</c> may read
    /// that special folder (see
    /// <c>scripts/verify_isolated_app_profile_paths.ps1</c>).
    /// </para>
    /// <para>
    /// This deliberately does NOT do what <c>TelemetryPrivacy.SanitizeException</c>
    /// does on Linux. That throws away the message, the inner exception and the
    /// HRESULT. Here <c>0x800711C7</c> and the assembly simple name are the entire
    /// diagnosis, so only the identifiers go. The Linux blanket form is the fallback
    /// if maximum safety is ever wanted over diagnosis.
    /// </para>
    /// <para>
    /// Over-redaction is possible and accepted: an account named <c>System</c> turns
    /// unrelated occurrences of that word into <c>%USER%</c>. That is the safe
    /// direction for a privacy filter and it is how
    /// <see cref="IsRedactedExtraKey"/> already errs. Beyond the blank and
    /// drive-root guards there are no length heuristics. The method is total - it
    /// never throws, for any input - because it runs inside <c>beforeSend</c>, where
    /// a throw costs the whole event.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string RedactUserIdentifiers(string? value)
    {
        return RedactUserIdentifiers(
            value,
            ReadIdentifier(static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ReadIdentifier(static () => Environment.GetEnvironmentVariable("LOCALAPPDATA")),
            ReadIdentifier(static () => Environment.UserName));

        // An environment that refuses to be read must not cost the event, and must
        // not take the other two identifiers down with it either.
        static string? ReadIdentifier(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The <see cref="RedactUserIdentifiers(string?)"/> seam: the same redaction with
    /// the three identifiers supplied instead of read from the live environment.
    /// </summary>
    /// <remarks>
    /// The smoke tests need this. The live entry point reads the account of whoever
    /// is running it, so a case built on a fixed <c>C:\Users\testaccount\...</c>
    /// string would pass only on a machine owned by a user named <c>testaccount</c>
    /// and fail on the CI runner, where the profile is <c>C:\Users\runneradmin</c>.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string RedactUserIdentifiers(
        string? value,
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        // Longest and most specific first; the bare account name LAST. See the
        // remarks on the one-argument overload for why that order is load-bearing.
        var redacted = ReplaceIdentifier(value, userProfileDirectory, "%USERPROFILE%", isDirectory: true);
        redacted = ReplaceIdentifier(redacted, localAppDataDirectory, "%LOCALAPPDATA%", isDirectory: true);
        redacted = ReplaceIdentifier(redacted, userName, "%USER%", isDirectory: false);
        return redacted;
    }

    /// <summary>
    /// Replaces one identifier with its token, or returns the text unchanged when the
    /// identifier is not safe to feed to <c>Replace</c>.
    /// </summary>
    /// <remarks>
    /// <c>string.Replace(oldValue, newValue, StringComparison)</c> THROWS
    /// <c>ArgumentException</c> on a zero-length <c>oldValue</c>, so a blank account
    /// name or an unset environment variable would throw inside <c>beforeSend</c> and
    /// lose the event. A bare drive root (<c>C:\</c>) is skipped as well: replacing
    /// it would mangle every path in the message for no privacy gain.
    /// </remarks>
    private static string ReplaceIdentifier(string value, string? identifier, string token, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return value;
        }

        var candidate = isDirectory
            ? identifier.TrimEnd('\\', '/')
            : identifier;

        // "C:", "C:\", "D:/" - a drive root, and nothing else is this short.
        if (isDirectory && candidate.Length <= 3)
        {
            return value;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return value;
        }

        return value.Replace(candidate, token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shutdown Sentry and flush pending events.
    /// Call this in app exit to ensure all events are sent.
    /// </summary>
    public static void Shutdown()
    {
        if (!_isInitialized)
        {
            return;
        }

        try
        {
            LoggingService.Debug("SentryService: Shutting down, flushing events...");

            // Flush pending events (2 second timeout)
            SentrySdk.Flush(TimeSpan.FromSeconds(2));

            // Dispose the SDK instance
            _sentryInstance?.Dispose();
            _sentryInstance = null;
            _isInitialized = false;

            LoggingService.Debug("SentryService: Shutdown complete");
        }
        catch (Exception ex)
        {
            LoggingService.Error("SentryService: Error during shutdown", ex);
        }
    }

    // =========================================================================
    // BREADCRUMBS
    // =========================================================================

    /// <summary>
    /// Add a breadcrumb for debugging flow before errors occur.
    /// Note: Breadcrumbs are stripped before sending to Sentry for privacy,
    /// but are useful for local debugging and understanding user flows.
    /// </summary>
    /// <param name="message">Human-readable description of the event</param>
    /// <param name="category">Category for grouping (e.g., "ui", "transcription", "network")</param>
    /// <param name="level">Severity level (default: Info)</param>
    /// <param name="data">Optional additional data dictionary</param>
    public static void AddBreadcrumb(
        string message,
        string category,
        BreadcrumbLevel level = BreadcrumbLevel.Info,
        Dictionary<string, string>? data = null)
    {
        if (!_isInitialized) return;

        try
        {
            SentrySdk.AddBreadcrumb(
                message: message,
                category: category,
                level: level,
                data: data
            );
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to add breadcrumb: {ex.Message}");
        }
    }

    // =========================================================================
    // ERROR CAPTURE
    // =========================================================================

    /// <summary>
    /// Capture an exception with optional message and extra context.
    /// </summary>
    /// <param name="exception">The exception to capture</param>
    /// <param name="message">Optional descriptive message (improves grouping and readability)</param>
    /// <param name="extras">Additional context (will NOT affect grouping)</param>
    /// <param name="tags">Tags for filtering (will NOT affect grouping)</param>
    /// <param name="fingerprint">Optional custom fingerprint for grouping</param>
    public static void Capture(
        Exception exception,
        string? message = null,
        Dictionary<string, object>? extras = null,
        Dictionary<string, string>? tags = null,
        string[]? fingerprint = null,
        SentryLevel? level = null)
    {
        if (!_isInitialized)
        {
            LoggingService.Debug("SentryService: Not initialized, skipping capture");
            return;
        }

        try
        {
            var sentryEvent = new SentryEvent(exception);
            if (level.HasValue)
            {
                sentryEvent.Level = level.Value;
            }

            SentrySdk.CaptureEvent(sentryEvent, scope =>
            {
                // Set message for better issue titles in Sentry UI
                if (!string.IsNullOrEmpty(message))
                {
                    scope.SetExtra("error_message", message);
                }

                // Add tags
                if (tags != null)
                {
                    foreach (var (key, value) in tags)
                    {
                        scope.SetTag(key, value);
                    }
                }

                // Add extras (sanitized by beforeSend)
                if (extras != null)
                {
                    foreach (var (key, value) in extras)
                    {
                        scope.SetExtra(key, value);
                    }
                }

                // Set custom fingerprint for proper grouping
                if (fingerprint != null && fingerprint.Length > 0)
                {
                    scope.SetFingerprint(fingerprint);
                }
                else if (!string.IsNullOrEmpty(message))
                {
                    // Default: group by message + error type
                    var errorType = exception.GetType().Name;
                    scope.SetFingerprint(new[] { "{{ default }}", message, errorType });
                }
            });

            LoggingService.Debug($"SentryService: Captured exception: {exception.GetType().Name}");
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to capture exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Capture a message (non-exception event).
    /// Use for important events that aren't errors.
    /// </summary>
    /// <param name="message">The message to capture</param>
    /// <param name="level">Severity level (default: Info)</param>
    public static void CaptureMessage(string message, SentryLevel level = SentryLevel.Info)
    {
        if (!_isInitialized) return;

        try
        {
            SentrySdk.CaptureMessage(message, level);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to capture message: {ex.Message}");
        }
    }

    /// <summary>
    /// Capture a structured diagnostic event as a grouped Sentry exception.
    /// Uses a synthetic exception type so we can attach tags/extras reliably
    /// through the existing exception-capture path.
    /// </summary>
    public static void CaptureDiagnosticEvent(
        string message,
        Dictionary<string, object>? extras = null,
        Dictionary<string, string>? tags = null,
        string[]? fingerprint = null,
        string? dedupeKey = null)
    {
        if (!_isInitialized)
        {
            LoggingService.Debug("SentryService: Not initialized, skipping diagnostic capture");
            return;
        }

        if (ShouldSkipDiagnosticCapture(dedupeKey)) return;

        var mergedTags = tags != null
            ? new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        mergedTags["event_type"] = "diagnostic";

        Capture(
            new DiagnosticEventException(message),
            message: message,
            extras: extras,
            tags: mergedTags,
            fingerprint: fingerprint ?? new[] { "diagnostic", message },
            level: SentryLevel.Warning);
    }

    /// <summary>
    /// Capture a no-speech diagnostic as a Sentry transaction, not an Issue.
    /// The transaction keeps the existing diagnostic fields without binding an
    /// exception. Transaction data is sanitized here because <c>beforeSend</c>
    /// only processes error events.
    /// </summary>
    public static void CaptureDiagnosticTransaction(
        string message,
        Dictionary<string, object>? extras = null,
        Dictionary<string, string>? tags = null,
        string[]? fingerprint = null,
        string? dedupeKey = null)
    {
        if (!_isInitialized)
        {
            LoggingService.Debug("SentryService: Not initialized, skipping diagnostic capture");
            return;
        }

        if (ShouldSkipDiagnosticCapture(dedupeKey)) return;

        try
        {
            var transaction = SentrySdk.StartTransaction(message, "diagnostic.no_speech");
            var (preparedTags, preparedData) = PrepareDiagnosticTransactionData(
                message,
                extras,
                tags,
                fingerprint);

            foreach (var (key, value) in preparedTags)
                transaction.SetTag(key, value);

            foreach (var (key, value) in preparedData)
                transaction.SetExtra(key, value);

            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to capture diagnostic transaction: {ex.Message}");
        }
    }

    // internal: test seam for HyperWhisper.SmokeTests.
    internal static (Dictionary<string, string> Tags, Dictionary<string, object?> Data)
        PrepareDiagnosticTransactionData(
            string message,
            Dictionary<string, object>? extras,
            Dictionary<string, string>? tags,
            string[]? fingerprint)
    {
        var preparedTags = tags != null
            ? new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        preparedTags["event_type"] = "diagnostic";

        var preparedData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["diagnostic_message"] = message,
            ["diagnostic_fingerprint"] = fingerprint ?? new[] { "diagnostic", message }
        };

        if (extras != null)
        {
            foreach (var (key, value) in extras)
            {
                preparedData[key] = IsRedactedExtraKey(key)
                    ? "[redacted]"
                    : value;
            }
        }

        return (preparedTags, preparedData);
    }

    private static bool ShouldSkipDiagnosticCapture(string? dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey)) return false;

        lock (_diagnosticLock)
        {
            if (_capturedDiagnosticKeys.Count > 500)
                _capturedDiagnosticKeys.Clear();

            if (_capturedDiagnosticKeys.Add(dedupeKey)) return false;

            LoggingService.Debug($"SentryService: Skipping duplicate diagnostic event: {dedupeKey}");
            return true;
        }
    }

    private sealed class DiagnosticEventException(string message) : Exception(message);

    // =========================================================================
    // TAGS
    // =========================================================================

    /// <summary>
    /// Set a global tag that appears on all subsequent events.
    /// Tags are indexed and searchable in Sentry - use for filterable dimensions.
    /// </summary>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value</param>
    public static void SetTag(string key, string value)
    {
        if (!_isInitialized) return;

        try
        {
            SentrySdk.ConfigureScope(scope => scope.SetTag(key, value));
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to set tag: {ex.Message}");
        }
    }

    // =========================================================================
    // PERFORMANCE SPANS
    // =========================================================================

    /// <summary>
    /// Start a new transaction for a user-facing operation.
    /// Transactions are the top-level performance unit in Sentry.
    /// Use for major operations like "Transcribe Audio" or "Export Diagnostics".
    /// </summary>
    /// <param name="name">Human-readable name (e.g., "Transcribe Audio")</param>
    /// <param name="operation">Category (e.g., "transcription", "ui", "export")</param>
    /// <returns>A transaction that must be finished when the operation completes</returns>
    public static ITransactionTracer? StartTransaction(string name, string operation)
    {
        if (!_isInitialized) return null;

        try
        {
            return SentrySdk.StartTransaction(name, operation);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to start transaction: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Start a child span under the given transaction.
    /// Use for sub-operations within a transaction (e.g., "API Call", "Audio Conversion").
    /// </summary>
    /// <param name="transaction">Parent transaction</param>
    /// <param name="operation">Category (e.g., "http", "file", "process")</param>
    /// <param name="description">Human-readable description (e.g., "POST /transcribe")</param>
    /// <returns>A span that must be finished when the sub-operation completes</returns>
    public static ISpan? StartSpan(ITransactionTracer? transaction, string operation, string description)
    {
        if (!_isInitialized || transaction == null) return null;

        try
        {
            return transaction.StartChild(operation, description);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to start span: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finish a transaction, recording its duration.
    /// </summary>
    /// <param name="transaction">The transaction to finish</param>
    /// <param name="status">Status of the operation (default: Ok)</param>
    public static void FinishTransaction(ITransactionTracer? transaction, SpanStatus status = SpanStatus.Ok)
    {
        if (transaction == null) return;

        try
        {
            transaction.Status = status;
            transaction.Finish();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to finish transaction: {ex.Message}");
        }
    }

    /// <summary>
    /// Finish a span, recording its duration.
    /// </summary>
    /// <param name="span">The span to finish</param>
    /// <param name="status">Status of the operation (default: Ok)</param>
    public static void FinishSpan(ISpan? span, SpanStatus status = SpanStatus.Ok)
    {
        if (span == null) return;

        try
        {
            span.Status = status;
            span.Finish();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"SentryService: Failed to finish span: {ex.Message}");
        }
    }

    /// <summary>
    /// Measure an async operation and record it as a transaction.
    /// Automatically starts and finishes the transaction around the work.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="name">Transaction name</param>
    /// <param name="operation">Operation category</param>
    /// <param name="work">The async work to measure</param>
    /// <returns>The result of the work</returns>
    public static async Task<T> MeasureAsync<T>(
        string name,
        string operation,
        Func<Task<T>> work)
    {
        var transaction = StartTransaction(name, operation);

        try
        {
            var result = await work();
            FinishTransaction(transaction, SpanStatus.Ok);
            return result;
        }
        catch (Exception)
        {
            FinishTransaction(transaction, SpanStatus.InternalError);
            throw;
        }
    }

    /// <summary>
    /// Measure an async operation (void return) and record it as a transaction.
    /// </summary>
    /// <param name="name">Transaction name</param>
    /// <param name="operation">Operation category</param>
    /// <param name="work">The async work to measure</param>
    public static async Task MeasureAsync(
        string name,
        string operation,
        Func<Task> work)
    {
        var transaction = StartTransaction(name, operation);

        try
        {
            await work();
            FinishTransaction(transaction, SpanStatus.Ok);
        }
        catch (Exception)
        {
            FinishTransaction(transaction, SpanStatus.InternalError);
            throw;
        }
    }

    /// <summary>
    /// Measure a synchronous operation and record it as a transaction.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="name">Transaction name</param>
    /// <param name="operation">Operation category</param>
    /// <param name="work">The work to measure</param>
    /// <returns>The result of the work</returns>
    public static T Measure<T>(
        string name,
        string operation,
        Func<T> work)
    {
        var transaction = StartTransaction(name, operation);

        try
        {
            var result = work();
            FinishTransaction(transaction, SpanStatus.Ok);
            return result;
        }
        catch (Exception)
        {
            FinishTransaction(transaction, SpanStatus.InternalError);
            throw;
        }
    }

    /// <summary>
    /// Measure a synchronous operation (void return) and record it as a transaction.
    /// </summary>
    /// <param name="name">Transaction name</param>
    /// <param name="operation">Operation category</param>
    /// <param name="work">The work to measure</param>
    public static void Measure(
        string name,
        string operation,
        Action work)
    {
        var transaction = StartTransaction(name, operation);

        try
        {
            work();
            FinishTransaction(transaction, SpanStatus.Ok);
        }
        catch (Exception)
        {
            FinishTransaction(transaction, SpanStatus.InternalError);
            throw;
        }
    }
}
