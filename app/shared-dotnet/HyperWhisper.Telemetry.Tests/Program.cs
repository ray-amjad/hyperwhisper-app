using System.Text.Json.Nodes;
using HyperWhisper.Telemetry;
using Sentry;
using Sentry.Protocol;

if (args is [AppDomainChild.Flag, var envelopeFile])
{
    AppDomainChild.Run(envelopeFile);
    return;
}

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
    ("an unobserved AggregateException keeps its inner type and frames, not their text", AggregateKeepsInnerTypeAndFrames),
    ("the inner-exception walk flattens, follows InnerException, and stops at 4", InnerExceptionWalkIsFlattenedAndCapped),
    ("a sanitized unobserved AggregateException reaches the envelope with its inner type and frames, and no text", SanitizedAggregateReachesEnvelopeWithInnerFrames),
    ("configured options leave unobserved task exceptions to the app handler",ConfiguredOptionsDisableSdkUnobservedTaskCapture),
    ("configured options leave AppDomain unhandled exceptions to the app handler", ConfiguredOptionsDisableSdkAppDomainCapture),
    ("a throwing inner StackTrace getter costs only that inner's frames", ThrowingInnerStackTraceCostsOnlyItsFrames),
    ("each inner exception value links to its true parent", InnerExceptionValuesLinkToTheirParent),
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

    var leakyException = sentryEvent.SentryExceptions!.Single();
    leakyException.Mechanism = new Mechanism { Type = "AppDomain.UnhandledException", Description = "raised for bob" };
    leakyException.Mechanism.Data["file"] = "/home/bob/secret.wav";
    leakyException.Mechanism.Data["/home/bob/key"] = "x";
    leakyException.Mechanism.Data["attempt"] = 3;
    leakyException.Mechanism.Data["info"] = new Uri("file:///home/bob/a.wav");
    leakyException.Stacktrace = new SentryStackTrace();
    leakyException.Stacktrace.Frames.Add(LeakyFrame());
    sentryEvent.SentryThreads = [new SentryThread { Name = "main", Stacktrace = new SentryStackTrace { Frames = [LeakyFrame()] } }];

    var sanitized = SanitizeAsBob(sentryEvent);

    Assert.True(ReferenceEquals(sanitized, sentryEvent));
    var exception = sentryEvent.SentryExceptions!.Single();
    Assert.Equal("AppDomain.UnhandledException", exception.Mechanism!.Type);
    Assert.Equal("raised for $USER", exception.Mechanism.Description);
    Assert.Equal<object?>("$HOME/secret.wav", exception.Mechanism.Data["file"]);
    Assert.Equal<object?>("x", exception.Mechanism.Data["$HOME/key"]);
    Assert.Equal<object?>(3, exception.Mechanism.Data["attempt"]);
    Assert.Equal<object?>("file://$HOME/a.wav", exception.Mechanism.Data["info"]);
    foreach (var frame in new[] { exception.Stacktrace!.Frames.Single(), sentryEvent.SentryThreads!.Single().Stacktrace!.Frames.Single() })
    {
        Assert.Equal("$HOME/src/HyperWhisper/Recorder.cs", frame.AbsolutePath);
        Assert.Equal("$HOME/src/HyperWhisper/Recorder.cs", frame.FileName);
        Assert.Equal("$XDG_DATA_HOME/HyperWhisper/app/HyperWhisper.Linux.dll", frame.Package);
        Assert.Equal("Open(\"$HOME/x.wav\");", frame.ContextLine);
        Assert.Equal("// $USER", frame.PreContext.Single());
        Assert.Equal("$HOME/y", frame.Vars["path"]);
        Assert.Equal("HyperWhisper.Linux.Recorder.Start", frame.Function);
        Assert.Equal("HyperWhisper.Linux", frame.Module);
        Assert.Equal(42, frame.LineNumber);
    }
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

    var withParams = new SentryEvent
    {
        Message = new SentryMessage { Message = "Failed to open {0} ({1} bytes)", Params = ["/home/bob/recording.wav", 48128] },
    };
    SanitizeAsBob(withParams);
    var sanitizedParams = withParams.Message!.Params!.ToList();
    Assert.Equal<object?>("$HOME/recording.wav", sanitizedParams[0]);
    Assert.Equal<object?>(48128, sanitizedParams[1]);
}

static SentryStackFrame LeakyFrame()
{
    var frame = new SentryStackFrame
    {
        AbsolutePath = "/home/bob/src/HyperWhisper/Recorder.cs",
        FileName = "/home/bob/src/HyperWhisper/Recorder.cs",
        Package = "/home/bob/.local/share/HyperWhisper/app/HyperWhisper.Linux.dll",
        ContextLine = "Open(\"/home/bob/x.wav\");",
        Function = "HyperWhisper.Linux.Recorder.Start",
        Module = "HyperWhisper.Linux",
        LineNumber = 42,
    };
    frame.PreContext.Add("// bob");
    frame.Vars["path"] = "/home/bob/y";
    return frame;
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
    var plantedDataPath = $"{home}/Music/secret.wav";
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
        // Thrown, not just built, so the event carries real stack frames: their
        // abs_path is this test's source file, which sits under $HOME on a dev box
        // and on CI. Data is copied by the SDK into exception.values[].mechanism.data.
        Exception thrown;
        try
        {
            var leaky = new IOException(raw);
            leaky.Data["file"] = plantedDataPath;
            throw leaky;
        }
        catch (IOException caught)
        {
            thrown = caught;
        }

        var sentryEvent = new SentryEvent(thrown)
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

    // The field list above pins the shape; THIS pins the leak. Every envelope the SDK
    // handed the transport, every line, every string anywhere in it: the home path
    // occurs nowhere (raw text, keys included), and no string still holds the
    // account name as a whole word once the redactor's own tokens are set aside
    // (an account named "user" matches the "USER" inside "$USER").
    var dump = transport.Dump();
    Assert.False(dump.Contains(home, StringComparison.OrdinalIgnoreCase));
    var strings = 0;
    foreach (var value in transport.AllStringValues())
    {
        strings++;
        var withoutTokens = System.Text.RegularExpressions.Regex.Replace(value, @"\$(HOME|USER|XDG_[A-Z]+_HOME)", "#");
        Assert.Equal(LinuxSentryEventSanitizer.RedactUserIdentifiers(withoutTokens), withoutTokens);
    }
    Assert.True(strings > 20);

    // Not vacuous: the planted Data path and the frames were there to leak.
    Assert.Equal(
        LinuxSentryEventSanitizer.RedactUserIdentifiers(plantedDataPath),
        exception?["mechanism"]?["data"]?["file"]?.GetValue<string>());
    if (ThisSourceFile().StartsWith(home + "/", StringComparison.Ordinal))
    {
        var frames = exception?["stacktrace"]?["frames"]?.AsArray() ?? [];
        Assert.True(frames.Any(frame =>
            frame?["abs_path"]?.GetValue<string>() == LinuxSentryEventSanitizer.RedactUserIdentifiers(ThisSourceFile())));
    }
}

static string ThisSourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

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

static void AggregateKeepsInnerTypeAndFrames()
{
    // The shape TaskScheduler.UnobservedTaskException hands the app (#1051): an
    // AggregateException that was never thrown, around one that was.
    Exception thrown;
    try
    {
        ThrowSecretMarker();
        throw new InvalidOperationException("unreachable");
    }
    catch (InvalidOperationException exception)
    {
        thrown = exception;
    }
    var aggregate = new AggregateException(thrown);
    Assert.True(aggregate.StackTrace is null);

    var sanitized = TelemetryPrivacy.SanitizeException(aggregate);

    Assert.True(sanitized.Message.Contains("System.AggregateException", StringComparison.Ordinal));
    Assert.True(sanitized.Message.Contains("System.InvalidOperationException", StringComparison.Ordinal));
    Assert.True(sanitized.StackTrace is not null);
    Assert.True(sanitized.StackTrace!.Contains(nameof(ThrowSecretMarker), StringComparison.Ordinal));
    Assert.False(sanitized.StackTrace.Contains(" in ", StringComparison.Ordinal));
    Assert.False(sanitized.Message.Contains("secret-marker", StringComparison.Ordinal));
    Assert.False(sanitized.StackTrace.Contains("secret-marker", StringComparison.Ordinal));
    Assert.False(sanitized.ToString().Contains("secret-marker", StringComparison.Ordinal));
    Assert.True(sanitized.InnerException is null);
    Assert.Equal(0, sanitized.Data.Count);
}

static void SanitizedAggregateReachesEnvelopeWithInnerFrames()
{
    // The production backend's Capture, the production options, the real SDK.
    Exception thrown;
    try
    {
        ThrowSecretMarker();
        throw new InvalidOperationException("unreachable");
    }
    catch (InvalidOperationException exception)
    {
        thrown = exception;
    }

    var transport = new CapturingSentryTransport();
    using (SentrySdk.Init(options =>
    {
        SentryTelemetryBackend.ConfigureOptions(options, TestConfiguration());
        options.Transport = transport;
        options.ProfilesSampleRate = 0;
        options.AutoSessionTracking = false;
    }))
    {
        new SentryTelemetryBackend().Capture(
            TelemetryPrivacy.SanitizeException(new AggregateException(thrown)),
            "Unobserved task exception");
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    var errorEvent = transport.FindPayload(payload => payload["exception"] is not null)
        ?? throw new InvalidOperationException("no error event: " + transport.Dump());
    var values = errorEvent["exception"]?["values"]?.AsArray() ?? [];
    Assert.Equal("System.AggregateException", values[^1]?["type"]?.GetValue<string>());
    var inner = values.SingleOrDefault(value => value?["type"]?.GetValue<string>() == "System.InvalidOperationException");
    Assert.True(inner is not null);
    var frames = inner!["stacktrace"]?["frames"]?.AsArray() ?? [];
    Assert.True(frames.Any(frame =>
        frame?["function"]?.GetValue<string>().Contains(nameof(ThrowSecretMarker), StringComparison.Ordinal) == true));
    Assert.True(frames.All(frame => frame?["abs_path"] is null && frame?["filename"] is null));
    Assert.Equal("chained", inner["mechanism"]?["type"]?.GetValue<string>());

    // Every byte the SDK handed the transport: no message, Data or HResult text.
    var dump = transport.Dump();
    Assert.False(dump.Contains("secret-marker", StringComparison.Ordinal));
    Assert.False(dump.Contains("/home/bob", StringComparison.Ordinal));
    Assert.False(dump.Contains("0x80131620", StringComparison.OrdinalIgnoreCase));
    Assert.False(dump.Contains(thrown.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void ThrowSecretMarker()
{
    var exception = new InvalidOperationException("secret-marker at /home/bob/x.wav");
    exception.Data["transcript"] = "secret-marker";
    exception.HResult = unchecked((int)0x80131620);
    throw exception;
}

static void InnerExceptionWalkIsFlattenedAndCapped()
{
    // Nested aggregates are containers: flattened, never named. Flatten() lists the
    // direct inner exceptions before a nested aggregate's, and the walk keeps that order.
    var nested = new AggregateException(
        new AggregateException(new TimeoutException("secret-marker")),
        new IOException("secret-marker", new UnauthorizedAccessException("secret-marker")));
    var flattened = TelemetryPrivacy.SanitizeException(nested).Message;
    Assert.True(flattened.Contains(
        "(inner: System.IO.IOException, System.TimeoutException, System.UnauthorizedAccessException)",
        StringComparison.Ordinal));
    Assert.False(flattened.Contains("secret-marker", StringComparison.Ordinal));

    // A plain InnerException chain of 6 names the first 4 only.
    Exception chain = new FormatException("secret-marker");
    chain = new KeyNotFoundException("secret-marker", chain);
    chain = new NotSupportedException("secret-marker", chain);
    chain = new ArgumentException("secret-marker", chain);
    chain = new IOException("secret-marker", chain);
    chain = new TimeoutException("secret-marker", chain);
    var outer = new InvalidOperationException("secret-marker", chain);
    var capped = TelemetryPrivacy.SanitizeException(outer).Message;
    Assert.True(capped.Contains(
        "(inner: System.TimeoutException, System.IO.IOException, System.ArgumentException, System.NotSupportedException)",
        StringComparison.Ordinal));
    Assert.False(capped.Contains("KeyNotFoundException", StringComparison.Ordinal));
    Assert.False(capped.Contains("secret-marker", StringComparison.Ordinal));

    // No inner exception: the message is the pre-#1051 text, so existing Sentry
    // groups for plain exceptions do not split.
    Assert.Equal(
        "A System.InvalidOperationException was reported with message, inner-exception, and data content removed.",
        TelemetryPrivacy.SanitizeException(new InvalidOperationException("secret-marker")).Message);
}

// Sentry 4.12.1 has no public getter for its default integrations (Integrations and
// HasIntegration are internal), so both tests observe the behaviour instead: the
// production options, the real SDK, an in-memory transport, and the real event.
static void ConfiguredOptionsDisableSdkUnobservedTaskCapture()
{
    var transport = new CapturingSentryTransport();
    using var raised = new ManualResetEventSlim(false);
    EventHandler<UnobservedTaskExceptionEventArgs> probe = (_, args) =>
    {
        if (args.Exception.InnerException?.Message == "unobserved-marker") raised.Set();
    };
    TaskScheduler.UnobservedTaskException += probe;
    try
    {
        using (SentrySdk.Init(options =>
        {
            SentryTelemetryBackend.ConfigureOptions(options, TestConfiguration());
            options.Transport = transport;
            options.ProfilesSampleRate = 0;
            options.AutoSessionTracking = false;
        }))
        {
            SentrySdk.CaptureMessage("sdk-alive");
            for (var attempt = 0; attempt < 50 && !raised.IsSet; attempt++)
            {
                AbandonFaultedTask();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                raised.Wait(TimeSpan.FromMilliseconds(100));
            }
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }
    finally
    {
        TaskScheduler.UnobservedTaskException -= probe;
    }

    // Not vacuous: the event fired, and the transport did carry an event.
    Assert.True(raised.IsSet);
    Assert.True(transport.FindPayload(payload => payload["logentry"] is not null) is not null);
    Assert.True(transport.FindPayload(payload => payload["exception"] is not null) is null);
    Assert.False(transport.Dump().Contains("unobserved-marker", StringComparison.Ordinal));
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void AbandonFaultedTask()
{
    var task = Task.Run(new Action(() => throw new InvalidOperationException("unobserved-marker")));
    // Waits for the fault without observing it.
    ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
}

static void ConfiguredOptionsDisableSdkAppDomainCapture()
{
    // An unhandled exception ends the process, so it runs in a child copy of this
    // program; the child's transport appends each envelope to a file.
    var envelopeFile = Path.Combine(Path.GetTempPath(), $"hw-telemetry-{Guid.NewGuid():N}.envelopes");
    try
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var start = new System.Diagnostics.ProcessStartInfo(self)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(AppDomainChild.Flag);
        start.ArgumentList.Add(envelopeFile);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        using var child = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("no child");
        var stderr = child.StandardError.ReadToEndAsync();
        var stdout = child.StandardOutput.ReadToEndAsync();
        if (!child.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException("child did not exit");
        }

        // Not vacuous: the child really died of the unhandled exception, and its
        // transport really carried an event before that.
        Assert.True(child.ExitCode != 0);
        Assert.True(stderr.Result.Contains(AppDomainChild.Marker, StringComparison.Ordinal));
        Assert.True(stdout.Result.Contains("child-flushed", StringComparison.Ordinal));
        var transport = CapturingSentryTransport.Load(envelopeFile);
        Assert.True(transport.FindPayload(payload => payload["logentry"] is not null) is not null);
        Assert.True(transport.FindPayload(payload => payload["exception"] is not null) is null);
        Assert.False(transport.Dump().Contains(AppDomainChild.Marker, StringComparison.Ordinal));
    }
    finally
    {
        File.Delete(envelopeFile);
    }
}

static void ThrowingInnerStackTraceCostsOnlyItsFrames()
{
    var outer = new InvalidOperationException("secret-marker", new HostileStackTraceException());
    var sanitized = TelemetryPrivacy.SanitizeException(outer);
    Assert.True(sanitized.Message.Contains("(inner: HostileStackTraceException)", StringComparison.Ordinal));
    var values = ((TelemetryPrivacy.TelemetryReportedException)sanitized).ToSentryExceptions();
    Assert.Equal(2, values.Count);
    Assert.True(values[0].Stacktrace is null);
    Assert.Equal("System.InvalidOperationException", values[1].Type);
}

static void InnerExceptionValuesLinkToTheirParent()
{
    // outer -> middle -> cause: the cause hangs off the middle, not the outer.
    var chain = new InvalidOperationException("secret-marker",
        new IOException("secret-marker", new TimeoutException("secret-marker")));
    var links = Links(chain);
    Assert.Equal((1, 0), links["System.IO.IOException"]);
    Assert.Equal((2, 1), links["System.TimeoutException"]);

    // An aggregate's flattened inners hang off the aggregate; an InnerException off its wrapper.
    var aggregate = new AggregateException(
        new IOException("secret-marker", new UnauthorizedAccessException("secret-marker")),
        new TimeoutException("secret-marker"));
    links = Links(aggregate);
    Assert.Equal((1, 0), links["System.IO.IOException"]);
    Assert.Equal((2, 0), links["System.TimeoutException"]);
    Assert.Equal((3, 1), links["System.UnauthorizedAccessException"]);

    // A nested aggregate is not named, so its contents hang off the exception that holds it.
    var wrapped = new InvalidOperationException("secret-marker", new IOException("secret-marker",
        new AggregateException(new TimeoutException("secret-marker"), new FormatException("secret-marker"))));
    links = Links(wrapped);
    Assert.Equal((1, 0), links["System.IO.IOException"]);
    Assert.Equal((2, 1), links["System.TimeoutException"]);
    Assert.Equal((3, 1), links["System.FormatException"]);

    // And the real SDK keeps the links in the envelope.
    var transport = new CapturingSentryTransport();
    using (InitTestSdk(transport))
    {
        new SentryTelemetryBackend().Capture(TelemetryPrivacy.SanitizeException(chain), null);
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
    var values = transport.FindPayload(payload => payload["exception"] is not null)?["exception"]?["values"]?.AsArray()
        ?? throw new InvalidOperationException("no error event: " + transport.Dump());
    var cause = values.Single(value => value?["type"]?.GetValue<string>() == "System.TimeoutException");
    Assert.Equal(2, cause!["mechanism"]?["exception_id"]?.GetValue<int>());
    Assert.Equal(1, cause["mechanism"]?["parent_id"]?.GetValue<int>());

    static Dictionary<string, (int Id, int Parent)> Links(Exception exception) =>
        ((TelemetryPrivacy.TelemetryReportedException)TelemetryPrivacy.SanitizeException(exception))
            .ToSentryExceptions()
            .Where(value => value.Mechanism?.Type == "chained")
            .ToDictionary(value => value.Type!, value => (value.Mechanism!.ExceptionId!.Value, value.Mechanism.ParentId!.Value));
}

static IDisposable InitTestSdk(CapturingSentryTransport transport, bool autoSessionTracking = false) => SentrySdk.Init(options =>
{
    SentryTelemetryBackend.ConfigureOptions(options, TestConfiguration());
    options.Transport = transport;
    options.ProfilesSampleRate = 0;
    options.AutoSessionTracking = autoSessionTracking;
});

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

sealed class HostileStackTraceException : Exception
{
    public override string? StackTrace => throw new InvalidOperationException("secret-marker");
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

static class AppDomainChild
{
    public const string Flag = "--appdomain-child";
    public const string Marker = "appdomain-marker";

    public static void Run(string envelopeFile)
    {
        // Production options, a file-backed transport, and no app handler: any
        // exception event in the file came from the SDK's own integration.
        SentrySdk.Init(options =>
        {
            SentryTelemetryBackend.ConfigureOptions(options, TelemetryConfiguration.Create(
                "https://public@example.invalid/1", "test", typeof(Program).Assembly));
            options.Transport = new CapturingSentryTransport(envelopeFile);
            options.ProfilesSampleRate = 0;
            options.AutoSessionTracking = false;
        });
        SentrySdk.CaptureMessage("child-alive");
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Console.WriteLine("child-flushed");
        Console.Out.Flush();

        var thread = new Thread(() => throw new InvalidOperationException(Marker));
        thread.Start();
        thread.Join();
    }
}

sealed class CapturingSentryTransport(string? mirrorFile = null) : Sentry.Extensibility.ITransport
{
    private const string Separator = "\n---envelope---\n";
    private readonly List<string> _envelopes = [];

    public static CapturingSentryTransport Load(string file)
    {
        var transport = new CapturingSentryTransport();
        if (File.Exists(file))
        {
            transport._envelopes.AddRange(File.ReadAllText(file)
                .Split(Separator, StringSplitOptions.RemoveEmptyEntries));
        }
        return transport;
    }

    public async Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await envelope.SerializeAsync(stream, null, cancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        lock (_envelopes)
        {
            _envelopes.Add(text);
            if (mirrorFile is not null) File.AppendAllText(mirrorFile, text + Separator);
        }
    }

    public string Dump()
    {
        lock (_envelopes) return string.Join(Separator, _envelopes);
    }

    public IEnumerable<string> AllStringValues()
    {
        var values = new List<string>();
        lock (_envelopes)
        {
            foreach (var line in _envelopes.SelectMany(envelope => envelope.Split('\n')))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (System.Text.Json.JsonException) { continue; }
                Collect(node);
            }
        }
        return values;

        void Collect(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj) Collect(property.Value);
                    break;
                case JsonArray array:
                    foreach (var item in array) Collect(item);
                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(out var text):
                    values.Add(text);
                    break;
            }
        }
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
