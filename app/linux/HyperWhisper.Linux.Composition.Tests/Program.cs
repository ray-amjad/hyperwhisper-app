using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.LocalInference;
using HyperWhisper.LocalApi;

[assembly: SupportedOSPlatform("linux")]

var tests = new (string Name, Func<Task> Run)[]
{
    ("toggle owns safe injection lifecycle", ToggleOwnsSafeInjectionLifecycle),
    ("failed start restores clipboard immediately", FailedStartRestoresImmediately),
    ("stop exception unwinds injection session", StopExceptionUnwindsInjection),
    ("secure completion preserves clipboard", SecureCompletionPreservesClipboard),
    ("push to talk starts stops and cancels", PushToTalkStartsStopsAndCancels),
    ("conflicting shortcuts are rejected", ConflictingShortcutsAreRejected),
    ("reconfiguration does not duplicate shortcut readers", ReconfigurationDoesNotDuplicateReaders),
    ("clipboard restore respects secure fields and preference", ClipboardRestoreRespectsOutcome),
    ("context capture skips disabled OCR", ContextCaptureSkipsDisabledOcr),
    ("OCR survives unavailable context", OcrSurvivesUnavailableContext),
    ("context survives OCR failure", ContextSurvivesOcrFailure),
    ("live post-processing failure persists raw transcript", LivePostProcessingFailurePersistsRaw),
    ("cloud provider storage values route deterministically", CloudProviderStorageRoutes),
    ("audio restoration is never caller-cancelled", AudioRestorationIsNonCancelable),
    ("live models are provider-specific", LiveModelsAreProviderSpecific),
    ("production Whisper backend selection reaches inference", WhisperBackendSelectionReachesInference),
    ("production Whisper CPU fallback policy is enforced", WhisperCpuFallbackPolicyIsEnforced),
    ("production Whisper settings select detected and explicit backends", WhisperSettingsSelectBackends),
    ("Local API post-processing matches Windows transient modes", LocalApiPostProcessingTransientModes),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length}/{tests.Length} Linux composition tests passed");

static async Task ToggleOwnsSafeInjectionLifecycle()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default).IsSuccess, "configure failed");

    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => fixture.Recording.StartCount == 1);
    Assert(fixture.Injection.Events.SequenceEqual(["capture", "session-start"]), "start lifecycle order changed");

    fixture.Shortcuts.Release(LinuxInteractionCoordinator.ToggleActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => fixture.Recording.StopCount == 1);
    Assert(fixture.Injection.Events.SequenceEqual(["capture", "session-start", "session-end", "restore-scheduled"]),
        "stop lifecycle order changed");
}

static async Task FailedStartRestoresImmediately()
{
    var fixture = new InteractionFixture();
    fixture.Recording.StartResult = PlatformResult.Failure("recording.failed", "failed");
    using var coordinator = fixture.Create();
    PlatformError? failure = null;
    coordinator.OperationFailed += (_, error) => failure = error;
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default).IsSuccess, "configure failed");
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => failure is not null);
    Assert(failure?.Code == "recording.failed", "original failure was not surfaced");
    Assert(fixture.Injection.Events.SequenceEqual(["capture", "session-start", "session-end", "restore-now"]),
        "failed start did not unwind injection state");
}

static async Task StopExceptionUnwindsInjection()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default).IsSuccess, "configure failed");
    await coordinator.StartRecordingAsync();
    fixture.Recording.ThrowOnStop = true;
    await coordinator.StopRecordingAsync();
    Assert(fixture.Injection.Events[^2..].SequenceEqual(["restore-now", "session-end"]),
        "stop exception left clipboard or target session active");
}

static async Task SecureCompletionPreservesClipboard()
{
    var fixture = new InteractionFixture();
    fixture.Recording.StopOutcome = new(PlatformResult.Success(), RestoreClipboard: false);
    using var coordinator = fixture.Create();
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default).IsSuccess, "configure failed");
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => fixture.Recording.IsActive);
    fixture.Shortcuts.Release(LinuxInteractionCoordinator.ToggleActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => fixture.Recording.StopCount == 1);
    Assert(!fixture.Injection.Events.Contains("restore-scheduled"), "secure-field clipboard would be overwritten");
}

static async Task PushToTalkStartsStopsAndCancels()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default with
    {
        PushToTalk = new PushToTalkConfiguration(PushToTalkMode.Modifier, ModifierSide.LeftAlt)
    }).IsSuccess, "configure failed");
    fixture.PushToTalk.RaisePressed();
    await UntilAsync(() => fixture.Recording.StartCount == 1);
    fixture.PushToTalk.RaiseReleased();
    await UntilAsync(() => fixture.Recording.StopCount == 1);

    fixture.PushToTalk.RaisePressed();
    await UntilAsync(() => fixture.Recording.StartCount == 2);
    fixture.PushToTalk.RaiseInterfered();
    await UntilAsync(() => fixture.Recording.CancelCount == 1);
    Assert(fixture.Injection.Events[^2..].SequenceEqual(["session-end", "restore-now"]),
        "PTT interference did not safely cancel");
}

static Task ConflictingShortcutsAreRejected()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    var shortcut = LinuxInteractionConfiguration.Default.ToggleShortcut;
    var result = coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default with
    {
        PushToTalk = new PushToTalkConfiguration(PushToTalkMode.CustomShortcut, CustomShortcut: shortcut)
    });
    Assert(result.IsFailure && result.Error?.Code == "interaction.shortcut_conflict", "shortcut conflict accepted");
    return Task.CompletedTask;
}

static async Task ReconfigurationDoesNotDuplicateReaders()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default).IsSuccess, "first configure failed");
    Assert(coordinator.ConfigureAndStart(LinuxInteractionConfiguration.Default with
    {
        ClipboardRestoreDelay = TimeSpan.FromSeconds(2)
    }).IsSuccess, "second configure failed");
    Assert(fixture.Shortcuts.StartCount == 1, "reconfiguration started a duplicate shortcut reader");
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ToggleActionName);
    await UntilAsync(() => fixture.Recording.StartCount == 1);
    Assert(fixture.Recording.StartCount == 1, "reconfiguration duplicated event handlers");
}

static Task ClipboardRestoreRespectsOutcome()
{
    Assert(!InteractionStopOutcome.FromInjection(PlatformResult.Success(), TextInjectionOutcome.SecureFieldSkipped, true).RestoreClipboard,
        "secure-field clipboard would be restored");
    Assert(InteractionStopOutcome.FromInjection(PlatformResult.Success(), TextInjectionOutcome.Pasted, true).RestoreClipboard,
        "pasted outcome ignored restore preference");
    Assert(InteractionStopOutcome.FromInjection(PlatformResult.Success(), TextInjectionOutcome.CopiedToClipboard, true).RestoreClipboard,
        "copied outcome ignored restore preference");
    Assert(!InteractionStopOutcome.FromInjection(PlatformResult.Success(), TextInjectionOutcome.Pasted, false).RestoreClipboard,
        "disabled restore preference was ignored");
    return Task.CompletedTask;
}

static async Task ContextCaptureSkipsDisabledOcr()
{
    var provider = new FakeContextProvider { Result = PlatformResult<ApplicationContextSnapshot?>.Success(
        new ApplicationContextSnapshot { ProcessName = "terminal", WindowTitle = "shell" }) };
    var ocr = new FakeOcr();
    var coordinator = new LinuxContextCaptureCoordinator(provider, ocr);
    var outcome = await coordinator.CaptureAsync(false);
    Assert(outcome.Snapshot?.ProcessName == "terminal" && ocr.Calls == 0, "disabled OCR was invoked");
}

static async Task OcrSurvivesUnavailableContext()
{
    var provider = new FakeContextProvider
    {
        Result = PlatformResult<ApplicationContextSnapshot?>.Failure("context.unavailable", "unavailable")
    };
    var ocr = new FakeOcr { Result = PlatformResult<string?>.Success("  visible words  ") };
    var outcome = await new LinuxContextCaptureCoordinator(provider, ocr).CaptureAsync(true);
    Assert(outcome.ContextFailure?.Code == "context.unavailable", "context failure was lost");
    Assert(outcome.Snapshot?.ScreenOcrText == "visible words", "OCR was lost without app context");
}

static async Task ContextSurvivesOcrFailure()
{
    var provider = new FakeContextProvider { Result = PlatformResult<ApplicationContextSnapshot?>.Success(
        new ApplicationContextSnapshot { ProcessName = "editor" }) };
    var ocr = new FakeOcr { Result = PlatformResult<string?>.Failure("ocr.denied", "denied") };
    var outcome = await new LinuxContextCaptureCoordinator(provider, ocr).CaptureAsync(true);
    Assert(outcome.Snapshot?.ProcessName == "editor", "context was lost after OCR failure");
    Assert(outcome.OcrFailure?.Code == "ocr.denied", "OCR failure was lost");
}

static async Task LivePostProcessingFailurePersistsRaw()
{
    var transcript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var history = new FakeHistory(transcript);
    var mode = new Mode { Name = "Local", PostProcessingMode = 2, PostProcessingProvider = "local_llm" };
    var result = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "  Ray raw transcript  ", transcript, mode, null, new ThrowingPostProcessor(),
        new FakeTextInjection(), history);
    Assert(result.Result.IsSuccess, "post-processing exception failed live transcription");
    Assert(history.Updated?.Status == TranscriptStatus.Completed, "history remained Processing");
    Assert(history.Updated?.Text == "Ray raw transcript" && history.Updated.PostProcessedText is null,
        "raw transcript fallback was not persisted");

    var cloudTranscript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var cloudHistory = new FakeHistory(cloudTranscript);
    var cloudMode = new Mode { Name = "Cloud", PostProcessingMode = 1, PostProcessingProvider = "anthropic" };
    var cloudResult = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "  Ray cloud raw transcript  ", cloudTranscript, cloudMode, null, new ThrowingPostProcessor(),
        new FakeTextInjection(), cloudHistory);
    Assert(cloudResult.Result.IsSuccess && cloudHistory.Updated?.Text == "Ray cloud raw transcript"
        && cloudHistory.Updated.PostProcessedText is null,
        "cloud post-processing failure did not preserve live raw transcript");
}

static Task CloudProviderStorageRoutes()
{
    var expected = new[]
    {
        "openai", "anthropic", "groq", "grok", "gemini", "cerebras", "mistral",
        "hyperwhispercloud", "hyperwhisper", "hyperwhisper_cloud",
    };
    foreach (var value in expected)
        Assert(LinuxPostProcessingRouter.TryResolveProvider(value, out _, out var endpointId) && endpointId is null,
            $"cloud post-processing provider {value} was not resolved");
    var id = Guid.NewGuid();
    Assert(LinuxPostProcessingRouter.TryResolveProvider($"custom:{id:D}", out var custom, out var resolved)
        && custom == HyperWhisper.CloudPostProcessing.CloudPostProcessingProvider.Custom && resolved == id,
        "custom endpoint provider was not resolved");
    Assert(!LinuxPostProcessingRouter.TryResolveProvider("unknown", out _, out _),
        "unknown cloud provider was accepted");
    return Task.CompletedTask;
}

static async Task AudioRestorationIsNonCancelable()
{
    var environment = new FakeAudioEnvironmentSession();
    var volume = new FakeMicrophoneVolume();
    var warm = new FakeKeepWarm();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await LinuxRecordingAudioRestorer.RestoreAsync(volume, environment, warm, "mic");
    Assert(environment.RestoreCalls == 1 && !environment.ObservedCancellationCanBeCanceled,
        "audio environment restore received a caller cancellation token");
    Assert(volume.RestoreCalls == 1 && warm.ResumeCalls == 1, "audio restoration did not complete every step");
}

static Task LiveModelsAreProviderSpecific()
{
    Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider("deepgram", "nova-3-general") == "nova-3-general",
        "Deepgram model was not routed");
    foreach (var provider in new[] { "openai", "elevenlabs", "grok", "hyperwhisper" })
        Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider(provider, "nova-3-general") is null,
            $"Deepgram model leaked to {provider}");
    return Task.CompletedTask;
}

static async Task WhisperBackendSelectionReachesInference()
{
    foreach (var backend in new[] { LocalWhisperBackend.Cpu, LocalWhisperBackend.Vulkan, LocalWhisperBackend.Cuda12 })
    {
        using var fixture = new WhisperTranscriberFixture(backend, allowFallback: false, RuntimeFor(backend));
        var result = await fixture.Transcriber.TranscribeAsync(fixture.AudioPath, "en");
        Assert(result.IsSuccess, $"production transcriber rejected {backend}");
        Assert(fixture.Service.LoadOptions?.Backend == backend
            && fixture.Service.LoadOptions.AllowCpuFallback == false,
            $"production transcriber did not forward {backend} and fallback policy");
        Assert(fixture.Service.TranscribeCalls == 1, $"production transcriber did not invoke {backend} inference");
        Assert(fixture.Transcriber.Capability.DisplayName.Contains(
            backend == LocalWhisperBackend.Cuda12 ? "CUDA 12" : backend.ToString(),
            StringComparison.OrdinalIgnoreCase), $"{backend} capability was not reported");
    }
}

static async Task WhisperCpuFallbackPolicyIsEnforced()
{
    using (var allowed = new WhisperTranscriberFixture(
        LocalWhisperBackend.Vulkan, allowFallback: true, "Cpu: fallback runtime"))
    {
        var result = await allowed.Transcriber.TranscribeAsync(allowed.AudioPath, "en");
        Assert(result.IsSuccess && result.Provider == "Local Whisper (CPU fallback)",
            "allowed GPU fallback was not surfaced honestly");
        Assert(allowed.Service.LoadOptions?.AllowCpuFallback == true,
            "allowed CPU fallback policy was not forwarded");
    }
    using (var denied = new WhisperTranscriberFixture(
        LocalWhisperBackend.Cuda12, allowFallback: false, "Cpu: fallback runtime"))
    {
        var result = await denied.Transcriber.TranscribeAsync(denied.AudioPath, "en");
        Assert(!result.IsSuccess && result.Failure?.Code == PortableTranscriptionErrorCode.BackendUnavailable,
            "forbidden CUDA fallback was accepted");
        Assert(denied.Service.TranscribeCalls == 0,
            "transcription ran after forbidden CUDA fallback");
    }
}

static Task WhisperSettingsSelectBackends()
{
    var previousBackend = Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND");
    var previousFallback = Environment.GetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK");
    var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-whisper-settings-{Guid.NewGuid():N}");
    try
    {
        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND", null);
        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK", null);
        var detected = new LinuxWhisperRuntimePreferenceSource(
            new MissingPrivateFiles(), new StaticPaths(root),
            new FixedGpu(new GpuInfo { Name = "NVIDIA", SupportsCuda = true, SupportsVulkan = true }));
        Assert(detected.Resolve() == new LinuxWhisperRuntimePreference(LocalWhisperBackend.Cuda12, true),
            "automatic selection did not prefer detected CUDA 12");

        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND", "vulkan");
        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK", "0");
        Assert(detected.Resolve() == new LinuxWhisperRuntimePreference(LocalWhisperBackend.Vulkan, false),
            "explicit strict Vulkan setting was not honored");
    }
    finally
    {
        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_BACKEND", previousBackend);
        Environment.SetEnvironmentVariable("HYPERWHISPER_WHISPER_CPU_FALLBACK", previousFallback);
    }
    return Task.CompletedTask;
}

static string RuntimeFor(LocalWhisperBackend backend) => backend switch
{
    LocalWhisperBackend.Cuda12 => "Cuda12: test runtime",
    LocalWhisperBackend.Vulkan => "Vulkan: test runtime",
    _ => "Cpu: test runtime",
};

static async Task LocalApiPostProcessingTransientModes()
{
    var root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-local-api-pp-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var database = new ApplicationDb(new StaticPaths(root));
        await database.MigrateAsync();
        var repository = new ModeRepository(database);
        var disabled = new Mode
        {
            Name = "Disabled", IsDefault = true, PostProcessingMode = 0,
            PostProcessingProvider = "none", Preset = "hyper",
        };
        await repository.UpsertAsync(disabled);
        var processor = new CapturingPostProcessor();
        var adapter = new LinuxLocalApiPostProcessor(processor, repository);

        try
        {
            _ = await adapter.ProcessAsync(
                new PostProcessRequest("raw", disabled.Id.ToString("D"), null, null, null, null),
                CancellationToken.None);
            throw new InvalidOperationException("disabled saved mode was accepted without an override");
        }
        catch (ArgumentException)
        {
        }

        var context = new LocalApiApplicationContext(
            "terminal", "Shell", null, null, null, null, null, "command",
            "terminal", "strong", "localApi", null);
        var cloud = await adapter.ProcessAsync(
            new PostProcessRequest(
                "raw", disabled.Id.ToString("D"), "message", null, "openai", "gpt-test", context),
            CancellationToken.None);
        Assert(processor.Mode is { PostProcessingMode: 1, PostProcessingProvider: "openai", LanguageModel: "gpt-test", Preset: "message" }
            && processor.Mode.Id != disabled.Id,
            "saved-mode cloud overrides were not applied to a transient clone");
        Assert(processor.Context?.AppType == "terminal" && cloud.Model == "gpt-test",
            "cloud override context/model response was lost");
        Assert((await repository.ListAsync()).Single().PostProcessingMode == 0,
            "Local API override mutated the persisted mode");

        _ = await adapter.ProcessAsync(
            new PostProcessRequest("raw", null, null, "custom prompt", "localLlm", "local.gguf"),
            CancellationToken.None);
        Assert(processor.Mode is
            { PostProcessingMode: 2, PostProcessingProvider: "local_llm", Preset: "custom", LocalPostProcessingModel: "local.gguf" },
            "local transient prompt/provider/model overrides did not match Windows semantics");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task UntilAsync(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(10, deadline.Token);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class InteractionFixture
{
    public FakeShortcuts Shortcuts { get; } = new();
    public FakePushToTalk PushToTalk { get; } = new();
    public FakeTextInjection Injection { get; } = new();
    public FakeRecording Recording { get; } = new();
    public LinuxInteractionCoordinator Create() => new(Shortcuts, PushToTalk, Injection, Recording, new InlineDispatcher());
}

sealed class WhisperTranscriberFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-whisper-route-{Guid.NewGuid():N}");
    public WhisperTranscriberFixture(LocalWhisperBackend backend, bool allowFallback, string runtime)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "ggml-test.bin"), [1]);
        AudioPath = Path.Combine(_root, "audio.wav");
        File.WriteAllBytes(AudioPath, [1]);
        Service = new FakeLocalWhisperService(runtime);
        Transcriber = new LinuxLocalWhisperTranscriber(
            _root, Service, new FixedWhisperPreferences(new(backend, allowFallback)));
    }
    public string AudioPath { get; }
    public FakeLocalWhisperService Service { get; }
    public LinuxLocalWhisperTranscriber Transcriber { get; }
    public void Dispose()
    {
        Transcriber.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}

sealed class FixedWhisperPreferences(LinuxWhisperRuntimePreference preference) : ILinuxWhisperRuntimePreferenceSource
{
    public LinuxWhisperRuntimePreference Resolve() => preference;
}

sealed class FakeLocalWhisperService(string runtime) : ILocalWhisperService
{
    public LocalWhisperLoadOptions? LoadOptions { get; private set; }
    public int TranscribeCalls { get; private set; }
    public bool IsLoaded => LoadOptions is not null;
    public string? Runtime => runtime;
    public Task<LocalWhisperResult> LoadAsync(LocalWhisperLoadOptions options, CancellationToken cancellationToken = default)
    {
        LoadOptions = options;
        return Task.FromResult(LocalWhisperResult.Success(string.Empty, runtime));
    }
    public Task<LocalWhisperResult> TranscribeAsync(LocalWhisperRequest request, CancellationToken cancellationToken = default)
    {
        TranscribeCalls++;
        return Task.FromResult(LocalWhisperResult.Success("Ray GPU route", runtime));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class CapturingPostProcessor : ITranscriptionPostProcessor
{
    public Mode? Mode { get; private set; }
    public ApplicationContextSnapshot? Context { get; private set; }

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(transcript, mode, null, cancellationToken);

    public Task<PortablePostProcessingResult> ProcessAsync(
        string transcript,
        Mode mode,
        ApplicationContextSnapshot? applicationContext,
        CancellationToken cancellationToken = default)
    {
        Mode = mode;
        Context = applicationContext;
        return Task.FromResult(PortablePostProcessingResult.Applied($"processed {transcript}", "test-provider"));
    }
}

sealed class FixedGpu(GpuInfo? gpu) : IGpuInfoProvider
{
    public PlatformResult<GpuInfo?> GetBestGpu() => PlatformResult<GpuInfo?>.Success(gpu);
    public void ClearCache() { }
}

sealed class MissingPrivateFiles : IPrivateFileService
{
    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents) => PlatformResult.Success();
    public PlatformResult WriteAllTextAtomically(string path, string contents) => PlatformResult.Success();
    public PlatformResult<byte[]?> ReadAllBytes(string path) => PlatformResult<byte[]?>.Success(null);
    public PlatformResult<string?> ReadAllText(string path) => PlatformResult<string?>.Success(null);
    public PlatformResult Delete(string path) => PlatformResult.Success();
    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) => PlatformResult<bool>.Success(true);
}

sealed class StaticPaths(string root) : IAppPaths
{
    public string DataDirectory => root;
    public string ConfigDirectory => root;
    public string CacheDirectory => root;
    public string StateDirectory => root;
    public string LogsDirectory => root;
    public string ModelsDirectory => root;
    public string RecordingsDirectory => root;
    public string RuntimeDirectory => root;
    public string TemporaryDirectory => root;
}

sealed class FakeShortcuts : IGlobalShortcutService
{
    private IReadOnlyDictionary<string, GlobalShortcut> _registered = new Dictionary<string, GlobalShortcut>();
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;
    public int StartCount { get; private set; }
    public PlatformResult Start() { StartCount++; return PlatformResult.Success(); }
    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(IReadOnlyCollection<NamedShortcut> shortcuts)
    {
        _registered = shortcuts.ToDictionary(value => value.Name, value => value.Shortcut);
        return shortcuts.ToDictionary(value => value.Name, _ => PlatformResult.Success());
    }
    public void Press(string name) => ShortcutPressed?.Invoke(this, new(name, _registered[name]));
    public void Release(string name) => ShortcutReleased?.Invoke(this, new(name, _registered[name]));
    public void Clear() => _registered = new Dictionary<string, GlobalShortcut>();
    public void ResetKeyboardState() { }
    public void Dispose() { }
}

sealed class FakePushToTalk : IPushToTalkMonitor
{
    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Interfered;
    public void Configure(PushToTalkConfiguration configuration) { }
    public PlatformResult Start() => PlatformResult.Success();
    public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
    public void RaiseReleased() => Released?.Invoke(this, EventArgs.Empty);
    public void RaiseInterfered() => Interfered?.Invoke(this, EventArgs.Empty);
    public void Reset() { }
    public void ResetToIdle() { }
    public void Dispose() { }
}

sealed class FakeTextInjection : ITextInjectionService
{
    public List<string> Events { get; } = [];
    public bool IsCapturedTargetAvailable => true;
    public void CaptureTarget() => Events.Add("capture");
    public void StartSession() => Events.Add("session-start");
    public void EndSession() => Events.Add("session-end");
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) => Events.Add("restore-scheduled");
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default)
    { Events.Add("restore-now"); return ValueTask.FromResult(PlatformResult.Success()); }
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(PlatformResult.Success());
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TextInjectionOutcome.Pasted);
    public void Dispose() { }
}

sealed class FakeRecording : IInteractionRecordingSession
{
    public bool IsActive { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int CancelCount { get; private set; }
    public PlatformResult StartResult { get; set; } = PlatformResult.Success();
    public InteractionStopOutcome StopOutcome { get; set; } = new(PlatformResult.Success());
    public bool ThrowOnStop { get; set; }
    public ValueTask<PlatformResult> StartAsync(CancellationToken cancellationToken = default)
    { StartCount++; IsActive = StartResult.IsSuccess; return ValueTask.FromResult(StartResult); }
    public ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default)
    { StopCount++; IsActive = false; if (ThrowOnStop) throw new InvalidOperationException("expected"); return ValueTask.FromResult(StopOutcome); }
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    { CancelCount++; IsActive = false; return ValueTask.CompletedTask; }
}

sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken = default) => action();
}

sealed class FakeContextProvider : IApplicationContextProvider
{
    public PlatformResult<ApplicationContextSnapshot?> Result { get; set; } =
        PlatformResult<ApplicationContextSnapshot?>.Success(null);
    public ValueTask<PlatformResult<ApplicationContextSnapshot?>> GatherAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Result);
    public void Dispose() { }
}

sealed class FakeOcr : IScreenOcrService
{
    public int Calls { get; private set; }
    public PlatformResult<string?> Result { get; set; } = PlatformResult<string?>.Success(null);
    public ValueTask<PlatformResult<string?>> CaptureAndRecognizeAsync(int maxCharacters = 2000,
        CancellationToken cancellationToken = default)
    { Calls++; return ValueTask.FromResult(Result); }
}

sealed class ThrowingPostProcessor : ITranscriptionPostProcessor
{
    public Task<PortablePostProcessingResult> ProcessAsync(string transcript, Mode mode,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("expected");
}

sealed class FakeHistory(Transcript transcript) : ITranscriptionHistoryStore
{
    public Transcript? Updated { get; private set; }
    public Task<Transcript?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Transcript?>(transcript);
    public Task AddAsync(Transcript value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> UpdateAsync(Transcript value, CancellationToken cancellationToken = default)
    { Updated = value; return Task.FromResult(true); }
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

sealed class FakeMicrophoneVolume : IMicrophoneVolumeService
{
    public int RestoreCalls { get; private set; }
    public PlatformResult BoostIfNeeded(string deviceId) => PlatformResult.Success();
    public PlatformResult Restore() { RestoreCalls++; return PlatformResult.Success(); }
    public PlatformResult<float?> ReadLevel(string deviceId) => PlatformResult<float?>.Success(1);
}

sealed class FakeKeepWarm : IMicrophoneKeepWarmService
{
    public int ResumeCalls { get; private set; }
    public void Configure(bool enabled, string? deviceId) { }
    public void SuspendForRecording() { }
    public void ResumeAfterRecording(string? deviceId) => ResumeCalls++;
    public void Dispose() { }
}

sealed class FakeAudioEnvironmentSession : IAudioEnvironmentSession
{
    public int RestoreCalls { get; private set; }
    public bool ObservedCancellationCanBeCanceled { get; private set; }
    public ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    { RestoreCalls++; ObservedCancellationCanBeCanceled = cancellationToken.CanBeCanceled; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
