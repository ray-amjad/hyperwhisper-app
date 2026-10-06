using System.Reflection;
using System.Runtime.InteropServices;
using Sentry;
using Sentry.Protocol;

namespace HyperWhisper.Telemetry;

/// <summary>
/// Privacy-preserving Linux error telemetry. A blank DSN is a strict no-op so
/// source builds never make a telemetry connection unless the builder opts in.
/// </summary>
public sealed class LinuxSentryService : IDisposable
{
    private readonly ITelemetryBackend _backend;
    private readonly object _gate = new();
    private IDisposable? _session;

    public LinuxSentryService() : this(new SentryTelemetryBackend()) { }

    internal LinuxSentryService(ITelemetryBackend backend) => _backend = backend;

    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public bool Initialize(string? dsn = null, string? environment = null)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                return true;
            }

            var resolvedDsn = dsn ?? TelemetryConfiguration.ResolveDsn();
            if (string.IsNullOrWhiteSpace(resolvedDsn))
            {
                return false;
            }
            if (!Uri.TryCreate(resolvedDsn.Trim(), UriKind.Absolute, out var parsedDsn)
                || (parsedDsn.Scheme != Uri.UriSchemeHttps && parsedDsn.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrWhiteSpace(parsedDsn.Host))
            {
                return false;
            }

            try
            {
                var configuration = TelemetryConfiguration.Create(
                    parsedDsn.ToString(),
                    environment,
                    Assembly.GetEntryAssembly());
                _session = _backend.Initialize(configuration);
                return _session is not null;
            }
            catch
            {
                // Telemetry must never prevent the application from starting.
                _session = null;
                return false;
            }
        }
    }

    public void Capture(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            try
            {
                _backend.Capture(TelemetryPrivacy.SanitizeException(exception), TelemetryPrivacy.SanitizeContext(context));
            }
            catch
            {
                // Reporting an application failure must not cause another one.
            }
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            var session = _session;
            _session = null;
            if (session is null)
            {
                return;
            }

            try
            {
                _backend.Flush(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Shutdown continues even when the SDK cannot flush.
            }

            try
            {
                session.Dispose();
            }
            catch
            {
                // Telemetry shutdown must not prevent application shutdown.
            }
        }
    }

    public void Dispose() => Shutdown();
}

internal static class TelemetryPrivacy
{
    private const int MaxInnerExceptions = 4;
    private const int MaxStackTraceLength = 16_384;

    /// <summary>
    /// Keeps the type names and method frames of an exception and of up to
    /// <see cref="MaxInnerExceptions"/> inner exceptions, and nothing else: no
    /// Message, no Data, no HResult text, at any depth.
    /// </summary>
    /// <remarks>
    /// An <see cref="AggregateException"/> from <c>TaskScheduler.UnobservedTaskException</c>
    /// is never thrown, so it has no stack of its own; the cause and its frames are
    /// only on the inner exceptions (#1051).
    /// </remarks>
    internal static Exception SanitizeException(Exception exception)
    {
        var outerType = TypeName(exception);
        var outerStack = SanitizeStackTrace(exception.StackTrace);

        IReadOnlyList<Exception> inner;
        try
        {
            inner = CollectInnerExceptions(exception);
        }
        catch
        {
            // A hostile InnerException getter costs the inner detail, not the report.
            inner = [];
        }

        var innerParts = inner
            .Select(innerException => new SanitizedPart(TypeName(innerException), SanitizeStackTrace(innerException.StackTrace)))
            .ToList();
        var stack = new List<string>();
        if (outerStack is not null) stack.Add(outerStack);
        foreach (var part in innerParts.Where(part => part.Stack is not null))
        {
            stack.Add($"--- inner {part.Type} ---");
            stack.Add(part.Stack!);
        }

        var stackTrace = stack.Count == 0 ? null : string.Join('\n', stack);
        if (stackTrace is { Length: > MaxStackTraceLength }) stackTrace = stackTrace[..MaxStackTraceLength];
        return new TelemetryReportedException(new SanitizedPart(outerType, outerStack), innerParts, stackTrace);
    }

    /// <summary>One kept exception: its type FullName and its sanitized stack, nothing else.</summary>
    internal sealed record SanitizedPart(string Type, string? Stack);

    private static string TypeName(Exception exception) =>
        exception.GetType().FullName ?? "System.Exception";

    /// <summary>
    /// Breadth-first: an <see cref="AggregateException"/> is flattened and stands
    /// for its inner exceptions (it is a container, so it is not named); any other
    /// exception is named and its <see cref="Exception.InnerException"/> followed.
    /// </summary>
    private static List<Exception> CollectInnerExceptions(Exception root)
    {
        var found = new List<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { root };
        var pending = new Queue<Exception>(Children(root));
        while (pending.Count > 0 && found.Count < MaxInnerExceptions)
        {
            var next = pending.Dequeue();
            if (!seen.Add(next)) continue;
            if (next is not AggregateException) found.Add(next);
            foreach (var child in Children(next)) pending.Enqueue(child);
        }
        return found;

        static IEnumerable<Exception> Children(Exception exception) => exception switch
        {
            AggregateException aggregate => aggregate.Flatten().InnerExceptions,
            { InnerException: { } single } => [single],
            _ => [],
        };
    }

    internal static string? SanitizeStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return null;
        var lines = stackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(128)
            .Select(line =>
            {
                var pathStart = line.IndexOf(" in ", StringComparison.Ordinal);
                return (pathStart >= 0 ? line[..pathStart] : line).TrimEnd('\r');
            });
        var sanitized = string.Join('\n', lines);
        return sanitized.Length <= 16_384 ? sanitized : sanitized[..16_384];
    }

    internal static string? SanitizeContext(string? context) => context switch
    {
        "Unhandled UI exception" => context,
        "Unhandled application exception" => context,
        "Unobserved task exception" => context,
        _ => null,
    };

    internal sealed class TelemetryReportedException(
        SanitizedPart outer,
        IReadOnlyList<SanitizedPart> inner,
        string? sanitizedStack)
        : Exception(inner.Count == 0
            ? $"A {outer.Type} was reported with message, inner-exception, and data content removed."
            : $"A {outer.Type} (inner: {string.Join(", ", inner.Select(part => part.Type))}) was reported with message, inner-exception, and data content removed.")
    {
        public override string? StackTrace => sanitizedStack;

        /// <summary>
        /// The Sentry exception values for this report, built from the sanitized
        /// parts only. The SDK builds frames from the runtime's own stack of a
        /// THROWN exception, so this never-thrown copy would otherwise reach Sentry
        /// with no frames at all (#1051). Innermost first, outermost last, as the
        /// SDK orders a chain.
        /// </summary>
        internal List<SentryException> ToSentryExceptions()
        {
            var values = new List<SentryException>();
            for (var i = inner.Count - 1; i >= 0; i--)
            {
                values.Add(ToSentryException(
                    inner[i],
                    $"A {inner[i].Type} was reported with message and data content removed.",
                    new Mechanism { Type = "chained", Handled = true, ExceptionId = i + 1, ParentId = 0 }));
            }
            values.Add(ToSentryException(
                outer,
                Message,
                new Mechanism { Type = "generic", Handled = true, ExceptionId = 0, IsExceptionGroup = inner.Count > 0 && outer.Type == typeof(AggregateException).FullName }));
            return values;
        }

        private static SentryException ToSentryException(SanitizedPart part, string value, Mechanism mechanism) => new()
        {
            Type = part.Type,
            Value = value,
            Mechanism = mechanism,
            Stacktrace = ParseFrames(part.Stack) is { Count: > 0 } frames ? new SentryStackTrace { Frames = frames } : null,
        };

        /// <summary>
        /// "   at Ns.Type.Method(args)" → Module "Ns.Type", Function "Method(args)".
        /// Any other line (.NET's "--- End of stack trace ---" markers) is dropped.
        /// Sentry wants the oldest frame first; .NET writes the newest first.
        /// </summary>
        private static List<SentryStackFrame> ParseFrames(string? stack)
        {
            var frames = new List<SentryStackFrame>();
            foreach (var raw in (stack ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
                var call = line[3..];
                var paren = call.IndexOf('(');
                var dot = call.LastIndexOf('.', paren < 0 ? call.Length - 1 : paren);
                frames.Add(dot <= 0
                    ? new SentryStackFrame { Function = call }
                    : new SentryStackFrame { Module = call[..dot], Function = call[(dot + 1)..] });
            }
            frames.Reverse();
            return frames;
        }
    }
}

internal sealed record TelemetryConfiguration(
    string Dsn,
    string Environment,
    string Release,
    IReadOnlyDictionary<string, string> Tags,
    double TracesSampleRate,
    double ProfilesSampleRate,
    bool AutoSessionTracking,
    bool SendDefaultPii,
    bool AttachStacktrace,
    int MaxBreadcrumbs)
{
    internal static string ResolveDsn(
        Func<string, string?>? readEnvironment = null,
        Assembly? entryAssembly = null)
    {
        readEnvironment ??= System.Environment.GetEnvironmentVariable;
        var fromEnvironment = readEnvironment("SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return ReadAssemblyDsn(entryAssembly ?? Assembly.GetEntryAssembly())
            ?? ReadAssemblyDsn(typeof(LinuxSentryService).Assembly)
            ?? string.Empty;
    }

    internal static TelemetryConfiguration Create(
        string dsn,
        string? environment,
        Assembly? entryAssembly)
    {
        var assembly = entryAssembly ?? typeof(LinuxSentryService).Assembly;
        var version = assembly.GetName().Version;
        var releaseVersion = version?.ToString(3) ?? "0.0.0";
        var buildNumber = version?.Revision.ToString() ?? "0";

        return new(
            dsn,
            string.IsNullOrWhiteSpace(environment) ? DefaultEnvironment : environment.Trim(),
            $"hyperwhisper@{releaseVersion}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["linux_version"] = System.Environment.OSVersion.VersionString,
                ["build_number"] = buildNumber,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["cpu_cores"] = System.Environment.ProcessorCount.ToString(),
            },
            TracesSampleRate: 1.0,
            ProfilesSampleRate: 1.0,
            AutoSessionTracking: true,
            SendDefaultPii: true,
            AttachStacktrace: true,
            MaxBreadcrumbs: 0);
    }

    private static string DefaultEnvironment =>
#if DEBUG
        "development";
#else
        "production";
#endif

    private static string? ReadAssemblyDsn(Assembly? assembly) => assembly?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SentryDsn")?
        .Value;
}

internal interface ITelemetryBackend
{
    IDisposable? Initialize(TelemetryConfiguration configuration);
    void Capture(Exception exception, string? context);
    void Flush(TimeSpan timeout);
}

internal sealed class SentryTelemetryBackend : ITelemetryBackend
{
    /// <summary>
    /// Whether the privacy filter replaces this extra's value with <c>"[redacted]"</c>.
    /// </summary>
    /// <remarks>
    /// A substring match on the KEY, which errs towards redaction. "path" is the
    /// backstop the Windows head already has (#934): recordings, models and
    /// user-picked media all live under <c>$HOME</c>, so a full path carries the
    /// Linux account name.
    /// </remarks>
    internal static bool IsSensitiveExtra(string key)
    {
        var normalized = key.ToLowerInvariant();
        return normalized.Contains("transcript", StringComparison.Ordinal)
            || normalized.Contains("text", StringComparison.Ordinal)
            || normalized.Contains("prompt", StringComparison.Ordinal)
            || normalized.Contains("path", StringComparison.Ordinal);
    }

    public IDisposable? Initialize(TelemetryConfiguration configuration)
    {
        var session = SentrySdk.Init(options => ConfigureOptions(options, configuration));

        SentrySdk.ConfigureScope(scope =>
        {
            foreach (var tag in configuration.Tags)
            {
                scope.SetTag(tag.Key, tag.Value);
            }
        });
        return session;
    }

    /// <summary>
    /// Every Sentry option the Linux head sets, in one place, so a test can build the
    /// same options production does and drive the real SDK pipeline with them.
    /// </summary>
    internal static void ConfigureOptions(SentryOptions options, TelemetryConfiguration configuration)
    {
        options.Dsn = configuration.Dsn;
        options.Environment = configuration.Environment;
        options.Release = configuration.Release;
        options.TracesSampleRate = configuration.TracesSampleRate;
        options.ProfilesSampleRate = configuration.ProfilesSampleRate;
        options.AutoSessionTracking = configuration.AutoSessionTracking;
        options.SendDefaultPii = configuration.SendDefaultPii;

        // ...but NOT the Linux account name. SendDefaultPii on its own makes the
        // SDK's Enricher stamp Environment.UserName into event.user.username, in an
        // event processor that runs BEFORE beforeSend (sentry-dotnet 4.12.1,
        // SentryClient), so the filter below never sees that field (#942, the Linux
        // half of #932). The IP still goes, so Sentry's per-issue user count holds.
        options.IsEnvironmentUser = false;

        options.AttachStacktrace = configuration.AttachStacktrace;
        options.MaxBreadcrumbs = configuration.MaxBreadcrumbs;
        options.SetBeforeSend((sentryEvent, _) => LinuxSentryEventSanitizer.SanitizeEvent(sentryEvent));

        // The app's own handlers (App.axaml.cs) report these two through
        // LinuxSentryService.Capture, which runs TelemetryPrivacy first. Left on, the
        // SDK's integrations send the SAME fault a second time, raw, past
        // TelemetryPrivacy (#1051: HYPERWHISPER-YX beside its sanitized copy YW).
        options.DisableUnobservedTaskExceptionCapture();
        options.DisableAppDomainUnhandledExceptionCapture();
    }

    public void Capture(Exception exception, string? context)
    {
        // A sanitized report goes as explicit exception values with frames, and with
        // no Exception object for the SDK to re-read (#1051).
        var sentryEvent = exception is TelemetryPrivacy.TelemetryReportedException reported
            ? new SentryEvent { Level = SentryLevel.Error, SentryExceptions = reported.ToSentryExceptions() }
            : new SentryEvent(exception);
        SentrySdk.CaptureEvent(sentryEvent, scope =>
        {
            if (!string.IsNullOrWhiteSpace(context))
            {
                scope.SetExtra("error_message", context);
            }
        });
    }

    public void Flush(TimeSpan timeout) => SentrySdk.Flush(timeout);
}
