using HyperWhisper.Telemetry;

var tests = new (string Name, Action Run)[]
{
    ("blank DSN is a strict no-op", BlankDsnIsNoOp),
    ("invalid DSN is a strict no-op", InvalidDsnIsNoOp),
    ("capture is a no-op before initialization", CaptureBeforeInitializationIsNoOp),
    ("environment DSN is trimmed and preferred", EnvironmentDsnIsPreferred),
    ("configuration matches desktop telemetry defaults", ConfigurationMatchesDefaults),
    ("sensitive telemetry fields are identified", SensitiveFieldsAreIdentified),
    ("exception and context content are sanitized", ExceptionContentIsSanitized),
    ("initialized telemetry captures and flushes", InitializedTelemetryCapturesAndFlushes),
    ("backend failures never escape telemetry", BackendFailuresNeverEscape),
    ("concurrent initialization creates one session", ConcurrentInitializationCreatesOneSession),
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

return;

static void BlankDsnIsNoOp()
{
    var backend = new FakeBackend();
    using var service = new LinuxSentryService(backend);
    Assert.False(service.Initialize("   "));
    Assert.False(service.IsInitialized);
    Assert.Equal(0, backend.InitializeCalls);
    Assert.Equal(0, backend.FlushCalls);
}

static void InvalidDsnIsNoOp()
{
    var backend = new FakeBackend();
    using var service = new LinuxSentryService(backend);
    Assert.False(service.Initialize("not a DSN"));
    Assert.False(service.Initialize("file:///tmp/not-a-sentry-endpoint"));
    Assert.Equal(0, backend.InitializeCalls);
}

static void CaptureBeforeInitializationIsNoOp()
{
    var backend = new FakeBackend();
    using var service = new LinuxSentryService(backend);
    service.Capture(new InvalidOperationException("not sent"), "context");
    Assert.Equal(0, backend.CaptureCalls);
}

static void EnvironmentDsnIsPreferred()
{
    var resolved = TelemetryConfiguration.ResolveDsn(
        name => name == "SENTRY_DSN" ? "  https://public@example.invalid/1  " : null,
        typeof(Program).Assembly);
    Assert.Equal("https://public@example.invalid/1", resolved);
}

static void ConfigurationMatchesDefaults()
{
    var configuration = TelemetryConfiguration.Create(
        "https://public@example.invalid/1",
        "production",
        typeof(Program).Assembly);
    Assert.Equal("production", configuration.Environment);
    Assert.True(configuration.Release.StartsWith("hyperwhisper@", StringComparison.Ordinal));
    Assert.True(configuration.Tags.ContainsKey("linux_version"));
    Assert.True(configuration.Tags.ContainsKey("build_number"));
    Assert.True(configuration.Tags.ContainsKey("architecture"));
    Assert.True(configuration.Tags.ContainsKey("cpu_cores"));
    Assert.Equal(1.0, configuration.TracesSampleRate);
    Assert.Equal(1.0, configuration.ProfilesSampleRate);
    Assert.True(configuration.AutoSessionTracking);
    Assert.True(configuration.SendDefaultPii);
    Assert.True(configuration.AttachStacktrace);
    Assert.Equal(0, configuration.MaxBreadcrumbs);
}

static void SensitiveFieldsAreIdentified()
{
    Assert.True(SentryTelemetryBackend.IsSensitiveExtra("final_transcript"));
    Assert.True(SentryTelemetryBackend.IsSensitiveExtra("selectedText"));
    Assert.True(SentryTelemetryBackend.IsSensitiveExtra("systemPrompt"));
    Assert.False(SentryTelemetryBackend.IsSensitiveExtra("provider"));
}

static void ExceptionContentIsSanitized()
{
    Exception original;
    try
    {
        ThrowSensitiveException();
        throw new InvalidOperationException("unreachable");
    }
    catch (Exception exception)
    {
        original = exception;
    }
    var sanitized = TelemetryPrivacy.SanitizeException(original);
    Assert.False(sanitized.Message.Contains("private", StringComparison.Ordinal));
    Assert.True(sanitized.InnerException is null);
    Assert.Equal(0, sanitized.Data.Count);
    Assert.True(sanitized.StackTrace?.Contains(nameof(ThrowSensitiveException), StringComparison.Ordinal) == true);
    Assert.False(sanitized.StackTrace?.Contains(" in ", StringComparison.Ordinal) == true);
    Assert.Equal("Unhandled UI exception", TelemetryPrivacy.SanitizeContext("Unhandled UI exception"));
    Assert.Equal<string?>(null, TelemetryPrivacy.SanitizeContext("transcript=private words"));
}

static void ThrowSensitiveException()
{
    var inner = new ArgumentException("prompt=private instructions");
    var exception = new InvalidOperationException(
        "transcript=private words audio=/private/ray.wav", inner);
    exception.Data["transcript"] = "private words";
    throw exception;
}

static void InitializedTelemetryCapturesAndFlushes()
{
    var backend = new FakeBackend();
    var service = new LinuxSentryService(backend);
    Assert.True(service.Initialize("https://public@example.invalid/1", "test"));
    Assert.True(service.IsInitialized);
    Assert.Equal(1, backend.InitializeCalls);
    Assert.Equal("test", backend.Configuration?.Environment);

    service.Capture(new InvalidOperationException("failure"), "Unhandled UI exception");
    Assert.Equal(1, backend.CaptureCalls);
    Assert.Equal("Unhandled UI exception", backend.Context);

    service.Dispose();
    Assert.False(service.IsInitialized);
    Assert.Equal(1, backend.FlushCalls);
    Assert.True(backend.SessionDisposed);
}

static void BackendFailuresNeverEscape()
{
    using var failedInitialization = new LinuxSentryService(new ThrowingBackend(throwOnInitialize: true));
    Assert.False(failedInitialization.Initialize("https://public@example.invalid/1"));

    var backend = new ThrowingBackend(throwOnInitialize: false);
    var initialized = new LinuxSentryService(backend);
    Assert.True(initialized.Initialize("https://public@example.invalid/1"));
    initialized.Capture(new InvalidOperationException("failure"));
    initialized.Shutdown();
    Assert.False(initialized.IsInitialized);
}

static void ConcurrentInitializationCreatesOneSession()
{
    var backend = new FakeBackend();
    using var service = new LinuxSentryService(backend);
    using var start = new ManualResetEventSlim(false);
    var calls = Enumerable.Range(0, 8)
        .Select(_ => Task.Run(() =>
        {
            start.Wait();
            return service.Initialize("https://public@example.invalid/1");
        }))
        .ToArray();
    start.Set();
    Task.WaitAll(calls);
    Assert.True(calls.All(call => call.Result));
    Assert.Equal(1, backend.InitializeCalls);
}

sealed class FakeBackend : ITelemetryBackend
{
    public int InitializeCalls { get; private set; }
    public int CaptureCalls { get; private set; }
    public int FlushCalls { get; private set; }
    public bool SessionDisposed { get; private set; }
    public string? Context { get; private set; }
    public TelemetryConfiguration? Configuration { get; private set; }
    public Exception? CapturedException { get; private set; }

    public IDisposable? Initialize(TelemetryConfiguration configuration)
    {
        InitializeCalls++;
        Configuration = configuration;
        return new CallbackDisposable(() => SessionDisposed = true);
    }

    public void Capture(Exception exception, string? context)
    {
        CaptureCalls++;
        Context = context;
        CapturedException = exception;
    }

    public void Flush(TimeSpan timeout) => FlushCalls++;
}

sealed class CallbackDisposable(Action callback) : IDisposable
{
    public void Dispose() => callback();
}

sealed class ThrowingBackend(bool throwOnInitialize) : ITelemetryBackend
{
    public IDisposable? Initialize(TelemetryConfiguration configuration) =>
        throwOnInitialize
            ? throw new InvalidOperationException("initialize")
            : new CallbackDisposable(() => throw new InvalidOperationException("dispose"));

    public void Capture(Exception exception, string? context) =>
        throw new InvalidOperationException("capture");

    public void Flush(TimeSpan timeout) => throw new InvalidOperationException("flush");
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
