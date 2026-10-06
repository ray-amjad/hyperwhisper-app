using System.Text.Json.Nodes;
using HyperWhisper.Telemetry;
using Sentry;
using Sentry.Protocol;

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
    ("Sentry options never stamp the Linux account name as user.username", OptionsDoNotStampEnvironmentUser),
    ("account-name redaction keeps the diagnosis and closes the leak", RedactionKeepsDiagnosisAndClosesLeak),
    ("beforeSend sanitizer rewrites every field that can carry the account name", SanitizerRewritesEveryField),
    ("beforeSend sanitizer keeps the grouping directive, adds no key, drops an event it cannot sanitize", SanitizerKeepsShapeAndDropsOnFault),
    ("the configured beforeSend keeps the account name out of the envelope", ConfiguredBeforeSendKeepsAccountNameOutOfEnvelope),
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
    Assert.True(SentryTelemetryBackend.IsSensitiveExtra("audio_path"));
    Assert.True(SentryTelemetryBackend.IsSensitiveExtra("modelPath"));
    Assert.False(SentryTelemetryBackend.IsSensitiveExtra("provider"));
}

static TelemetryConfiguration TestConfiguration() => TelemetryConfiguration.Create(
    "https://public@example.invalid/1",
    "test",
    typeof(Program).Assembly);

static void OptionsDoNotStampEnvironmentUser()
{
    // #942: SendDefaultPii with IsEnvironmentUser left at its default makes the
    // SDK's Enricher write Environment.UserName into user.username BEFORE
    // beforeSend runs, so no filter can strip it afterwards.
    var options = new SentryOptions();
    SentryTelemetryBackend.ConfigureOptions(options, TestConfiguration());
    Assert.True(options.SendDefaultPii);
    Assert.Equal(false, options.IsEnvironmentUser);
}

static string Redact(string value, string? userName = "bob") =>
    LinuxSentryEventSanitizer.RedactUserIdentifiers(
        value,
        "/home/bob",
        new Dictionary<string, string?> { ["XDG_DATA_HOME"] = "/home/bob/.local/share", ["XDG_CACHE_HOME"] = "/srv/cache/bob/" },
        userName);

static void RedactionKeepsDiagnosisAndClosesLeak()
{
    // Directory before bare name, longest directory first.
    Assert.Equal("$HOME/Music/x.wav", Redact("/home/bob/Music/x.wav"));
    Assert.Equal("$XDG_DATA_HOME/HyperWhisper/models/ggml-base.bin", Redact("/home/bob/.local/share/HyperWhisper/models/ggml-base.bin"));
    // An XDG directory redirected outside $HOME is its own rule; trailing '/' trimmed.
    Assert.Equal("$XDG_CACHE_HOME/hw", Redact("/srv/cache/bob/hw"));
    Assert.Equal("$HOME", Redact("/home/bob"));
    // A directory must end a segment: a second account is not half-rewritten.
    Assert.Equal("/home/bobby/x", Redact("/home/bobby/x"));
    // Bare name: both neighbours must be non-alphanumeric.
    Assert.Equal("$USER.wav and $USER@corp.com", Redact("bob.wav and bob@corp.com"));
    Assert.Equal("Headset ($USER's AirPods)", Redact("Headset (Bob's AirPods)"));
    Assert.Equal("$USER-laptop", Redact("bob-laptop"));
    Assert.Equal("bobsled team", Redact("bobsled team"));
    Assert.Equal("HyperWhisper.SharedCore.dll (0x800711C7)", Redact("HyperWhisper.SharedCore.dll (0x800711C7)", "ed"));
    Assert.Equal("HyperWhisper.SharedCore.dll (0x800711C7)", Redact("HyperWhisper.SharedCore.dll (0x800711C7)", "c"));
    // Emitted tokens are never re-read: an account named "home" stays one token.
    Assert.Equal("$HOME/x", LinuxSentryEventSanitizer.RedactUserIdentifiers("/home/home/x", "/home/home", null, "home"));
    // Root "/" and blanks are not rules.
    Assert.Equal("/usr/lib/x.so", LinuxSentryEventSanitizer.RedactUserIdentifiers("/usr/lib/x.so", "/", null, " "));
    Assert.Equal(string.Empty, LinuxSentryEventSanitizer.RedactUserIdentifiers(null, "/home/bob", null, "bob"));
}

static SentryEvent? SanitizeAsBob(SentryEvent sentryEvent) =>
    LinuxSentryEventSanitizer.SanitizeEvent(
        sentryEvent,
        "/home/bob",
        new Dictionary<string, string?> { ["XDG_DATA_HOME"] = "/home/bob/.local/share" },
        "bob");

static void SanitizerRewritesEveryField()
{
    var sentryEvent = new SentryEvent
    {
        Message = Fixtures.LeakyLoadFailure,
        ServerName = "bob-laptop",
        SentryExceptions = [new SentryException { Type = "System.IO.IOException", Value = Fixtures.LeakyLoadFailure }],
        DebugImages =
        [
            new DebugImage
            {
                Type = "pe_dotnet",
                CodeFile = "/home/bob/.local/share/HyperWhisper/app/HyperWhisper.Linux.dll",
                DebugFile = "/home/bob/.local/share/HyperWhisper/app/HyperWhisper.Linux.pdb",
                DebugId = "a13b911b-469d-47a0-8fd2-407b06d2a12d-c0b21b72",
            },
        ],
        Fingerprint = ["{{ default }}", Fixtures.LeakyLoadFailure, "IOException"],
    };
    sentryEvent.SetExtra("error_message", Fixtures.LeakyLoadFailure);
    sentryEvent.SetExtra("model_path", "/home/bob/x.bin");
    sentryEvent.SetExtra("size_bytes", 48128);
    sentryEvent.SetTag("input_device", "Bob's AirPods");
    sentryEvent.SetTag("component", "transcription");

    var sanitized = SanitizeAsBob(sentryEvent);

    Assert.True(ReferenceEquals(sanitized, sentryEvent));
    var exception = sentryEvent.SentryExceptions!.Single();
    Assert.Equal(Fixtures.RedactedLoadFailure, exception.Value);
    Assert.Equal("System.IO.IOException", exception.Type);
    Assert.Equal(Fixtures.RedactedLoadFailure, sentryEvent.Message!.Message);
    Assert.Equal("$USER-laptop", sentryEvent.ServerName);
    Assert.Equal("$XDG_DATA_HOME/HyperWhisper/app/HyperWhisper.Linux.dll", sentryEvent.DebugImages!.Single().CodeFile);
    Assert.Equal("$XDG_DATA_HOME/HyperWhisper/app/HyperWhisper.Linux.pdb", sentryEvent.DebugImages!.Single().DebugFile);
    Assert.Equal("a13b911b-469d-47a0-8fd2-407b06d2a12d-c0b21b72", sentryEvent.DebugImages!.Single().DebugId);
    Assert.Equal(3, sentryEvent.Fingerprint.Count);
    Assert.Equal("{{ default }}", sentryEvent.Fingerprint[0]);
    Assert.Equal(Fixtures.RedactedLoadFailure, sentryEvent.Fingerprint[1]);
    Assert.Equal("IOException", sentryEvent.Fingerprint[2]);
    Assert.Equal<object?>(Fixtures.RedactedLoadFailure, sentryEvent.Extra["error_message"]);
    Assert.Equal<object?>("[redacted]", sentryEvent.Extra["model_path"]);
    Assert.Equal<object?>(48128, sentryEvent.Extra["size_bytes"]);
    Assert.Equal("$USER's AirPods", sentryEvent.Tags["input_device"]);
    Assert.Equal("transcription", sentryEvent.Tags["component"]);

    var formatted = new SentryEvent { Message = new SentryMessage { Formatted = Fixtures.LeakyLoadFailure } };
    SanitizeAsBob(formatted);
    Assert.Equal(Fixtures.RedactedLoadFailure, formatted.Message!.Formatted);
    Assert.Equal<string?>(null, formatted.Message!.Message);
}

static void SanitizerKeepsShapeAndDropsOnFault()
{
    var directive = new SentryEvent { Fingerprint = ["{{ default }}", "/home/default/x.so"] };
    LinuxSentryEventSanitizer.SanitizeEvent(directive, "/home/default", null, "default");
    Assert.Equal("{{ default }}", directive.Fingerprint[0]);
    Assert.Equal("$HOME/x.so", directive.Fingerprint[1]);

    var bare = new SentryEvent();
    SanitizeAsBob(bare);
    Assert.Equal(0, bare.Fingerprint.Count);
    Assert.True(bare.DebugImages is null);
    Assert.True(bare.SentryExceptions?.Any() != true);

    // sentry-dotnet sends the ORIGINAL event when beforeSend throws, so a fault
    // must drop the event instead.
    var unsanitizable = new SentryEvent { SentryExceptions = [null!] };
    Assert.True(SanitizeAsBob(unsanitizable) is null);
}

static void ConfiguredBeforeSendKeepsAccountNameOutOfEnvelope()
{
    // The REAL pipeline: production options, an in-memory transport in place of
    // the network. The raw exception is captured through SentrySdk directly, which
    // is what the SDK's own unhandled-exception integrations do; they never pass
    // through LinuxSentryService.Capture's SanitizeException. Built from THIS
    // machine's identifiers, so it is the same case on CI and on a dev box.
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var userName = Environment.UserName;
    var raw = $"Could not open '{home}/Music/take 1.wav' for {userName}: Permission denied (errno 13, 0x80131620)";
    var expected = LinuxSentryEventSanitizer.RedactUserIdentifiers(raw);
    Assert.True(expected != raw);

    var plantedCodeFile = $"{home}/.local/share/HyperWhisper/app/HyperWhisper.Linux.dll";
    var transport = new CapturingSentryTransport();
    using (SentrySdk.Init(options =>
    {
        SentryTelemetryBackend.ConfigureOptions(options, TestConfiguration());
        // The only departures from production, none of them privacy-related:
        // nothing leaves the machine, no profiler, no session envelope.
        options.Transport = transport;
        options.ProfilesSampleRate = 0;
        options.AutoSessionTracking = false;
    }))
    {
        var sentryEvent = new SentryEvent(new IOException(raw))
        {
            DebugImages = [new DebugImage { Type = "pe_dotnet", CodeFile = plantedCodeFile }],
        };
        SentrySdk.CaptureEvent(sentryEvent, scope => scope.SetFingerprint(["{{ default }}", raw]));
        SentrySdk.CaptureMessage(raw);
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    var errorEvent = transport.FindPayload(payload => payload["exception"] is not null)
        ?? throw new InvalidOperationException("no error event: " + transport.Dump());
    var exception = errorEvent["exception"]?["values"]?[0];
    Assert.Equal(expected, exception?["value"]?.GetValue<string>());
    Assert.Equal("System.IO.IOException", exception?["type"]?.GetValue<string>());
    Assert.True(expected.Contains("errno 13, 0x80131620", StringComparison.Ordinal));
    Assert.Equal<string?>(null, errorEvent["user"]?["username"]?.GetValue<string>());
    Assert.Equal(
        LinuxSentryEventSanitizer.RedactUserIdentifiers(Environment.MachineName),
        errorEvent["server_name"]?.GetValue<string>());
    Assert.Equal(expected, errorEvent["fingerprint"]?[1]?.GetValue<string>());
    var plantedSeen = false;
    foreach (var image in errorEvent["debug_meta"]?["images"]?.AsArray() ?? [])
    {
        foreach (var key in new[] { "code_file", "debug_file" })
        {
            var path = image?[key]?.GetValue<string>();
            if (path is not null) Assert.Equal(LinuxSentryEventSanitizer.RedactUserIdentifiers(path), path);
        }
        plantedSeen |= image?["code_file"]?.GetValue<string>() == LinuxSentryEventSanitizer.RedactUserIdentifiers(plantedCodeFile);
    }
    Assert.True(plantedSeen);

    var messageEvent = transport.FindPayload(payload => payload["logentry"] is not null)
        ?? throw new InvalidOperationException("no message event: " + transport.Dump());
    Assert.Equal(expected, messageEvent["logentry"]?["message"]?.GetValue<string>());
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

static class Fixtures
{
    public const string LeakyLoadFailure =
        "Could not open '/home/bob/.local/share/HyperWhisper/models/ggml-base.bin' for bob: Permission denied (errno 13, 0x80131620)";
    public const string RedactedLoadFailure =
        "Could not open '$XDG_DATA_HOME/HyperWhisper/models/ggml-base.bin' for $USER: Permission denied (errno 13, 0x80131620)";
}

sealed class CapturingSentryTransport : Sentry.Extensibility.ITransport
{
    private readonly List<string> _envelopes = [];

    public async Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null, cancellationToken);
        lock (_envelopes) _envelopes.Add(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    public string Dump()
    {
        lock (_envelopes) return string.Join("\n---envelope---\n", _envelopes);
    }

    public JsonObject? FindPayload(Func<JsonObject, bool> predicate)
    {
        lock (_envelopes)
        {
            foreach (var line in _envelopes.SelectMany(envelope => envelope.Split('\n')))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException) { continue; }
                if (node is JsonObject payload && predicate(payload)) return payload;
            }
        }
        return null;
    }
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
