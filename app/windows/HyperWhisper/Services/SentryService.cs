using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    /// The <c>beforeSend</c> body: drops denied extras and rewrites the signed-in
    /// user's Windows identifiers out of every field of an error event that can
    /// carry them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fields, and why each one is here:
    /// <list type="bullet">
    /// <item><c>Extra</c> - the only field the filter covered before #932, and it
    /// matched on the KEY, which is why the exception message went out raw.</item>
    /// <item><c>Tags</c> - <c>selected_input_device_name</c> is a tag as well as an
    /// extra, and a Bluetooth endpoint is routinely named after its owner
    /// ("Bob's AirPods").</item>
    /// <item><c>SentryExceptions[].Value</c> - the Sentry issue TITLE, and the whole
    /// of HYPERWHISPER-Y5 / -YF / -Z1.</item>
    /// <item><c>Message</c> - <c>CaptureMessage</c> events. <c>Capture()</c> puts the
    /// caller's text in the <c>error_message</c> EXTRA instead, so Message is null
    /// on the events in this issue.</item>
    /// <item><c>ServerName</c> - the machine name, which <c>SendDefaultPii</c> adds.
    /// It is NOT deleted: a machine name is not an account name and it is the only
    /// way to tell two devices apart inside one issue. But Windows offers the
    /// account name as the default computer name, so "RAY-DESKTOP-PC" is a real
    /// shape, and the account name comes out of it like anywhere else.</item>
    /// <item><c>DebugImages[].CodeFile</c> / <c>.DebugFile</c> - the second door onto
    /// the very path HYPERWHISPER-Y5 / -YF / -Z1 are about. <c>AttachStacktrace</c>
    /// is on by design, so the SDK's own <c>DebugStackTrace</c> adds one debug image
    /// per managed module with <c>CodeFile = module.FullyQualifiedName</c>, and the
    /// installer is per-user
    /// (<c>setup-x64.iss</c>: <c>%LOCALAPPDATA%\Programs\HyperWhisper</c>). Those
    /// images are merged onto the event BEFORE <c>beforeSend</c> runs, so a clean
    /// title shipped in the same envelope as
    /// <c>"code_file":"C:\Users\bob\AppData\Local\Programs\..."</c>. They are
    /// REDACTED rather than dropped: the module file name is what makes a stack
    /// frame symbolicate, and #932 explicitly keeps the assembly simple name.</item>
    /// <item><c>Fingerprint</c> - <see cref="Capture"/>'s default fingerprint is
    /// <c>["{{ default }}", message, errorType]</c> and the caller's message can hold
    /// a path, so a redacted <c>value</c> rode beside a raw <c>fingerprint</c>. The
    /// literal <c>{{ default }}</c> directive is left alone - it is Sentry's grouping
    /// instruction, not data - and everything else goes through the redactor. That
    /// MERGES what used to be one Sentry group per account name into one group per
    /// fault, which is the grouping the fingerprint was always meant to express; it
    /// cannot split a group, because the redactor is a function of the text alone
    /// and two events that fingerprinted alike still do.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The entries are mutated IN PLACE. <c>SentryExceptions</c> hands back the
    /// event's own <see cref="SentryException"/> objects, so writing the collection
    /// back would be a no-op on an event that has one - and NOT a no-op on an event
    /// that has none, where it replaces a null backing field with an empty one and
    /// adds an <c>"exception":{"values":[]}</c> key the event did not have. Same for
    /// <c>Message</c>, whose write-back was a self-assignment. The smoke suite pushes
    /// a real exception and a real message event through the SDK and asserts both.
    /// </para>
    /// <para>
    /// Every step is total: <see cref="Redact"/> never throws for any input, and
    /// nothing here indexes or parses. The outer <c>try</c> is a backstop for the
    /// step after this one, and it returns <c>null</c> - it DROPS the event - rather
    /// than returning what it was handed. That is not the conservative choice it
    /// looks like. In sentry-dotnet 4.12.1 the <c>beforeSend</c> invocation
    /// (<c>SentryClient.BeforeSendInternal</c>) already sits inside a <c>try</c>: a
    /// throw aborts the assignment that would have taken the sanitized result, the
    /// <c>catch</c> logs, and the method falls through to <c>return @event</c> - the
    /// ORIGINAL, un-sanitized event. So letting a throw escape publishes
    /// <c>C:\Users\bob\...</c>, and with <c>MaxBreadcrumbs = 0</c> even the SDK's own
    /// "BeforeSend callback failed" breadcrumb is dropped, so it would be silent.
    /// Losing one event is the cheaper failure. The identifiers are read ONCE per
    /// event rather than once per field.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static SentryEvent? SanitizeEvent(SentryEvent sentryEvent)
        => SanitizeEventGuarded(sentryEvent, static () => BuildLiveRedactionRules());

    /// <summary>
    /// The <see cref="SanitizeEvent(SentryEvent)"/> seam: the same sanitization with
    /// the three identifiers supplied instead of read from the live environment.
    /// </summary>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static SentryEvent? SanitizeEvent(
        SentryEvent sentryEvent,
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
        => SanitizeEventGuarded(
            sentryEvent,
            () => BuildRedactionRules(userProfileDirectory, localAppDataDirectory, userName));

    /// <summary>
    /// <see cref="SanitizeEvent(SentryEvent, IReadOnlyList{RedactionRule})"/>, with a
    /// fault turned into a dropped event instead of an un-sanitized one.
    /// </summary>
    private static SentryEvent? SanitizeEventGuarded(
        SentryEvent sentryEvent,
        Func<IReadOnlyList<RedactionRule>> buildRules)
    {
        try
        {
            return SanitizeEvent(sentryEvent, buildRules());
        }
        catch (Exception ex)
        {
            // The exception's own text can hold the identifier this method exists to
            // remove, and this log line stays on the user's machine - but the type
            // name is all a maintainer needs to find the fault, so only that is
            // written. The log call itself is guarded: nothing in this catch may
            // escape, or the SDK ships the raw event.
            try
            {
                LoggingService.Debug(
                    $"SentryService: beforeSend sanitization failed ({ex.GetType().Name}); dropping the event");
            }
            catch
            {
                // Deliberately empty - see above.
            }

            return null;
        }
    }

    private static SentryEvent SanitizeEvent(SentryEvent sentryEvent, IReadOnlyList<RedactionRule> rules)
    {
        // Drop any suspicious extras (transcript, text, prompt), and rewrite the
        // user's Windows identifiers out of every extra that survives. The redacted
        // branch is never re-examined, which is what makes the "not already
        // [redacted]" rule structural rather than a second check.
        if (sentryEvent.Extra != null)
        {
            var sanitizedExtras = new Dictionary<string, object?>();
            foreach (var kvp in sentryEvent.Extra)
            {
                sanitizedExtras[kvp.Key] = IsRedactedExtraKey(kvp.Key)
                    ? "[redacted]"
                    : kvp.Value is string extraText
                        ? Redact(extraText, rules)
                        : kvp.Value;
            }

            // Buffered first: SetExtra writes into the dictionary being enumerated.
            foreach (var kvp in sanitizedExtras)
            {
                sentryEvent.SetExtra(kvp.Key, kvp.Value);
            }
        }

        var sanitizedTags = new Dictionary<string, string>();
        foreach (var kvp in sentryEvent.Tags)
        {
            sanitizedTags[kvp.Key] = Redact(kvp.Value, rules);
        }

        foreach (var kvp in sanitizedTags)
        {
            sentryEvent.SetTag(kvp.Key, kvp.Value);
        }

        foreach (var sentryException in sentryEvent.SentryExceptions)
        {
            if (sentryException.Value != null)
            {
                sentryException.Value = Redact(sentryException.Value, rules);
            }
        }

        var message = sentryEvent.Message;
        if (message != null)
        {
            // Each property is rewritten only when it is non-null, so a null
            // Formatted stays null rather than becoming "".
            if (message.Formatted != null)
            {
                message.Formatted = Redact(message.Formatted, rules);
            }

            if (message.Message != null)
            {
                message.Message = Redact(message.Message, rules);
            }
        }

        if (sentryEvent.ServerName != null)
        {
            sentryEvent.ServerName = Redact(sentryEvent.ServerName, rules);
        }

        // Mutated in place, like the exception values above: the list and the images
        // in it belong to the event, and the SDK has already merged its own images
        // onto it. Null is left null - reading the property does not create the list,
        // and writing an empty one would add a "debug_meta" key the event never had.
        if (sentryEvent.DebugImages != null)
        {
            foreach (var debugImage in sentryEvent.DebugImages)
            {
                if (debugImage == null)
                {
                    continue;
                }

                if (debugImage.CodeFile != null)
                {
                    debugImage.CodeFile = Redact(debugImage.CodeFile, rules);
                }

                if (debugImage.DebugFile != null)
                {
                    debugImage.DebugFile = Redact(debugImage.DebugFile, rules);
                }
            }
        }

        // Count is read rather than a null check: Fingerprint is never null in 4.12.1.
        // The guard saves an allocation on the events that have no fingerprint, which
        // is most of them; it is NOT load-bearing for behaviour, because the
        // serializer omits an empty array either way. The DebugImages guard above IS
        // load-bearing - reading that property does not create the list, and writing
        // one would add a "debug_meta" key the SDK never wrote.
        if (sentryEvent.Fingerprint.Count > 0)
        {
            sentryEvent.Fingerprint = RedactFingerprint(sentryEvent.Fingerprint, rules);
        }

        return sentryEvent;
    }

    /// <summary>
    /// One fingerprint, redacted part by part, with Sentry's grouping directive left
    /// alone.
    /// </summary>
    /// <remarks>
    /// <c>{{ default }}</c> tells Sentry to fold its own grouping in beside these
    /// parts. It is an instruction, not data, and an account named <c>default</c>
    /// would otherwise turn it into <c>{{ %USER% }}</c> - a string Sentry does not
    /// recognise - and silently regroup every issue for that one user.
    /// </remarks>
    private static string[] RedactFingerprint(
        IReadOnlyList<string> fingerprint,
        IReadOnlyList<RedactionRule> rules)
    {
        var redacted = new string[fingerprint.Count];
        for (var i = 0; i < fingerprint.Count; i++)
        {
            var part = fingerprint[i];
            redacted[i] = part == null || part == SentryDefaultFingerprintDirective
                ? part!
                : Redact(part, rules);
        }

        return redacted;
    }

    /// <summary>
    /// Sentry's own grouping directive, which must survive redaction verbatim.
    /// </summary>
    private const string SentryDefaultFingerprintDirective = "{{ default }}";

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
    /// This is ONE left-to-right scan over the input, not three passes of
    /// <c>string.Replace</c>. Three passes are wrong in two ways that a fourth pass
    /// cannot fix. Pass N+1 reads what pass N wrote, so an account named <c>User</c>
    /// - Microsoft's own default on a Windows dev VM image - turned the
    /// <c>%USERPROFILE%</c> token that had just removed it back into
    /// <c>%%USER%PROFILE%</c>, re-encoding the identifier the pass before had
    /// removed; <c>App</c> and <c>Data</c> did the same to <c>\AppData\</c>. And a
    /// substring match for the bare name has no idea where a word ends, so account
    /// <c>ed</c> shredded <c>HyperWhisper.Shar%USER%Core.dll</c> and account
    /// <c>c</c> shredded <c>0x800711%USER%7</c> - the two things this method exists
    /// to keep. The scan fixes both by construction: it only ever moves FORWARD over
    /// the input, so no rule can match a token this method itself emitted, and the
    /// bare name is matched only between boundaries (below).
    /// </para>
    /// <para>
    /// At a given position the LONGEST identifier wins, which is why
    /// <c>BuildRedactionRules</c> sorts. Local-app-data normally sits inside the
    /// profile, so a path under it reads <c>%LOCALAPPDATA%\...</c> rather than
    /// <c>%USERPROFILE%\AppData\Local\...</c> - the more specific answer, and the one
    /// that makes the two rules tell each other apart in a test. Neither rule is dead
    /// code: the profile rule is the only one that catches
    /// <c>C:\Users\bob\Documents\...</c>, and the local-app-data rule is the only one
    /// that catches a local-app-data directory redirected to another drive. That
    /// directory is read from the environment rather than from the special-folder
    /// API, because only <c>AppPaths</c> may read that special folder (see
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
    /// Over-redaction is still possible and still accepted: an account named
    /// <c>System</c> turns a standalone occurrence of that word into <c>%USER%</c>.
    /// That is the safe direction for a privacy filter and it is how
    /// <see cref="IsRedactedExtraKey"/> already errs. The method is total - it never
    /// throws, for any input - because it runs inside <c>beforeSend</c>, and a throw
    /// there does NOT cost the event: sentry-dotnet 4.12.1 catches it and sends the
    /// event it was handed, un-sanitized. See
    /// <see cref="SanitizeEventGuarded"/>, which turns that into a dropped event.
    /// </para>
    /// </remarks>
    // internal (not private): test seam for HyperWhisper.SmokeTests via
    // InternalsVisibleTo (see HyperWhisper.csproj) - no other accessibility
    // change is intended.
    internal static string RedactUserIdentifiers(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Redact(value, BuildLiveRedactionRules());
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
            return string.Empty;
        }

        return Redact(
            value,
            BuildRedactionRules(userProfileDirectory, localAppDataDirectory, userName));
    }

    /// <summary>
    /// What has to sit around a run of text for it to count as this identifier.
    /// </summary>
    private enum MatchBoundary
    {
        /// <summary>
        /// A directory: the match has to END a path segment, so the character after
        /// it must be a separator or the end of the string. Without that,
        /// <c>C:\Users\bob\AppData\Local</c> matched inside
        /// <c>...\AppData\LocalLow\NVIDIA\x.log</c> (a standard Windows sibling) and
        /// produced <c>%LOCALAPPDATA%Low\NVIDIA\x.log</c>, and profile
        /// <c>C:\Users\bob</c> rewrote a SECOND account's <c>C:\Users\bobby\...</c>
        /// to <c>%USERPROFILE%by\...</c> - a fragment of someone else's name,
        /// attributed to the reporter. Neither is "over-redaction"; both mangle the
        /// diagnosis, and the second one leaks.
        /// </summary>
        DirectorySegment,

        /// <summary>
        /// A bare account name: neither neighbour may be a letter or a digit.
        /// </summary>
        WholeWord
    }

    /// <summary>
    /// One identifier, the token that replaces it, and what has to bound a match.
    /// </summary>
    private readonly record struct RedactionRule(string Identifier, string Token, MatchBoundary Boundary);

    /// <summary>
    /// The three identifiers of the signed-in user, read from the process.
    /// </summary>
    /// <remarks>
    /// Read ONCE per event rather than once per field: <c>SanitizeEvent</c> touches
    /// about eleven strings on a typical diagnostic event, and <c>Environment.UserName</c>
    /// P/Invokes <c>GetUserNameExW</c> with no BCL cache. Not cached ACROSS events -
    /// a static cache would need a memory barrier for a value that costs microseconds.
    /// </remarks>
    private static IReadOnlyList<RedactionRule> BuildLiveRedactionRules()
    {
        return BuildRedactionRules(
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
    /// Turns the three identifiers into the rules <see cref="Redact"/> scans with,
    /// longest identifier first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every guard that decides whether an identifier is usable lives here, once, and
    /// this is the ONLY producer of rules: a blank identifier is dropped - both the
    /// directories and the account name go through <c>IsNullOrWhiteSpace</c> - a
    /// directory loses a trailing separator, and a bare drive root (<c>C:\</c>, three
    /// characters or fewer once trimmed) is dropped because replacing it would mangle
    /// every path in the message for no privacy gain. Because the blank guards are
    /// here, <see cref="MatchRule"/> can never be handed a zero-length identifier and
    /// carries no guard of its own for one.
    /// </para>
    /// <para>
    /// Both kinds of rule are bounded, in different ways - see
    /// <see cref="MatchBoundary"/>. A directory has to end a path segment; a bare
    /// account name has to be a whole word.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RedactionRule> BuildRedactionRules(
        string? userProfileDirectory,
        string? localAppDataDirectory,
        string? userName)
    {
        var rules = new List<RedactionRule>(3);

        AddDirectory(userProfileDirectory, "%USERPROFILE%");
        AddDirectory(localAppDataDirectory, "%LOCALAPPDATA%");

        if (!string.IsNullOrWhiteSpace(userName))
        {
            rules.Add(new RedactionRule(userName, "%USER%", MatchBoundary.WholeWord));
        }

        // OrderByDescending is stable, so two identifiers of the same length keep
        // the order they were added in.
        return rules.OrderByDescending(rule => rule.Identifier.Length).ToList();

        void AddDirectory(string? directory, string token)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var trimmed = directory.TrimEnd('\\', '/');

            // "C:", "C:\", "D:/" - a drive root, and nothing else is this short.
            if (trimmed.Length <= 3 || string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            rules.Add(new RedactionRule(trimmed, token, MatchBoundary.DirectorySegment));
        }
    }

    /// <summary>
    /// The single forward scan. Emitted tokens are never re-read.
    /// </summary>
    private static string Redact(string value, IReadOnlyList<RedactionRule> rules)
    {
        if (string.IsNullOrEmpty(value) || rules.Count == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var matchLength = MatchRule(value, index, rules, out var token);
            if (matchLength > 0)
            {
                // Appended, never re-examined. The cursor only moves forward over
                // the INPUT, so no rule can match text this method emitted - which
                // is what stops an account named "User" from rewriting the
                // "%USERPROFILE%" that had just removed it.
                builder.Append(token);
                index += matchLength;
                continue;
            }

            builder.Append(value[index]);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The rule that matches at <paramref name="index"/>, or a length of 0.
    /// </summary>
    private static int MatchRule(
        string value,
        int index,
        IReadOnlyList<RedactionRule> rules,
        out string token)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var length = rule.Identifier.Length;

            // No zero-length guard here on purpose: BuildRedactionRules is the only
            // producer of rules and it drops every blank identifier, so one cannot
            // reach this loop - a guard for it would be unreachable code that no test
            // could kill. The scan terminates even if one ever did, because Redact
            // advances the cursor whenever the returned length is not positive.
            if (index + length > value.Length)
            {
                continue;
            }

            if (string.Compare(value, index, rule.Identifier, 0, length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            if (!IsBoundedMatch(value, index, length, rule.Boundary))
            {
                continue;
            }

            token = rule.Token;
            return length;
        }

        token = string.Empty;
        return 0;
    }

    /// <summary>
    /// Whether the run at <paramref name="index"/> is bounded the way its rule needs.
    /// </summary>
    private static bool IsBoundedMatch(string value, int index, int length, MatchBoundary boundary)
        => boundary == MatchBoundary.DirectorySegment
            ? index + length == value.Length || IsDirectorySeparator(value[index + length])
            : (index == 0 || IsAccountNameBoundary(value[index - 1]))
                && (index + length == value.Length || IsAccountNameBoundary(value[index + length]));

    private static bool IsDirectorySeparator(char character)
        => character == '\\' || character == '/';

    /// <summary>
    /// A character a bare account name cannot run across: anything that is not a
    /// letter and not a digit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule, and it is the third one tried. A bare substring match
    /// shredded the diagnosis (account <c>ed</c> turned
    /// <c>HyperWhisper.SharedCore.dll</c> into <c>HyperWhisper.Shar%USER%Core.dll</c>).
    /// A delimiter-INCLUSION set - only the characters Windows forbids in an account
    /// name, plus whitespace, the apostrophes, <c>-</c> and <c>_</c> - kept the
    /// diagnosis but left the leak wide open, because it excluded <c>.</c>, <c>@</c>
    /// and the parentheses: account <c>bob</c> survived in
    /// <c>'C:\Users\bob\Documents\bob.docx'</c>, in <c>Bob.AirPods</c>, in
    /// <c>bob@corp.com</c> and in <c>Headset (Bob's AirPods)</c> - and the first of
    /// those is the exact shape of the Sentry issue TITLE that #932 exists to close.
    /// </para>
    /// <para>
    /// Excluding only letters and digits keeps BOTH properties, because the strings
    /// #932 must preserve are dotted tokens whose parts sit between letters and
    /// digits, not at their edges. Traced, for the message in the issue:
    /// <list type="bullet">
    /// <item>account <c>ed</c> in <c>SharedCore</c> - neighbours <c>r</c> and
    /// <c>C</c>, both alphanumeric, so no match; the assembly name survives.</item>
    /// <item>account <c>c</c> in <c>0x800711C7</c> - neighbours <c>1</c> and
    /// <c>7</c>, so no match; the HRESULT survives.</item>
    /// <item>account <c>bob</c> in <c>\bob.docx</c> - neighbours <c>\</c> and
    /// <c>.</c>, so it MATCHES; the leak closes.</item>
    /// <item>account <c>Bob</c> in <c>(Bob's AirPods)</c> - neighbours <c>(</c> and
    /// <c>'</c>, so it MATCHES; the leak closes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The residual cost is exact, and it is over-redaction rather than a leak: an
    /// account named <c>dll</c>, <c>SharedCore</c> or <c>0x800711C7</c> - all legal
    /// Windows account names - loses that token out of its own crash report, because
    /// <c>.dll'</c> and <c>(0x800711C7)</c> ARE whole words by this rule. That is the
    /// accepted direction for this filter, the same direction
    /// <see cref="IsRedactedExtraKey"/> already errs in, and a lost file extension is
    /// cheaper than a published account name.
    /// </para>
    /// <para>
    /// <see cref="char.IsLetterOrDigit(char)"/> rather than an ASCII range, so a CJK
    /// or Cyrillic account name is bounded by the same rule as a Latin one.
    /// </para>
    /// </remarks>
    private static bool IsAccountNameBoundary(char character)
        => !char.IsLetterOrDigit(character);

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
