using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;

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
    ("audio restoration is never caller-cancelled", AudioRestorationIsNonCancelable),
    ("live models are provider-specific", LiveModelsAreProviderSpecific),
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
