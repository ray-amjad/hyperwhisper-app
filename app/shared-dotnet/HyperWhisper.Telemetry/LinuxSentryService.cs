using System.Reflection;
using System.Runtime.InteropServices;
using Sentry;

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
                _backend.Capture(exception, context);
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
    internal static bool IsSensitiveExtra(string key)
    {
        var normalized = key.ToLowerInvariant();
        return normalized.Contains("transcript", StringComparison.Ordinal)
            || normalized.Contains("text", StringComparison.Ordinal)
            || normalized.Contains("prompt", StringComparison.Ordinal);
    }

    public IDisposable? Initialize(TelemetryConfiguration configuration)
    {
        var session = SentrySdk.Init(options =>
        {
            options.Dsn = configuration.Dsn;
            options.Environment = configuration.Environment;
            options.Release = configuration.Release;
            options.TracesSampleRate = configuration.TracesSampleRate;
            options.ProfilesSampleRate = configuration.ProfilesSampleRate;
            options.AutoSessionTracking = configuration.AutoSessionTracking;
            options.SendDefaultPii = configuration.SendDefaultPii;
            options.AttachStacktrace = configuration.AttachStacktrace;
            options.MaxBreadcrumbs = configuration.MaxBreadcrumbs;
            options.SetBeforeSend((sentryEvent, _) =>
            {
                if (sentryEvent.Extra is not null)
                {
                    foreach (var extra in sentryEvent.Extra.ToArray())
                    {
                        if (IsSensitiveExtra(extra.Key))
                        {
                            sentryEvent.SetExtra(extra.Key, "[redacted]");
                        }
                    }
                }

                return sentryEvent;
            });
        });

        SentrySdk.ConfigureScope(scope =>
        {
            foreach (var tag in configuration.Tags)
            {
                scope.SetTag(tag.Key, tag.Value);
            }
        });
        return session;
    }

    public void Capture(Exception exception, string? context)
    {
        var sentryEvent = new SentryEvent(exception);
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
