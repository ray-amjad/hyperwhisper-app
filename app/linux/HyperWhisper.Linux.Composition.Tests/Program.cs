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
using HyperWhisper.ModelReadiness;
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
    ("local Whisper timestamps route through portable result", LocalWhisperTimestampsRoute),
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
    ("production batch transcription preserves the full retry envelope", BatchRetryBudgetIsUnbounded),
    ("Silero detector preserves bounded recurrent state", SileroDetectorStateIsBounded),
    ("packaged Silero ONNX model executes silence fixture", PackagedSileroExecutes),
    ("first-run onboarding persists decisions and gates real readiness", OnboardingStateMachine),
    ("onboarding checks secure credentials and installed local models", OnboardingModeReadiness),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length}/{tests.Length} Linux composition tests passed");

static Task BatchRetryBudgetIsUnbounded()
{
    Assert(LinuxModeAwareTranscriptionFactory.BatchRetryBudgetMs == 0,
        "the production Linux batch host silently inherited the 30s interactive retry budget");
    return Task.CompletedTask;
}

static Task OnboardingStateMachine()
{
    var mode = new Mode { Id = Guid.NewGuid(), Name = "Local Whisper", ProviderType = "local", LocalEngine = "whisper" };
    var microphone = new AudioInputDevice("mic-1", "Test microphone", true);
    var decisions = new List<bool>();
    Mode? selectedMode = mode;
    AudioInputDevice? selectedDevice = microphone;
    var onboarding = new LinuxOnboardingViewModel(
        new(true, true, true, true, true, true, false),
        [mode], mode, [microphone], microphone, selectedModeAvailable: true,
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
    onboarding.Next();
    Assert(onboarding.IsVisible && decisions.Count == 0, "onboarding completed without a successful test dictation");
    onboarding.SetTestStatus("complete", succeeded: true);
    onboarding.SelectedDevice = new AudioInputDevice("mic-2", "Second microphone", false);
    Assert(!onboarding.CanGoNext, "changing the selected microphone retained a stale successful test");
    onboarding.SetTestStatus("complete", succeeded: true);
    onboarding.Next();
    Assert(!onboarding.IsVisible && decisions.SequenceEqual([false]), "completion was not durably requested");
    Assert(selectedMode == mode && selectedDevice?.Id == "mic-2", "selections did not reach the live adapters");

    var unavailable = new LinuxOnboardingViewModel(
        new(true, true, false, false, false, true, false),
        [new Mode { Id = Guid.NewGuid(), Name = "Parakeet", ProviderType = "local", LocalEngine = "parakeet" }],
        null, [microphone], microphone, selectedModeAvailable: false,
        skipped => { decisions.Add(skipped); return true; }, _ => { }, _ => { }, key => key);
    unavailable.Show(); unavailable.Next(); unavailable.Next(); unavailable.Next();
    Assert(unavailable.IsProvider && !unavailable.CanGoNext,
        "unavailable local engine incorrectly passed the provider readiness gate");
    unavailable.Skip();
    Assert(!unavailable.IsVisible && decisions.SequenceEqual([false, true]), "skip was not durably requested");
    return Task.CompletedTask;
}

static async Task OnboardingModeReadiness()
{
    var localCapability = new ModelCapability(
        "local/localWhisper/base", "Base", "localWhisper", "base",
        ModelDeployment.Local, ModelWorkload.Voice, ModelSurface.BatchTranscription,
        true, true, [], false, RequiresCredential: false);
    var cloudCapability = new ModelCapability(
        "cloud/stt/openai/whisper-1", "OpenAI", "openai", "whisper-1",
        ModelDeployment.Cloud, ModelWorkload.Voice, ModelSurface.BatchTranscription,
        true, true, [], false, CredentialAccount: "OpenAIApiKey");
    var metaCapability = new ModelCapability(
        "cloud/stt/metaMuse/muse-voice-transcribe-1.0", "Meta Muse", "meta",
        "muse-voice-transcribe-1.0", ModelDeployment.Cloud, ModelWorkload.Voice,
        ModelSurface.BatchTranscription, true, false, [], false,
        CloudTierEligible: true, ByokEligible: true, CredentialAccount: "MetaApiKey");
    var credentials = new OnboardingCredentials(("OpenAIApiKey", "private-test-key"),
        ("LicenseKey", "private-license"), ("MetaApiKey", "private-meta-key"));
    var localModels = new OnboardingLocalModels("base");
    var readiness = new LinuxOnboardingModeReadiness(credentials, localModels,
        [localCapability, cloudCapability, metaCapability]);

    Assert(await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "local", LocalEngine = "whisper", Model = "base",
    }), "installed local model was rejected");
    Assert(!await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "local", LocalEngine = "whisper", Model = "medium",
    }), "missing local model was accepted");
    Assert(await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "cloud", CloudProvider = "openai", CloudTranscriptionModel = "whisper-1",
    }), "credentialed catalog cloud mode was rejected");
    Assert(!await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "cloud", CloudProvider = "openai", CloudTranscriptionModel = "unknown-model",
    }), "unknown cloud model was accepted");
    Assert(await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "cloud", CloudProvider = "hyperwhisper", CloudTranscriptionModel = "scribe_v2",
    }), "credentialed HyperWhisper mode was rejected");
    Assert(await readiness.IsReadyAsync(new Mode
    {
        ProviderType = "cloud", CloudProvider = "meta",
        CloudTranscriptionModel = "muse-voice-transcribe-1.0",
    }), "explicit internal Meta composition could not read the isolated secure key");
    Assert(!await new LinuxOnboardingModeReadiness(
        new OnboardingCredentials(), localModels, [localCapability, cloudCapability]).IsReadyAsync(new Mode
        {
            ProviderType = "cloud", CloudProvider = "openai", CloudTranscriptionModel = "whisper-1",
        }), "credentialless cloud mode was accepted");

    // Linux has no cloud MODEL picker — the Modes screen's model field is free
    // text (MainWindow.axaml, ModeTranscriptionModel) — so what makes a new
    // HyperWhisper Cloud model selectable here is this validator accepting the
    // typed id. Every case above injects synthetic capabilities; this one omits
    // the argument so LinuxOnboardingModeReadiness falls back to
    // UnifiedModelCatalog.LoadBundled() and reads the REAL bundled
    // cloud-stt-catalog.json. Without that a catalog row could go missing and
    // every synthetic case would still pass.
    var bundled = new LinuxOnboardingModeReadiness(credentials, localModels);
    foreach (var azureModel in new[] { "mai-transcribe-2", "mai-transcribe-1.5" })
    {
        Assert(await bundled.IsReadyAsync(new Mode
        {
            ProviderType = "cloud", CloudProvider = "microsoftazurespeech",
            CloudTranscriptionModel = azureModel,
        }), $"the real bundled catalog rejected Azure MAI model '{azureModel}'");
    }
    Assert(!await bundled.IsReadyAsync(new Mode
    {
        ProviderType = "cloud", CloudProvider = "microsoftazurespeech",
        CloudTranscriptionModel = "mai-transcribe-3",
    }), "a model id the catalog does not carry was accepted");
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

// The opt-out header used to be attached by a DelegatingHandler on the shared
// HttpClient, gated on the hardcoded production hostname. It is now built by the
// Rust core from TranscribeParams.shareAnonymousSpeedData, so this test asserts
// both halves of what that gate was reaching for:
//
//   1. an opted-out HyperWhisper Cloud request still carries the header — and,
//      unlike the old handler, it carries it on a NON-production base URL too,
//      which is the bug this replaced;
//   2. the same opted-out user's direct-vendor (OpenAI) request does not. That
//      is no longer a hostname check but a structural property of the core: only
//      build_routed_request emits the header, and no direct-vendor builder calls
//      it. Pinned in Rust by hw-net's hyperwhisper_cloud tests; asserted here
//      because this composition is what the Linux head actually ships.
//
// The third assertion pins the direction of the flag, which is the easy thing to
// get backwards: TRUE means SHARE, and a sharing user sends no header at all.
static async Task LatencyOptOutIsScoped()
{
    var audio = Path.Combine(Path.GetTempPath(), $"hyperwhisper-optout-{Guid.NewGuid():N}.wav");
    await File.WriteAllTextAsync(audio, "RIFF-test-audio");
    try
    {
        var sink = new CapturingHttpHandler();
        // false = the user turned "share anonymous speed data" OFF. TRUE would
        // mean sharing, i.e. no header at all.
        using var service = new CloudTranscriptionService(
            sink,
            new OptOutCredentials(),
            shareAnonymousSpeedData: () => false);

        var cloud = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.HyperWhisperCloud,
            audio,
            string.Empty,
            // Deliberately NOT transcribe-prod-v2.hyperwhisper.com: the deleted
            // handler dropped the user's choice everywhere else.
            BaseUrl: "https://transcribe-staging.hyperwhisper.test"));
        var direct = await service.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.OpenAi,
            audio,
            "whisper-1"));

        Assert(cloud.IsSuccess && direct.IsSuccess, "opt-out fixture did not complete both transcriptions");
        Assert(sink.OptOutValues.SequenceEqual(["1", null]),
            $"latency opt-out leaked to a direct provider or was omitted: [{string.Join(", ", sink.OptOutValues.Select(value => value ?? "(absent)"))}]");

        var sharing = new CapturingHttpHandler();
        using var sharingService = new CloudTranscriptionService(
            sharing,
            new OptOutCredentials(),
            shareAnonymousSpeedData: () => true);
        var shared = await sharingService.TranscribeAsync(new CloudTranscriptionRequest(
            CloudTranscriptionProvider.HyperWhisperCloud,
            audio,
            string.Empty,
            BaseUrl: "https://transcribe-staging.hyperwhisper.test"));
        Assert(shared.IsSuccess && sharing.OptOutValues.SequenceEqual([(string?)null]),
            "a sharing user's request carried the opt-out header — the flag is inverted");
    }
    finally
    {
        File.Delete(audio);
    }
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

static async Task LocalWhisperTimestampsRoute()
{
    using var fixture = new WhisperTranscriberFixture(LocalWhisperBackend.Cpu, allowFallback: true, RuntimeFor(LocalWhisperBackend.Cpu));
    var result = await fixture.Transcriber.TranscribeAsync(
        fixture.AudioPath,
        new TranscriptionWorkflowRequest(Language: "en", StoreWordTimestamps: true));
    Assert(result.IsSuccess && fixture.Service.LastRequest?.IncludeWordTimestamps == true,
        "portable timestamp preference did not reach local Whisper");
    Assert(result.Timestamps is { Segments.Count: 1, Words.Count: 2 }
        && result.Timestamps.RawText == "Ray GPU route"
        && result.Timestamps.Words![0].Word == "Ray",
        "local Whisper timestamps were not mapped into the provider-neutral result");
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

        var local = await adapter.ProcessAsync(
            new PostProcessRequest("raw", null, null, "custom prompt", "localLlm", "local.gguf"),
            CancellationToken.None);
        Assert(processor.Mode is
            { PostProcessingMode: 2, PostProcessingProvider: "local_llm", Preset: "custom", LocalPostProcessingModel: "local.gguf" },
            "local transient prompt/provider/model overrides did not match Windows semantics");
        Assert(local.Model == "local.gguf",
            "a local run with no resolved model did not fall back to the stored GGUF filename");

        // ...but that request sets BOTH `LanguageModel` and `LocalPostProcessingModel`,
        // so it cannot tell the two fallback arms apart. A saved LOCAL mode with no
        // `LanguageModel` at all is the only shape that reaches
        // `?? mode.LocalPostProcessingModel` — without it the response would name
        // `""` for a run that used a real GGUF.
        var localOnly = new Mode
        {
            Name = "Local only", PostProcessingMode = 2,
            PostProcessingProvider = "local_llm", Preset = "hyper",
            LanguageModel = null, LocalPostProcessingModel = "baseline.gguf",
        };
        await repository.UpsertAsync(localOnly);
        var localOnlyResult = await adapter.ProcessAsync(
            new PostProcessRequest("raw", localOnly.Id.ToString("D"), null, null, null, null),
            CancellationToken.None);
        Assert(localOnlyResult.Model == "baseline.gguf",
            "a saved local mode with no LanguageModel did not fall back to its GGUF filename");

        // Issue #314: the model the processor actually RAN wins over the one
        // stored on the working Mode. Without this the response names
        // `gpt-test` — a model that never saw the text — after any fallback or
        // substitution inside the cloud/local post-processors.
        processor.ResolvedModel = "grok-4.3";
        var substituted = await adapter.ProcessAsync(
            new PostProcessRequest("raw", disabled.Id.ToString("D"), "message", null, "openai", "gpt-test", context),
            CancellationToken.None);
        Assert(substituted.Model == "grok-4.3",
            "the resolved model did not win over the model stored on the Mode");

        // A RUN THAT DID NOT NAME ITS MODEL IS STILL A RUN, matching macOS
        // `responseLabels` and Windows `ResponseLabels`. Only NULL — the processor
        // named nothing — falls back to the Mode. A processor that ran and
        // answered blank reports blank: substituting the Mode's stored id there
        // would name `gpt-test` for text this run produced, which is #314 itself.
        processor.ResolvedModel = "   ";
        var blank = await adapter.ProcessAsync(
            new PostProcessRequest("raw", disabled.Id.ToString("D"), "message", null, "openai", "gpt-test", context),
            CancellationToken.None);
        Assert(blank.Model.Length == 0,
            "a run that did not name its model reported the Mode's stored model instead");
        processor.ResolvedModel = null;
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
    public LocalWhisperRequest? LastRequest { get; private set; }
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
        LastRequest = request;
        var timestamps = request.IncludeWordTimestamps
            ? new LocalWhisperTimestamps(
                [new(0, 0, 0.8, "Ray GPU route")],
                [new("Ray", 0, 0.3, 0.95), new("GPU", 0.3, 0.8, 0.9)],
                "Ray GPU route")
            : null;
        return Task.FromResult(LocalWhisperResult.Success("Ray GPU route", runtime, timestamps));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class CapturingPostProcessor : ITranscriptionPostProcessor
{
    public Mode? Mode { get; private set; }
    public ApplicationContextSnapshot? Context { get; private set; }

    /// <summary>
    /// The model this fake claims actually ran (issue #314). Null means "the
    /// processor did not name one", which is the pre-fix behaviour and must
    /// still fall back to the labels stored on the Mode.
    /// </summary>
    public string? ResolvedModel { get; set; }

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
        return Task.FromResult(
            PortablePostProcessingResult.Applied($"processed {transcript}", "test-provider", ResolvedModel));
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
    // Owned by hw-net (`LATENCY_OPT_OUT_HEADER`, providers/hyperwhisper_cloud.rs).
    // Restated here because the constant is not exported over the FFI; a drift
    // fails the Rust tests before it reaches this one.
    internal const string OptOutHeaderName = "X-Latency-Opt-Out";

    public List<string?> OptOutValues { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        OptOutValues.Add(request.Headers.TryGetValues(OptOutHeaderName, out var values)
            ? values.Single() : null);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"text\":\"ok\"}", System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

sealed class OptOutCredentials : ICloudCredentialSource
{
    public ValueTask<CloudCredential?> GetCredentialAsync(
        CloudTranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Both a license key (HyperWhisper Cloud) and an API key (direct vendor),
        // so one source serves both halves of the test.
        return ValueTask.FromResult<CloudCredential?>(
            new CloudCredential("test-api-key", "test-license-key", "test-device"));
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

sealed class OnboardingCredentials(params (string Account, string Value)[] values) : IProviderCredentialSource
{
    private readonly Dictionary<string, string> _values = values.ToDictionary(item => item.Account, item => item.Value);

    public ValueTask<ProviderCredential?> GetCredentialAsync(
        string account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_values.TryGetValue(account, out var value)
            ? new ProviderCredential(value) : null);
    }
}

sealed class OnboardingLocalModels(params string[] installed) : ILocalModelReadinessSource
{
    private readonly HashSet<string> _installed = installed.ToHashSet(StringComparer.Ordinal);

    public ValueTask<bool> IsInstalledAsync(
        ModelCapability model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_installed.Contains(model.ModelId));
    }
}
