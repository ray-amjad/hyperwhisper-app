using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using System.Runtime.Versioning;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.LocalInference;
using HyperWhisper.LocalApi;
using HyperWhisper.SpeechOutput;
using HyperWhisper.SharedCore;
using HyperWhisper.Diagnostics;
using HyperWhisper.LiveStreaming;
using System.Diagnostics;

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
    ("interaction actions are atomic and content-free", InteractionActionsAreAtomicAndContentFree),
    ("unsafe persistent cancel shortcut is rejected", UnsafePersistentCancelIsRejected),
    ("interaction registration failure restores prior bindings", InteractionRegistrationRollback),
    ("clipboard restore respects secure fields and preference", ClipboardRestoreRespectsOutcome),
    ("context capture skips disabled OCR", ContextCaptureSkipsDisabledOcr),
    ("OCR survives unavailable context", OcrSurvivesUnavailableContext),
    ("context survives OCR failure", ContextSurvivesOcrFailure),
    ("live post-processing failure persists raw transcript", LivePostProcessingFailurePersistsRaw),
    ("live output processing matches batch semantics", LiveOutputProcessingMatchesBatch),
    ("live auto-paste off copies and persists", LiveAutoPasteOffCopiesAndPersists),
    ("live delivered final is not injected twice", LiveDeliveredFinalIsNotDuplicated),
    ("production live sink forwards partial and final updates", LiveSinkForwardsUpdates),
    ("cloud provider storage values route deterministically", CloudProviderStorageRoutes),
    ("audio restoration is never caller-cancelled", AudioRestorationIsNonCancelable),
    ("live models are provider-specific", LiveModelsAreProviderSpecific),
    ("local and cloud live sessions select isolated engines", LiveEngineRoutingIsIsolated),
    ("production Whisper backend selection reaches inference", WhisperBackendSelectionReachesInference),
    ("production Whisper CPU fallback policy is enforced", WhisperCpuFallbackPolicyIsEnforced),
    ("production Whisper settings select detected and explicit backends", WhisperSettingsSelectBackends),
    ("Local API post-processing matches Windows transient modes", LocalApiPostProcessingTransientModes),
    ("mode cycling is deterministic and wraps", ModeCyclingIsDeterministic),
    ("typed tray actions route without unsafe overlap", TypedTrayActionsRouteSafely),
    ("tray microphone selection is deterministic", TrayMicrophoneSelectionIsDeterministic),
    ("diagnostic capabilities fail closed from platform evidence", DiagnosticCapabilitiesFailClosed),
    ("lifecycle diagnostics expose only fixed fields", LifecycleDiagnosticsAreContentFree),
    ("M4A storage performs a real private FFmpeg encode", M4aStorageEncodes),
    ("M4A history playback performs a real FFmpeg decode", M4aPlaybackDecodes),
    ("anonymous speed opt-out is scoped to HyperWhisper Cloud", LatencyOptOutIsScoped),
    ("Silero detector preserves bounded recurrent state", SileroDetectorStateIsBounded),
    ("packaged Silero ONNX model executes silence fixture", PackagedSileroExecutes),
    ("first-run onboarding persists decisions and gates real readiness", OnboardingStateMachine),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length}/{tests.Length} Linux composition tests passed");

static Task OnboardingStateMachine()
{
    var mode = new Mode { Id = Guid.NewGuid(), Name = "Local Whisper", ProviderType = "local", LocalEngine = "whisper" };
    var microphone = new AudioInputDevice("mic-1", "Test microphone", true);
    var decisions = new List<bool>();
    Mode? selectedMode = mode;
    AudioInputDevice? selectedDevice = microphone;
    var onboarding = new LinuxOnboardingViewModel(
        new(true, true, true, true, true, true, false),
        [mode], mode, [microphone], microphone,
        skipped => { decisions.Add(skipped); return true; },
        value => selectedMode = value,
        value => selectedDevice = value,
        key => key);

    onboarding.Show();
    Assert(onboarding.IsVisible && onboarding.IsWelcome, "onboarding did not start at welcome");
    onboarding.Next();
    Assert(onboarding.IsCapabilities && onboarding.Capabilities.DesktopPortal, "capability step is not evidence-backed");
    onboarding.Next();
    Assert(onboarding.IsProvider && onboarding.IsSelectedModeAvailable, "ready local mode was rejected");
    onboarding.SelectedMode = mode;
    onboarding.Next();
    Assert(onboarding.IsMicrophone, "provider step did not advance");
    onboarding.SelectedDevice = microphone;
    onboarding.Next();
    Assert(onboarding.IsTest && onboarding.IsTestReady, "test readiness did not use the selected mode and microphone");
    onboarding.SetTestStatus("complete", succeeded: true);
    onboarding.Next();
    Assert(!onboarding.IsVisible && decisions.SequenceEqual([false]), "completion was not durably requested");
    Assert(selectedMode == mode && selectedDevice == microphone, "selections did not reach the live adapters");

    var unavailable = new LinuxOnboardingViewModel(
        new(true, true, false, false, false, true, false),
        [new Mode { Id = Guid.NewGuid(), Name = "Parakeet", ProviderType = "local", LocalEngine = "parakeet" }],
        null, [microphone], microphone,
        skipped => { decisions.Add(skipped); return true; }, _ => { }, _ => { }, key => key);
    unavailable.Show(); unavailable.Next(); unavailable.Next(); unavailable.Next(); unavailable.Next();
    Assert(unavailable.IsTest && !unavailable.IsTestReady, "unavailable local engine incorrectly passed readiness");
    unavailable.Skip();
    Assert(!unavailable.IsVisible && decisions.SequenceEqual([false, true]), "skip was not durably requested");
    return Task.CompletedTask;
}

static async Task M4aStorageEncodes()
{
    const string ffmpeg = "/usr/bin/ffmpeg";
    Assert(File.Exists(ffmpeg), "FFmpeg is required for M4A storage parity");
    var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-m4a-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var source = Path.Combine(directory, "recording.wav");
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            ArgumentList = { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.1", source },
        })!) await process.WaitForExitAsync();
        Assert(File.Exists(source), "FFmpeg test WAV was not created");
        var result = await new FfmpegM4aAudioTransformer(() => true, ffmpeg).TransformAsync(source);
        Assert(result.IsSuccess && result.Value!.EndsWith(".m4a", StringComparison.Ordinal), "M4A transform failed");
        var destination = result.Value!;
        Assert(!File.Exists(source) && File.Exists(destination), "successful transform did not atomically replace WAV");
        Assert((File.GetUnixFileMode(destination) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
            "M4A output was readable outside the current user");
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task LatencyOptOutIsScoped()
{
    var sink = new CapturingHttpHandler();
    using var client = new HttpClient(new LinuxLatencyOptOutHandler(() => false, sink));
    _ = await client.GetAsync("https://transcribe-prod-v2.hyperwhisper.com/transcribe");
    _ = await client.GetAsync("https://api.openai.com/v1/audio/transcriptions");
    Assert(sink.OptOutValues.SequenceEqual(["1", null]), "latency opt-out leaked to a direct provider or was omitted");
}

static async Task M4aPlaybackDecodes()
{
    const string ffmpeg = "/usr/bin/ffmpeg";
    var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-m4a-play-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var source = Path.Combine(directory, "recording.m4a");
        using (var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            ArgumentList = { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.1", "-c:a", "aac", source },
        })!) await process.WaitForExitAsync();
        using var playback = new InspectingPlaybackService();
        using var decoding = new FfmpegDecodingAudioPlaybackService(playback, new StaticPaths(directory), ffmpeg);
        var loaded = decoding.Load(source);
        Assert(loaded.IsSuccess && decoding.LoadedFilePath == source, "M4A playback wrapper did not preserve the history path");
        Assert(playback.LoadedPath is not null && Path.GetExtension(playback.LoadedPath) == ".wav"
            && File.Exists(playback.LoadedPath), "M4A playback did not decode a temporary WAV");
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task SileroDetectorStateIsBounded()
{
    var session = new FakeSileroSession();
    using var detector = new SileroVoiceActivityDetector(session);
    var first = await detector.ContainsSpeechAsync(new float[512]);
    var second = await detector.ContainsSpeechAsync(new float[512]);
    Assert(first.IsSuccess && first.Value == false && second.Value == true,
        "Silero threshold or recurrent state was not applied deterministically");
    Assert(session.States.Count == 2 && session.States[0].All(value => value == 0)
        && session.States[1].All(value => value == 1), "Silero state did not advance exactly once per frame");
    await detector.ResetAsync();
    _ = await detector.ContainsSpeechAsync(new float[512]);
    Assert(session.States[^1].All(value => value == 0), "Silero state was not reset between audio files");
    var invalid = await detector.ContainsSpeechAsync(new float[513]);
    Assert(invalid.IsFailure && invalid.Error!.Code == "vad.window_invalid", "oversized Silero frame was accepted");
}

static async Task PackagedSileroExecutes()
{
    var model = Path.Combine(AppContext.BaseDirectory, "parakeet-engine", "silero_vad.onnx");
    Assert(File.Exists(model), "packaged Silero model was not copied to the composition output");
    using var session = new OnnxSileroInferenceSession(model);
    var inference = session.Run(new float[512], new float[256]);
    Assert(inference.IsSuccess && inference.Value is not null,
        $"packaged Silero model did not execute safely: {inference.Error?.Code} {inference.Error?.Message}");
    var output = inference.Value!;
    Assert(output.State.Length == 256
        && float.IsFinite(output.SpeechProbability)
        && output.SpeechProbability is >= 0 and <= 1,
        "packaged Silero model returned invalid output tensor shapes or probability");
    await Task.CompletedTask;
}

static Task DiagnosticCapabilitiesFailClosed()
{
    var unavailable = LinuxDiagnosticCapabilityProbe.Create(new(
        AudioCapture: true,
        Clipboard: true,
        UInput: false,
        CapturedTargetFocus: true,
        GlobalShortcuts: false,
        ScreenCapture: true,
        UsesDesktopPortal: false,
        LocalInference: false,
        Cuda: true));
    Assert(unavailable.AudioCapture && unavailable.Clipboard,
        "independently verified capabilities were lost");
    Assert(!unavailable.TextInjection && !unavailable.GlobalShortcuts,
        "missing injection or shortcut evidence was promoted");
    Assert(!unavailable.PortalScreenCapture,
        "non-portal capture was reported as portal capture");
    Assert(!unavailable.LocalInference && !unavailable.Cuda,
        "CUDA was reported without available local inference");

    var available = LinuxDiagnosticCapabilityProbe.Create(new(
        true, true, true, true, true, true, true, true, true));
    Assert(available.AudioCapture && available.Clipboard && available.GlobalShortcuts
        && available.TextInjection && available.PortalScreenCapture
        && available.LocalInference && available.Cuda,
        "fully evidenced capabilities were not reported");
    return Task.CompletedTask;
}

static async Task LifecycleDiagnosticsAreContentFree()
{
    var events = new List<DiagnosticEvent>();
    var diagnostics = new LinuxLifecycleDiagnostics(
        () => true,
        (value, _) =>
        {
            events.Add(value);
            return Task.FromResult(DiagnosticWriteResult.Ok);
        });
    await diagnostics.ReportAsync(DiagnosticComponent.Transcription, DiagnosticOutcome.Failed);
    Assert(events.Count == 1, "lifecycle event was not written exactly once");
    Assert(events[0].Severity == DiagnosticSeverity.Error
        && events[0].Component == DiagnosticComponent.Transcription
        && events[0].Outcome == DiagnosticOutcome.Failed,
        "fixed lifecycle fields were mapped incorrectly");
    Assert(typeof(DiagnosticEvent).GetProperties().Select(property => property.Name).Order().SequenceEqual(
        new[] { "Component", "Outcome", "Severity", "TimestampUtc" }),
        "diagnostic event contract gained a content-bearing field");

    var disabled = new LinuxLifecycleDiagnostics(
        () => false,
        (_, _) => throw new InvalidOperationException("disabled writer was invoked"));
    await disabled.ReportAsync(DiagnosticComponent.Audio, DiagnosticOutcome.Started);
}

static async Task TypedTrayActionsRouteSafely()
{
    var target = new FakeTrayTarget();
    using var handler = target.CreateHandler();

    Assert((await handler.HandleAsync(StatusNotifierAction.StartRecording)).IsSuccess,
        "safe recording start was rejected");
    Assert(target.Events.SequenceEqual(["start"]), "start action routed incorrectly");
    Assert((await handler.HandleAsync(StatusNotifierAction.StartRecording)).Error?.Code == "tray.unsafe_state",
        "duplicate start was not refused");
    Assert((await handler.HandleAsync(StatusNotifierAction.SelectNextMicrophone)).Error?.Code == "tray.unsafe_state",
        "microphone changed during recording");
    Assert((await handler.HandleAsync(StatusNotifierAction.TranscribeFile)).Error?.Code == "tray.unsafe_state",
        "file import started during recording");
    Assert((await handler.HandleAsync(StatusNotifierAction.CycleMode)).IsSuccess,
        "future mode could not be cycled during recording");
    Assert((await handler.HandleAsync(StatusNotifierAction.StopRecording)).IsSuccess,
        "active recording could not be stopped");

    var immediate = new[]
    {
        StatusNotifierAction.SelectDefaultMicrophone,
        StatusNotifierAction.SelectPreviousMicrophone,
        StatusNotifierAction.SelectNextMicrophone,
        StatusNotifierAction.TranscribeFile,
        StatusNotifierAction.OpenHistory,
        StatusNotifierAction.OpenSettings,
        StatusNotifierAction.OpenHelp,
        StatusNotifierAction.OpenSupport,
        StatusNotifierAction.SendFeedback,
        StatusNotifierAction.Show,
        StatusNotifierAction.Hide,
        StatusNotifierAction.Quit,
    };
    foreach (var action in immediate)
        Assert((await handler.HandleAsync(action)).IsSuccess, $"{action} did not route");

    Assert(target.Events.SequenceEqual([
        "start", "mode", "stop", "mic-default", "mic-previous", "mic-next", "file", "history",
        "settings", "help", "support", "feedback", "show", "hide", "quit"]),
        "typed tray action mapping changed");

    target.Importing = true;
    Assert((await handler.HandleAsync(StatusNotifierAction.StartRecording)).Error?.Code == "tray.unsafe_state",
        "recording overlapped an import");
    target.Importing = false;

    var blockedTarget = new FakeTrayTarget { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    using var blockedHandler = blockedTarget.CreateHandler();
    var pendingStart = blockedHandler.HandleAsync(StatusNotifierAction.StartRecording);
    Assert((await blockedHandler.HandleAsync(StatusNotifierAction.SelectDefaultMicrophone)).Error?.Code == "tray.busy",
        "overlapping state mutation was not refused");
    Assert((await blockedHandler.HandleAsync(StatusNotifierAction.Show)).IsSuccess,
        "window recovery was blocked by a long-running action");
    blockedTarget.StartGate.SetResult();
    Assert((await pendingStart).IsSuccess, "blocked start did not complete");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert((await blockedHandler.HandleAsync(StatusNotifierAction.StopRecording, cancelled.Token)).Error?.Code == "tray.cancelled",
        "pre-cancelled tray action escaped the handler");
    handler.Dispose();
    Assert((await handler.HandleAsync(StatusNotifierAction.Show)).Error?.Code == "tray.disposed",
        "disposed handler accepted an action");
}

static Task TrayMicrophoneSelectionIsDeterministic()
{
    var devices = new[]
    {
        new AudioInputDevice("z-id", "Private microphone name"),
        new AudioInputDevice("a-id", "Another private name"),
        new AudioInputDevice("m-id", "Default private name", true),
    };
    Assert(LinuxTrayMicrophoneSelector.SelectDefault(devices)?.Id == "m-id",
        "default microphone was not selected by stable identity");
    Assert(LinuxTrayMicrophoneSelector.SelectAdjacent(devices, "m-id", 1)?.Id == "z-id",
        "next microphone order was not deterministic");
    Assert(LinuxTrayMicrophoneSelector.SelectAdjacent(devices, "m-id", -1)?.Id == "a-id",
        "previous microphone order was not deterministic");
    Assert(LinuxTrayMicrophoneSelector.SelectAdjacent(devices, "missing", 1)?.Id == "z-id",
        "unknown selection did not recover from the default microphone");
    Assert(LinuxTrayMicrophoneSelector.SelectAdjacent([], null, 1) is null,
        "empty microphone list produced a selection");
    return Task.CompletedTask;
}

static Task ModeCyclingIsDeterministic()
{
    var alpha = new Mode { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Name = "Alpha", SortOrder = 1 };
    var beta = new Mode { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "beta", SortOrder = 1 };
    var first = new Mode { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Last by name", SortOrder = 0 };
    var modes = new[] { beta, first, alpha };
    var activeRecordingModeSnapshot = first;

    Assert(ReferenceEquals(LinuxModeCycler.Next(modes, null), first), "no selection did not choose the first ordered mode");
    Assert(ReferenceEquals(LinuxModeCycler.Next(modes, first), alpha), "sort/name ordering changed");
    Assert(ReferenceEquals(LinuxModeCycler.Next(modes, alpha), beta), "case-insensitive name ordering changed");
    Assert(ReferenceEquals(LinuxModeCycler.Next(modes, beta), first), "mode cycle did not wrap");
    Assert(ReferenceEquals(activeRecordingModeSnapshot, first),
        "cycling a future mode changed the active recording snapshot");
    Assert(ReferenceEquals(LinuxModeCycler.Next(modes, new Mode { Id = Guid.NewGuid() }), first),
        "unknown selection did not recover at the first mode");
    Assert(LinuxModeCycler.Next(Array.Empty<Mode>(), null) is null, "empty mode list produced a selection");
    return Task.CompletedTask;
}

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

static async Task InteractionActionsAreAtomicAndContentFree()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    var configuration = InteractionConfiguration();
    Assert(coordinator.ConfigureAndStart(configuration).IsSuccess, "interaction action registration failed");
    Assert(fixture.Shortcuts.Current.Keys.Order().SequenceEqual(new[]
    {
        LinuxInteractionCoordinator.CancelActionName,
        LinuxInteractionCoordinator.ChangeModeActionName,
        LinuxInteractionCoordinator.ToggleActionName,
    }.Order()), "named interaction actions were not registered atomically");

    var modeChanges = 0;
    coordinator.ChangeModeRequested += (_, args) =>
    {
        Assert(ReferenceEquals(args, EventArgs.Empty), "mode callback exposed input details");
        throw new InvalidOperationException("subscriber isolation");
    };
    coordinator.ChangeModeRequested += (_, _) => modeChanges++;
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ChangeModeActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ChangeModeActionName);
    Assert(modeChanges == 1, "repeated mode key-down was not suppressed");
    fixture.Shortcuts.Release(LinuxInteractionCoordinator.ChangeModeActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.ChangeModeActionName);
    Assert(modeChanges == 2, "released mode shortcut could not be used again");

    fixture.Recording.SetActive(true);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.CancelActionName);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.CancelActionName);
    await UntilAsync(() => fixture.Recording.CancelCount == 1);
    fixture.Shortcuts.EmitRaw("raw-key-material", new(ShortcutModifiers.Meta, new("Q")));
    Assert(modeChanges == 2 && fixture.Recording.CancelCount == 1,
        "unregistered raw input escaped the named-action boundary");
}

static Task UnsafePersistentCancelIsRejected()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    var result = coordinator.ConfigureAndStart(InteractionConfiguration() with
    {
        CancelShortcut = new(ShortcutModifiers.None, new("Escape")),
    });
    Assert(result.IsFailure && result.Error?.Code == "interaction.cancel_shortcut_unsafe",
        "persistent unmodified cancel shortcut was accepted");
    Assert(fixture.Shortcuts.RegistrationCount == 0,
        "unsafe cancel shortcut reached the global registration backend");
    return Task.CompletedTask;
}

static async Task InteractionRegistrationRollback()
{
    var fixture = new InteractionFixture();
    using var coordinator = fixture.Create();
    var original = InteractionConfiguration();
    Assert(coordinator.ConfigureAndStart(original).IsSuccess, "initial interaction registration failed");
    fixture.Shortcuts.FailNextName = LinuxInteractionCoordinator.ChangeModeActionName;
    var replacement = original with
    {
        ToggleShortcut = new(ShortcutModifiers.Control, new("F8")),
        CancelShortcut = new(ShortcutModifiers.Control, new("F9")),
        ChangeModeShortcut = new(ShortcutModifiers.Control, new("F10")),
    };
    var result = coordinator.ConfigureAndStart(replacement);
    Assert(result.IsFailure, "simulated live registration failure was accepted");
    Assert(fixture.Shortcuts.RegistrationCount == 3,
        "failed replacement did not trigger an atomic prior-binding restore");
    Assert(fixture.Shortcuts.Current[LinuxInteractionCoordinator.ToggleActionName] == original.ToggleShortcut
        && fixture.Shortcuts.Current[LinuxInteractionCoordinator.CancelActionName] == original.CancelShortcut
        && fixture.Shortcuts.Current[LinuxInteractionCoordinator.ChangeModeActionName] == original.ChangeModeShortcut,
        "failed replacement left partial shortcuts active");
    fixture.Recording.SetActive(true);
    fixture.Shortcuts.Press(LinuxInteractionCoordinator.CancelActionName);
    await UntilAsync(() => fixture.Recording.CancelCount == 1);
}

static LinuxInteractionConfiguration InteractionConfiguration() => new(
    new(ShortcutModifiers.Control | ShortcutModifiers.Shift, new("Space")),
    new(PushToTalkMode.Disabled),
    TimeSpan.FromMilliseconds(750),
    new(ShortcutModifiers.Control, new("Escape")),
    new(ShortcutModifiers.Control | ShortcutModifiers.Shift, new("Period")));

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
        new FakeTextInjection(), history, new TranscriptionWorkflowRequest());
    Assert(result.Result.IsSuccess, "post-processing exception failed live transcription");
    Assert(history.Updated?.Status == TranscriptStatus.Completed, "history remained Processing");
    Assert(history.Updated?.Text == "Ray raw transcript" && history.Updated.PostProcessedText is null,
        "raw transcript fallback was not persisted");

    var cloudTranscript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var cloudHistory = new FakeHistory(cloudTranscript);
    var cloudMode = new Mode { Name = "Cloud", PostProcessingMode = 1, PostProcessingProvider = "anthropic" };
    var cloudResult = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "  Ray cloud raw transcript  ", cloudTranscript, cloudMode, null, new ThrowingPostProcessor(),
        new FakeTextInjection(), cloudHistory, new TranscriptionWorkflowRequest());
    Assert(cloudResult.Result.IsSuccess && cloudHistory.Updated?.Text == "Ray cloud raw transcript"
        && cloudHistory.Updated.PostProcessedText is null,
        "cloud post-processing failure did not preserve live raw transcript");
}

static async Task LiveOutputProcessingMatchesBatch()
{
    var transcript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var history = new FakeHistory(transcript);
    var injection = new FakeTextInjection();
    var mode = new Mode { Name = "Output", PostProcessingMode = 0, RemoveTrailingPeriod = true };
    var result = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "Um, I think API uses new line hyper whisper.", transcript, mode, null,
        new ThrowingPostProcessor(), injection, history,
        new TranscriptionWorkflowRequest(
            Language: "en",
            SelectedMode: mode,
            VocabularyReplacements: [new("hyper whisper", "HyperWhisper")],
            ModeVocabularyReplacements: [new("uses", "USES")],
            OutputOptions: new SpeechOutputProcessingOptions(
                RemoveFillerWords: true,
                RemoveTrailingPeriod: true,
                AutocapitalizeInsert: true),
            CursorContext: PortableCursorContext.MidSentence));
    Assert(result.Result.IsSuccess && history.Updated?.TranscribedText
        == "Um, I think API uses new line hyper whisper.", "live raw transcript was not preserved");
    Assert(history.Updated?.Text == "I think API USES \n\n HyperWhisper."
        && injection.InjectedText == "I think API USES \n\n HyperWhisper ",
        "live finalization diverged from batch ordered transcript/injection processing");
}

static async Task LiveAutoPasteOffCopiesAndPersists()
{
    var transcript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var history = new FakeHistory(transcript);
    var injection = new FakeTextInjection();
    var mode = new Mode { Name = "Copy", PostProcessingMode = 0, RemoveTrailingPeriod = true };
    var result = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "I’ll use API.", transcript, mode, null, new ThrowingPostProcessor(), injection, history,
        new TranscriptionWorkflowRequest(
            Language: "en",
            SelectedMode: mode,
            OutputOptions: new SpeechOutputProcessingOptions(
                RemoveFillerWords: false,
                RemoveTrailingPeriod: true,
                AutocapitalizeInsert: true),
            PasteResultText: false,
            CursorContext: PortableCursorContext.MidSentence));
    Assert(result.Result.IsSuccess && result.InjectionOutcome == TextInjectionOutcome.CopiedToClipboard,
        "live auto-paste off did not return copied outcome");
    Assert(injection.InjectedText is null && injection.CopiedText == "I’ll use API ",
        "live auto-paste off injected or copied the wrong text");
    Assert(history.Updated?.Text == "I’ll use API." && history.Updated.Status == TranscriptStatus.Completed,
        "live auto-paste off did not persist transcript history");
}

static async Task LiveDeliveredFinalIsNotDuplicated()
{
    var transcript = new Transcript { Status = TranscriptStatus.Processing, Text = "processing" };
    var history = new FakeHistory(transcript);
    var injection = new FakeTextInjection();
    var result = await LinuxLiveTranscriptionFinalizer.FinalizeAndPersistAsync(
        "already pasted", transcript, new Mode { Name = "Live" }, null,
        new ThrowingPostProcessor(), injection, history, new TranscriptionWorkflowRequest(),
        deliveredOutcome: TextInjectionOutcome.Pasted);
    Assert(result.Result.IsSuccess && result.InjectionOutcome == TextInjectionOutcome.Pasted,
        "pre-delivered live outcome was lost");
    Assert(injection.InjectedText is null && injection.CopiedText is null,
        "live final text was injected a second time at stop");
    Assert(history.Updated?.Text == "already pasted", "pre-delivered live transcript was not persisted");
}

static Task LiveSinkForwardsUpdates()
{
    var sink = new LinuxLiveTranscriptSink();
    var updates = new List<LiveTranscriptUpdate>();
    sink.TranscriptReceived += (_, _) => throw new InvalidOperationException("subscriber");
    sink.TranscriptReceived += (_, update) => updates.Add(update);
    sink.OnTranscript(new(" partial ", false));
    sink.OnTranscript(new(" final ", true));
    Assert(updates.SequenceEqual(new[]
    {
        new LiveTranscriptUpdate("partial", false),
        new LiveTranscriptUpdate("final", true),
    }), "production live sink lost or altered transcript update semantics");
    return Task.CompletedTask;
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
    Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider("parakeetLocal", null) == "parakeet-v3",
        "local Parakeet did not receive its production default model");
    Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider("parakeet_local", "parakeet-v2") == "parakeet-v2",
        "local Parakeet selection was not preserved");
    Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider("parakeetLocal", "nova-3-general") == "parakeet-v3",
        "a stale cloud model leaked into local Parakeet");
    Assert(LinuxLiveStreamingSettingsMapper.ModelForProvider("nemotronLocal", "wrong")
        == "nemotron-3.5-ml-560ms", "local Nemotron did not enforce its streaming model");
    return Task.CompletedTask;
}

static async Task LiveEngineRoutingIsIsolated()
{
    var cloud = new RecordingLiveTranscriber("cloud");
    var local = new RecordingLiveTranscriber("local");
    var router = new LinuxRoutingLiveTranscriber(cloud, local);
    _ = await router.TranscribeAsync(new(LiveTranscriptionProvider.Deepgram), EmptyAudio());
    _ = await router.TranscribeAsync(new(LiveTranscriptionProvider.ParakeetLocal), EmptyAudio());
    _ = await router.TranscribeAsync(new(LiveTranscriptionProvider.NemotronLocal), EmptyAudio());
    Assert(cloud.Providers.SequenceEqual([LiveTranscriptionProvider.Deepgram]),
        "cloud live mode reached the local daemon");
    Assert(local.Providers.SequenceEqual([
        LiveTranscriptionProvider.ParakeetLocal, LiveTranscriptionProvider.NemotronLocal]),
        "local live mode reached the network client");
}

static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyAudio()
{
    await Task.CompletedTask;
    yield break;
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
    public IReadOnlyDictionary<string, GlobalShortcut> Current => _registered;
    public int RegistrationCount { get; private set; }
    public string? FailNextName { get; set; }
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;
    public int StartCount { get; private set; }
    public PlatformResult Start() { StartCount++; return PlatformResult.Success(); }
    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(IReadOnlyCollection<NamedShortcut> shortcuts)
    {
        RegistrationCount++;
        _registered = shortcuts
            .Where(value => value.Name != FailNextName)
            .ToDictionary(value => value.Name, value => value.Shortcut);
        var results = shortcuts.ToDictionary(
            value => value.Name,
            value => value.Name == FailNextName
                ? PlatformResult.Failure("shortcut_grab_failed", "The shortcut is already in use.")
                : PlatformResult.Success());
        FailNextName = null;
        return results;
    }
    public void Press(string name) => ShortcutPressed?.Invoke(this, new(name, _registered[name]));
    public void Release(string name) => ShortcutReleased?.Invoke(this, new(name, _registered[name]));
    public void EmitRaw(string name, GlobalShortcut shortcut) => ShortcutPressed?.Invoke(this, new(name, shortcut));
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
    public string? CopiedText { get; private set; }
    public string? InjectedText { get; private set; }
    public bool IsCapturedTargetAvailable => true;
    public void CaptureTarget() => Events.Add("capture");
    public void StartSession() => Events.Add("session-start");
    public void EndSession() => Events.Add("session-end");
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) => Events.Add("restore-scheduled");
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default)
    { Events.Add("restore-now"); return ValueTask.FromResult(PlatformResult.Success()); }
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        CopiedText = text;
        return ValueTask.FromResult(PlatformResult.Success());
    }
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        InjectedText = text;
        return ValueTask.FromResult(TextInjectionOutcome.Pasted);
    }
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
    public bool IsStreaming { get; private set; }
    public ValueTask<PlatformResult> StartAsync(
        InteractionRecordingKind kind,
        CancellationToken cancellationToken = default)
    { StartCount++; IsStreaming = kind == InteractionRecordingKind.Streaming; IsActive = StartResult.IsSuccess; return ValueTask.FromResult(StartResult); }
    public ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default)
    { StopCount++; IsActive = false; if (ThrowOnStop) throw new InvalidOperationException("expected"); return ValueTask.FromResult(StopOutcome); }
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    { CancelCount++; IsActive = false; return ValueTask.CompletedTask; }
    public void SetActive(bool active) => IsActive = active;
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

sealed class FakeTrayTarget
{
    public List<string> Events { get; } = [];
    public bool Active { get; private set; }
    public bool Importing { get; set; }
    public TaskCompletionSource? StartGate { get; init; }

    public LinuxTrayActionHandler CreateHandler() => new(
        () => Active,
        () => Importing,
        async _ =>
        {
            Events.Add("start");
            Active = true;
            if (StartGate is not null) await StartGate.Task;
        },
        _ => { Events.Add("stop"); Active = false; return Task.CompletedTask; },
        _ => { Events.Add("file"); return Task.CompletedTask; },
        () => Events.Add("mic-default"),
        () => Events.Add("mic-previous"),
        () => Events.Add("mic-next"),
        () => Events.Add("mode"),
        () => Events.Add("history"),
        () => Events.Add("settings"),
        () => Events.Add("help"),
        () => Events.Add("support"),
        () => Events.Add("feedback"),
        () => Events.Add("show"),
        () => Events.Add("hide"),
        () => Events.Add("quit"),
        text => text);
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

sealed class CapturingHttpHandler : HttpMessageHandler
{
    public List<string?> OptOutValues { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OptOutValues.Add(request.Headers.TryGetValues(LinuxLatencyOptOutHandler.HeaderName, out var values)
            ? values.Single() : null);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

sealed class InspectingPlaybackService : IAudioPlaybackService
{
    public event EventHandler? PlaybackEnded { add { } remove { } }
    public event EventHandler<TimeSpan>? PositionChanged { add { } remove { } }
    public event EventHandler<TimeSpan>? DurationReady { add { } remove { } }
    public event EventHandler<PlatformError>? PlaybackFailed { add { } remove { } }
    public bool IsPlaying => false;
    public bool IsLoaded => LoadedPath is not null;
    public TimeSpan TotalDuration => TimeSpan.Zero;
    public string? LoadedFilePath => LoadedPath;
    public string? LoadedPath { get; private set; }
    public PlatformResult Load(string audioPath) { LoadedPath = audioPath; return PlatformResult.Success(); }
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public void Dispose() { }
}

sealed class FakeSileroSession : ISileroInferenceSession
{
    public List<float[]> States { get; } = [];
    public PlatformResult<SileroInferenceResult> Run(float[] frame, float[] state)
    {
        States.Add(state.ToArray());
        return PlatformResult<SileroInferenceResult>.Success(new(States.Count == 2 ? 0.75f : 0.25f,
            Enumerable.Repeat(1f, 256).ToArray()));
    }
    public void Dispose() { }
}
