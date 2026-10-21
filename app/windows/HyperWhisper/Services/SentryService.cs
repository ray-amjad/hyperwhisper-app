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
public static partial class SentryService
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
                ConfigureSentryOptions(options, dsn, resolvedEnv, $"hyperwhisper@{version}"));

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
    /// Everything <see cref="Initialize(string?, string?)"/> configures on the SDK,
    /// in one place that a test can drive.
    /// </summary>
    /// <remarks>
    /// This is a seam, not a layer. The privacy guarantees of this service live in
    /// TWO places - <c>beforeSend</c>, and the options that decide what the SDK
    /// stamps on an event BEFORE <c>beforeSend</c> ever sees it - and only an event
    /// pushed through the real pipeline proves both are wired. The smoke suite
    /// initializes the SDK with these very options and an in-memory transport, then
    /// reads the envelope it would have sent.
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static void ConfigureSentryOptions(
        SentryOptions options,
        string dsn,
        string? environment,
        string release)
    {
        options.Dsn = dsn;
        options.Environment = environment;
        options.Release = release;

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

        // ...but NOT the Windows account name. SendDefaultPii on its own makes the
        // SDK's own enricher stamp Environment.UserName into event.user.username,
        // and it does that in an event processor that runs BEFORE beforeSend - so
        // the filter below would rewrite the exception title while the very same
        // event still serialized "user":{"username":"<account name>"}. That is the
        // leak HYPERWHISPER-Y5/-YF/-Z1 are about, arriving by a second door.
        //
        // Turning it off does not cost the "N users" count on an issue: SendDefaultPii
        // still sends the IP, and Sentry counts a user by IP when no id or username
        // is set. The diagnosis the issue needs (which release, which HRESULT, which
        // assembly) is untouched.
        options.IsEnvironmentUser = false;

        // PRIVACY SANITIZATION
        // Scrub potentially sensitive data from error events.
        //
        // Note: Breadcrumbs are read-only in C# SDK, but we don't add any
        // with sensitive data, and the beforeSend hook provides extra protection.
        // If needed, breadcrumbs could be disabled entirely via options.MaxBreadcrumbs = 0
        options.SetBeforeSend((sentryEvent, hint) => SanitizeEvent(sentryEvent));

        // Disable breadcrumbs to avoid leaking text content via logs
        // This matches the macOS implementation which strips breadcrumbs before sending
        options.MaxBreadcrumbs = 0;
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

                // Set custom fingerprint for proper grouping.
                //
                // Both branches put caller text into the fingerprint, and the caller's
                // message can hold a path - CaptureDiagnosticEvent passes its own
                // message down here as well. The fingerprint is a field of the event
                // like any other, so SanitizeEvent redacts it in beforeSend rather
                // than each call site doing it; that is also what keeps the SDK's
                // "{{ default }}" directive intact. Do not pre-redact here: this runs
                // on the scope, and doing it twice buys nothing.
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

    /// <summary>
    /// The tags and data of one diagnostic TRANSACTION, sanitized.
    /// </summary>
    /// <remarks>
    /// A transaction never reaches <c>beforeSend</c> - that hook is for error events
    /// only, and <c>SetBeforeSendTransaction</c> is a separate one this service does
    /// not configure - so this method is the whole of the privacy filter on that
    /// path, and <c>TracesSampleRate = 1.0</c> means it runs on 100% of them. Up to
    /// #932 it only ever matched on the KEY, which left the identifier rewrite that
    /// <see cref="SanitizeEvent(SentryEvent)"/> does on the error path absent here.
    /// <c>selected_input_device_name</c> is the field that makes it concrete: it
    /// comes from <c>WaveInCapabilities.ProductName</c>, it is a TAG as well as an
    /// extra, and a Bluetooth endpoint is routinely named after its owner. The same
    /// no-speech event was redacted as an Issue and shipped verbatim as a
    /// transaction.
    /// </remarks>
    // internal: test seam for HyperWhisper.SmokeTests.
    internal static (Dictionary<string, string> Tags, Dictionary<string, object?> Data)
        PrepareDiagnosticTransactionData(
            string message,
            Dictionary<string, object>? extras,
            Dictionary<string, string>? tags,
            string[]? fingerprint)
    {
        // Once for the whole payload, not once per field.
        var rules = BuildLiveRedactionRules();
        var redactedMessage = Redact(message, rules);

        var preparedTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags != null)
        {
            foreach (var (key, value) in tags)
            {
                preparedTags[key] = Redact(value, rules);
            }
        }

        preparedTags["event_type"] = "diagnostic";

        var preparedData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["diagnostic_message"] = redactedMessage,

            // The caller's fingerprint goes through the redactor too. It used to be
            // written straight through - CaptureDiagnosticTransaction(..., fingerprint:
            // new[] { Environment.UserName }) would have shipped the raw account name
            // in a field whose sibling one line up was carefully redacted.
            ["diagnostic_fingerprint"] = fingerprint != null
                ? RedactFingerprint(fingerprint, rules)
                : new[] { "diagnostic", redactedMessage }
        };

        if (extras != null)
        {
            foreach (var (key, value) in extras)
            {
                preparedData[key] = IsRedactedExtraKey(key)
                    ? "[redacted]"
                    : value is string extraText
                        ? Redact(extraText, rules)
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
