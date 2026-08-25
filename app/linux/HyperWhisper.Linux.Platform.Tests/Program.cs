using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Linux.Platform.Security;
using HyperWhisper.Linux.Platform.SystemIntegration;
using HyperWhisper.Platform.Abstractions;

[assembly: SupportedOSPlatform("linux")]

var tests = new (string Name, Func<Task> Run)[]
{
    ("XDG paths honor absolute overrides", XdgOverrides),
    ("XDG paths ignore relative overrides", XdgRelativeFallback),
    ("recordings path honors safe persisted override", RecordingsPathOverride),
    ("recordings path rejects relative and root overrides", RecordingsPathRejectsUnsafe),
    ("recordings directory validator requires private writable path", RecordingsDirectoryValidation),
    ("private writes create exact 0600 files", PrivateFileMode),
    ("private overwrite restores exact 0600", PrivateOverwriteMode),
    ("atomic failure preserves prior contents", AtomicFailurePreservesTarget),
    ("private reads reject permissive files", RejectPermissiveRead),
    ("evdev drops unrelated keys at boundary", DropsUnrelatedKeys),
    ("evdev emits configured logical shortcut", EmitsConfiguredShortcut),
    ("evdev session binding replacement preserves held actions", EvdevSessionBindingReplacement),
    ("evdev disposal is bounded for uncooperative devices", EvdevDisposalBounded),
    ("global shortcut capability probe is content-free and closes sources", ShortcutCapabilityProbe),
    ("X11 mapper preserves logical shortcut privacy", X11ShortcutPrivacy),
    ("X11 modifier-only shortcuts emit press and release", X11ModifierShortcut),
    ("X11 maps multi-modifier-only shortcuts in either order", X11MultiModifierShortcut),
    ("true Xorg selects XGrabKey instead of evdev", XorgSelectsXGrabKey),
    ("X11 XGrabKey host integration", X11GrabIntegration),
    ("X11 Display mutation is serialized with reader", X11ConcurrentMutationIntegration),
    ("StatusNotifierItem action protocol is bounded", StatusNotifierProtocol),
    ("StatusNotifierItem accepts every allowlisted action", StatusNotifierAllowlist),
    ("StatusNotifierItem rejects payload and casing variants", StatusNotifierRejectsPayloads),
    ("StatusNotifierItem bounded reader drains oversized lines", StatusNotifierBoundedReader),
    ("StatusNotifierItem dispatch is typed and subscriber-safe", StatusNotifierDispatch),
    ("StatusNotifierItem preserves legacy window events", StatusNotifierLegacyEvents),
    ("StatusNotifierItem helper exposes only fixed actions", StatusNotifierHelperManifest),
    ("StatusNotifierItem helper disconnect is observable", StatusNotifierDisconnect),
    ("StatusNotifierItem startup cancellation stops helper", StatusNotifierStartupCancellation),
    ("event dispatch isolates failing subscribers", IsolatesSubscribers),
    ("Pulse recorder writes private canonical WAV", PulseRecorderWritesWave),
    ("Pulse recorder reports unavailable capability", PulseRecorderUnavailable),
    ("Pulse playback delegates PCM and ends safely", PulsePlaybackDelegates),
    ("Pulse playback isolates failing subscribers", PulsePlaybackSubscriberSafety),
    ("injection falls back losslessly to clipboard", InjectionClipboardFallback),
    ("injection uses uinput after clipboard", InjectionUsesUInput),
    ("injection restores captured clipboard", InjectionRestoresClipboard),
    ("injection restores every captured MIME format", InjectionRestoresAllFormats),
    ("clipboard privacy policy reaches copy-only and paste paths", ClipboardPrivacyPolicyPropagation),
    ("clipboard privacy policy is safe under concurrent updates", ClipboardPrivacyPolicyConcurrency),
    ("clipboard privacy hint is enabled only by policy", ClipboardPrivacyMimePolicy),
    ("clipboard privacy reports unsupported without native ownership", ClipboardPrivacyUnsupported),
    ("injection refuses a secure field before clipboard mutation", InjectionRefusesSecureField),
    ("injection falls back when captured target is lost", InjectionTargetLost),
    ("injection falls back when target changes before paste", InjectionTargetChanged),
    ("injection propagates cancellation", InjectionCancellation),
    ("disposing injection cancels scheduled restore", InjectionDisposalSafety),
    ("Wayland AT-SPI target accepts stable focused identity", AtSpiTargetStable),
    ("Wayland AT-SPI target rejects changed identity", AtSpiTargetChanged),
    ("AT-SPI insertion context matches Windows terminators", AtSpiInsertionContextClassification),
    ("AT-SPI insertion context rejects content-bearing output", AtSpiInsertionContextPrivacy),
    ("clipboard failure prevents uinput", ClipboardFailurePreventsUInput),
    ("uinput exception preserves clipboard fallback", UInputExceptionFallsBack),
    ("Wayland fallback advertises partial multi-MIME restore", CommandClipboardCapability),
    ("Wayland native owner receives every MIME format", NativeWaylandRestore),
    ("clipboard restore rejects snapshots above 32 MiB", ClipboardSnapshotBound),
    ("native X11 owner receives every MIME format", NativeX11Restore),
    ("native X11 owner serves every MIME format", NativeX11RoundTrip),
    ("XWayland owner bridges text HTML and PNG", XWaylandRoundTrip),
    ("external desktop helpers have a hard timeout", ExternalHelperTimeout),
    ("external desktop helper output is bounded promptly", ExternalHelperOutputBound),
    ("X11 application context parses active window safely", X11ApplicationContext),
    ("Wayland application context uses AT-SPI", WaylandApplicationContext),
    ("GNOME Wayland prefers companion D-Bus", GnomeWaylandApplicationContext),
    ("KDE Wayland prefers KWin companion D-Bus", KdeWaylandApplicationContext),
    ("Application context carries a shared app-type classification", ApplicationContextIsClassified),
    ("An unclassifiable application context keeps the other default", ApplicationContextClassificationDefaults),
    ("Wayland companion falls back explicitly to AT-SPI", WaylandCompanionFallback),
    ("Wayland context reports unsupported without AT-SPI", WaylandContextUnsupported),
    ("active application timeout is explicit", ApplicationContextTimeout),
    ("OCR uses private files, truncates, and cleans up", OcrPrivateCleanup),
    ("OCR capture failure cleans up", OcrCaptureFailureCleanup),
    ("OCR cancellation propagates and cleans up", OcrCancellationCleanup),
    ("OCR exposes portal capture hook capability", OcrPortalCapability),
    ("portal screenshot maps consent outcomes", PortalScreenshotOutcomes),
    ("portal screenshot cancellation propagates", PortalScreenshotCancellation),
    ("Wayland active-app host integration", WaylandApplicationContextIntegration),
    ("portal screenshot host integration", PortalScreenshotIntegration),
    ("Xvfb active-window integration", X11ApplicationContextIntegration),
    ("Linux runtime locator resolves packaged backend variants", RuntimeLocatorVariants),
    ("Linux runtime locator matches published app output", RuntimeLocatorPublishedOutput),
    ("child launcher preserves literal argv", ChildProcessLiteralArgv),
    ("child launcher terminates process tree", ChildProcessTermination),
    ("package update probe is read-only and parses bounded metadata", PackageUpdateProbe),
    ("GPU detector rejects software Vulkan renderer", GpuRejectsSoftwareRenderer),
    ("GPU detector requires CUDA hardware evidence", GpuCudaEvidence),
    ("host GPU evidence never promotes software renderer", HostGpuEvidence),
    ("Pulse input enumeration parses sources and default", PulseInputEnumeration),
    ("streaming audio emits copied chunks safely", StreamingAudioCapture),
    ("streaming audio Stop interrupts a blocked source", StreamingAudioBlockedStop),
    ("private credential fallback is owner-only", PrivateCredentialFallback),
    ("Secret Service keeps credential out of argv", SecretServiceArgvPrivacy),
    ("single-instance socket signals primary safely", SingleInstanceSocket),
    ("XDG autostart is atomic and owner-only", XdgAutostart),
    ("push-to-talk emits only configured logical action", PushToTalkPrivacy),
    ("push-to-talk double-press latches and unlatches", PushToTalkDoubleLock),
    ("push-to-talk hold activation and release are debounced", PushToTalkHoldDebounce),
    ("push-to-talk isolates event subscribers", PushToTalkSubscriberIsolation),
    ("evdev interference is content-free", PushToTalkInterferencePrivacy),
    ("push-to-talk active interference cancels without key data", PushToTalkActiveInterference),
    ("interaction registers cancel and change-mode actions atomically", InteractionActionRegistration),
    ("interaction suppresses repeats and emits content-free mode changes", InteractionActionPrivacy),
    ("streaming startup is cancellable and excludes batch starts", StreamingStartupCancellation),
    ("batch and streaming shortcuts refuse the opposite active kind", InteractionKindMutualExclusion),
    ("interaction accepts unassigned and multi-modifier shortcuts", InteractionFlexibleShortcutValidation),
    ("interaction conflicts and registration failures restore prior bindings", InteractionActionRollback),
    ("interaction restores live X11 grabs after registration failure", InteractionX11Rollback),
    ("bare Escape is armed only for an active recording", SessionCancelLifecycle),
    ("cancel confirmation preserves the active desktop session", SessionCancelConfirmationPreservesSession),
    ("session cancel registration failure restores idle bindings", SessionCancelRegistrationRollback),
    ("session cancel arm racing disposal restores idle bindings", SessionCancelDisposalRace),
    ("X11 session cancel failure restores existing grabs", SessionCancelX11Rollback),
    ("interaction duration limit stops UI and shortcut recordings once", InteractionDurationLimitStopsOnce),
    ("manual completion disarms interaction duration limit", InteractionDurationLimitManualCompletion),
    ("disposing interaction disarms duration limit callback", InteractionDurationLimitDisposal),
    ("device identity hashes machine id internally", DeviceIdentityHashesMachineId),
    ("device identity persists owner-only fallback", DeviceIdentityFallback),
    ("host device identity is privacy-preserving", HostDeviceIdentity),
    ("microphone volume boosts and restores every channel", MicrophoneVolumeRoundTrip),
    ("microphone volume reports pactl unsupported", MicrophoneVolumeUnsupported),
    ("microphone keep-warm suspends and resumes child", MicrophoneKeepWarmLifecycle),
    ("sound effects expose unsupported and safe success", SoundEffectsPaths),
    ("audio environment mute restores exact prior state", AudioEnvironmentMuteRestore),
    ("audio environment unchanged requires no backend", AudioEnvironmentUnchanged),
    ("audio environment duck restores channel volumes", AudioEnvironmentDuckRestore),
    ("UI dispatcher posts and invokes through context", UiDispatcherContext),
    ("UI dispatcher cancellation skips queued work", UiDispatcherCancellation),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static Task XdgOverrides()
{
    var paths = new LinuxAppPaths(
        new FakeEnvironment("/users/test", new Dictionary<string, string>
        {
            ["XDG_DATA_HOME"] = "/xdg/data",
            ["XDG_CONFIG_HOME"] = "/xdg/config",
            ["XDG_CACHE_HOME"] = "/xdg/cache",
            ["XDG_STATE_HOME"] = "/xdg/state",
            ["XDG_RUNTIME_DIR"] = "/run/user/42",
            ["TMPDIR"] = "/var/tmp",
        }),
        new FakeUser(42));

    Assert.Equal("/xdg/data/hyperwhisper", paths.DataDirectory);
    Assert.Equal("/xdg/config/hyperwhisper", paths.ConfigDirectory);
    Assert.Equal("/xdg/cache/hyperwhisper", paths.CacheDirectory);
    Assert.Equal("/xdg/state/hyperwhisper/logs", paths.LogsDirectory);
    Assert.Equal("/run/user/42/hyperwhisper", paths.RuntimeDirectory);
    Assert.Equal("/var/tmp/hyperwhisper-42", paths.TemporaryDirectory);
    return Task.CompletedTask;
}

static Task XdgRelativeFallback()
{
    var paths = new LinuxAppPaths(
        new FakeEnvironment("/users/test", new Dictionary<string, string>
        {
            ["XDG_DATA_HOME"] = "relative/data",
            ["XDG_RUNTIME_DIR"] = "relative/runtime",
        }),
        new FakeUser(7));

    Assert.Equal("/users/test/.local/share/hyperwhisper", paths.DataDirectory);
    Assert.Equal("/users/test/.local/share/hyperwhisper/runtime", paths.RuntimeDirectory);
    return Task.CompletedTask;
}

static Task RecordingsPathOverride() => WithTemporaryDirectory(directory =>
{
    var configHome = Path.Combine(directory, "config");
    var custom = Path.Combine(directory, "voice");
    var configDirectory = Path.Combine(configHome, "hyperwhisper");
    Directory.CreateDirectory(configDirectory);
    File.WriteAllText(Path.Combine(configDirectory, "settings.json"),
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["storage.recordingsDirectory"] = custom,
        }));
    var paths = new LinuxAppPaths(
        new FakeEnvironment(directory, new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = configHome }),
        new FakeUser(9));
    Assert.Equal(custom, paths.RecordingsDirectory);
});

static Task RecordingsPathRejectsUnsafe() => WithTemporaryDirectory(directory =>
{
    var configHome = Path.Combine(directory, "config");
    var configDirectory = Path.Combine(configHome, "hyperwhisper");
    Directory.CreateDirectory(configDirectory);
    var settings = Path.Combine(configDirectory, "settings.json");
    File.WriteAllText(settings, "{\"storage.recordingsDirectory\":\"relative\"}");
    var environment = new FakeEnvironment(directory, new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = configHome });
    var paths = new LinuxAppPaths(environment, new FakeUser(9));
    Assert.Equal(Path.Combine(directory, ".local/share/hyperwhisper/recordings"), paths.RecordingsDirectory);
    File.WriteAllText(settings, "{\"storage.recordingsDirectory\":\"/\"}");
    paths = new LinuxAppPaths(environment, new FakeUser(9));
    Assert.Equal(Path.Combine(directory, ".local/share/hyperwhisper/recordings"), paths.RecordingsDirectory);
});

static Task RecordingsDirectoryValidation() => WithTemporaryDirectory(directory =>
{
    Assert.Equal("storage.path_relative", LinuxRecordingDirectoryValidator.ValidateAndPrepare("relative").Error?.Code);
    Assert.Equal("storage.path_root", LinuxRecordingDirectoryValidator.ValidateAndPrepare("/").Error?.Code);
    var target = Path.Combine(directory, "private-recordings");
    var valid = LinuxRecordingDirectoryValidator.ValidateAndPrepare(target);
    Assert.True(valid.IsSuccess);
    Assert.Equal(target, valid.Value);
    Assert.True(Directory.Exists(target));
    Assert.True(!Directory.EnumerateFileSystemEntries(target).Any());
});

static Task PrivateFileMode() => WithTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "token");
    var service = new LinuxPrivateFileService();
    Assert.Success(service.WriteAllTextAtomically(path, "secret"));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    Assert.Equal("secret", service.ReadAllText(path).Value);
});

static Task PrivateOverwriteMode() => WithTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "token");
    File.WriteAllText(path, "old");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    var service = new LinuxPrivateFileService();
    Assert.Success(service.WriteAllTextAtomically(path, "new"));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    Assert.Equal("new", File.ReadAllText(path));
});

static Task AtomicFailurePreservesTarget() => WithTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "token");
    File.WriteAllText(path, "old");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    var service = new LinuxPrivateFileService(new UnixPrivateFilePermissions(), new FailingReplace());
    var result = service.WriteAllTextAtomically(path, "new");
    Assert.True(result.IsFailure);
    Assert.Equal("old", File.ReadAllText(path));
    Assert.Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
});

static Task RejectPermissiveRead() => WithTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "token");
    File.WriteAllText(path, "secret");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    var result = new LinuxPrivateFileService().ReadAllText(path);
    Assert.True(result.IsFailure);
    Assert.Equal("private_file_insecure", result.Error!.Code);
});

static async Task DropsUnrelatedKeys()
{
    var source = new FakeSource("keyboard", Frame(48, 1), Frame(48, 0));
    using var service = new LinuxGlobalShortcutService(new FakeSourceFactory(source), null);
    service.RegisterShortcuts([new NamedShortcut("record", new GlobalShortcut(ShortcutModifiers.Control, new ShortcutKeyCode("A")))]);
    var count = 0;
    service.ShortcutPressed += (_, _) => count++;
    Assert.Success(service.Start());
    await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(0, count);
}

static async Task EmitsConfiguredShortcut()
{
    var source = new FakeSource("keyboard", Frame(29, 1), Frame(30, 1), Frame(30, 0));
    using var service = new LinuxGlobalShortcutService(new FakeSourceFactory(source), null);
    service.RegisterShortcuts([new NamedShortcut("record", new GlobalShortcut(ShortcutModifiers.Control, new ShortcutKeyCode("A")))]);
    var events = new List<string>();
    service.ShortcutPressed += (_, args) => events.Add($"down:{args.Name}");
    service.ShortcutReleased += (_, args) => events.Add($"up:{args.Name}");
    Assert.Success(service.Start());
    await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal("down:record,up:record", string.Join(',', events));
}

static Task EvdevSessionBindingReplacement()
{
    var toggle = EvdevShortcutMapper.Map(new NamedShortcut(
        LinuxInteractionCoordinator.ToggleActionName,
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new("Space")))).Value!;
    var escape = EvdevShortcutMapper.Map(new NamedShortcut(
        LinuxInteractionCoordinator.SessionCancelActionName,
        new GlobalShortcut(ShortcutModifiers.None, new("Escape")))).Value!;
    var filter = new EvdevShortcutFilter();
    filter.ReplaceBindings([toggle]);
    _ = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 29, 1));
    _ = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 42, 1));
    var pressed = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 57, 1));
    Assert.Equal(LinuxInteractionCoordinator.ToggleActionName, pressed.Signals.Single().Shortcut.Name);

    filter.ReplaceBindings([toggle, escape]);
    var released = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 57, 0));
    Assert.True(!released.Signals.Single().Pressed);
    var cancelled = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 1, 1));
    Assert.Equal(LinuxInteractionCoordinator.SessionCancelActionName, cancelled.Signals.Single().Shortcut.Name);

    filter.ReplaceBindings([toggle]);
    filter.ReplaceBindings([toggle, escape]);
    var unrelatedRelease = filter.Process("keyboard", new EvdevEvent(EvdevEvent.KeyType, 29, 0));
    Assert.True(unrelatedRelease.Signals.All(signal => signal.Shortcut.Name != LinuxInteractionCoordinator.SessionCancelActionName));
    return Task.CompletedTask;
}

static Task EvdevDisposalBounded()
{
    var source = new UncooperativeSource();
    var service = new LinuxGlobalShortcutService(new FakeSourceFactory(source), null);
    Assert.Success(service.Start());
    var started = Stopwatch.StartNew();
    service.Dispose();
    Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    source.Release();
    return Task.CompletedTask;
}

static Task ShortcutCapabilityProbe()
{
    var source = new ProbeSource();
    using var service = new LinuxGlobalShortcutService(new FakeSourceFactory(source), null);
    var capability = service.GetCapabilities();
    Assert.True(capability.Available);
    Assert.Equal("evdev", capability.Backend);
    Assert.True(source.Disposed);
    Assert.Equal(0, source.ReadCount);
    return Task.CompletedTask;
}

static async Task IsolatesSubscribers()
{
    var diagnostics = new FakeDiagnostics();
    var source = new FakeSource("keyboard", Frame(30, 1));
    using var service = new LinuxGlobalShortcutService(new FakeSourceFactory(source), diagnostics);
    service.RegisterShortcuts([new NamedShortcut("record", new GlobalShortcut(ShortcutModifiers.None, new ShortcutKeyCode("A")))]);
    var reached = false;
    service.ShortcutPressed += (_, _) => throw new InvalidOperationException("subscriber");
    service.ShortcutPressed += (_, _) => reached = true;
    Assert.Success(service.Start());
    await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(reached);
    Assert.Equal(1, diagnostics.SubscriberFailures);
}

static async Task X11ShortcutPrivacy()
{
    var connection = new FakeX11Connection(new X11HotkeyEvent(99, 0, true),
        new X11HotkeyEvent(38, 4, true), new X11HotkeyEvent(38, 4, false));
    using var service = new X11GlobalShortcutService(new FakeX11Factory(connection));
    var events = new List<string>();
    service.ShortcutPressed += (_, args) => events.Add("down:" + args.Name);
    service.ShortcutReleased += (_, args) => events.Add("up:" + args.Name);
    var result = service.RegisterShortcuts([new NamedShortcut("record", new(ShortcutModifiers.Control, new("A")))]);
    Assert.Success(result["record"]); Assert.Success(service.Start());
    await connection.Drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Task.Delay(30);
    Assert.Equal("down:record,up:record", string.Join(',', events));
    Assert.Equal(4, connection.Grabs.Count);
}

static async Task X11ModifierShortcut()
{
    var connection = new FakeX11Connection(new X11HotkeyEvent(37, 0, true), new X11HotkeyEvent(37, 4, false));
    using var service = new X11GlobalShortcutService(new FakeX11Factory(connection));
    var events = 0;
    service.ShortcutPressed += (_, _) => events++;
    service.ShortcutReleased += (_, _) => events++;
    service.RegisterShortcuts([new NamedShortcut("ptt", new(ShortcutModifiers.Control))]);
    Assert.Success(service.Start());
    await connection.Drained.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(30);
    Assert.Equal(2, events);
    Assert.Equal(8, connection.Grabs.Count);
    Assert.True(connection.Grabs.All(value => value.Modifiers is 0 or 2 or 16 or 18));
}

static Task X11MultiModifierShortcut()
{
    var mapped = X11ShortcutMapper.Map(new NamedShortcut("toggle",
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt)));
    Assert.True(mapped.IsSuccess);
    Assert.Equal(4, mapped.Value!.Triggers.Count);
    Assert.True(mapped.Value.Triggers.Count(trigger => trigger.Modifiers == 4) == 2);
    Assert.True(mapped.Value.Triggers.Count(trigger => trigger.Modifiers == 8) == 2);
    return Task.CompletedTask;
}

static Task XorgSelectsXGrabKey()
{
    var x11 = new FakeGlobalShortcutService();
    var source = new FakeSourceFactory();
    using var service = new LinuxGlobalShortcutService(source, null, x11);
    service.RegisterShortcuts([new NamedShortcut("record", new(ShortcutModifiers.None, new("A")))]);
    Assert.Success(service.Start());
    Assert.Equal(1, x11.StartCalls);
    Assert.Equal(0, source.OpenCalls);
    return Task.CompletedTask;
}

static Task StatusNotifierProtocol()
{
    Assert.Equal(StatusNotifierMessage.Available, LinuxStatusNotifierItemService.ParseMessage("CAPABILITY|available"));
    Assert.Equal(StatusNotifierMessage.Unsupported, LinuxStatusNotifierItemService.ParseMessage("CAPABILITY|unsupported"));
    Assert.Equal(StatusNotifierMessage.Show, LinuxStatusNotifierItemService.ParseMessage("ACTION|show"));
    Assert.Equal(StatusNotifierMessage.Hide, LinuxStatusNotifierItemService.ParseMessage("ACTION|hide"));
    Assert.Equal(StatusNotifierMessage.Quit, LinuxStatusNotifierItemService.ParseMessage("ACTION|quit"));
    Assert.Equal(StatusNotifierMessage.Unknown, LinuxStatusNotifierItemService.ParseMessage("ACTION|keystroke:secret"));
    return Task.CompletedTask;
}

static Task StatusNotifierAllowlist()
{
    var cases = new Dictionary<string, StatusNotifierMessage>(StringComparer.Ordinal)
    {
        ["record-start"] = StatusNotifierMessage.StartRecording,
        ["record-stop"] = StatusNotifierMessage.StopRecording,
        ["microphone-default"] = StatusNotifierMessage.SelectDefaultMicrophone,
        ["microphone-previous"] = StatusNotifierMessage.SelectPreviousMicrophone,
        ["microphone-next"] = StatusNotifierMessage.SelectNextMicrophone,
        ["mode-cycle"] = StatusNotifierMessage.CycleMode,
        ["transcribe-file"] = StatusNotifierMessage.TranscribeFile,
        ["history"] = StatusNotifierMessage.OpenHistory,
        ["settings"] = StatusNotifierMessage.OpenSettings,
        ["help"] = StatusNotifierMessage.OpenHelp,
        ["support"] = StatusNotifierMessage.OpenSupport,
        ["feedback"] = StatusNotifierMessage.SendFeedback,
        ["show"] = StatusNotifierMessage.Show,
        ["hide"] = StatusNotifierMessage.Hide,
        ["quit"] = StatusNotifierMessage.Quit,
    };
    foreach (var item in cases)
        Assert.Equal(item.Value, LinuxStatusNotifierItemService.ParseMessage("ACTION|" + item.Key));
    Assert.Equal(Enum.GetValues<StatusNotifierAction>().Length, cases.Count);
    return Task.CompletedTask;
}

static Task StatusNotifierRejectsPayloads()
{
    string?[] rejected =
    [
        null, "", " ", "ACTION|", "action|show", "ACTION|SHOW", "ACTION|show ", " ACTION|show",
        "ACTION|show|extra", "ACTION|microphone-next:device", "ACTION|history/path", "ACTION|settings?key=value",
        "ACTION|record-start\0", "ACTION|quit\t", "ACTION|help\r", "ACTION|support\n",
        "ACTION|transcript", "ACTION|credential", "ACTION|keystroke:secret", "EVENT|show",
        new string('A', LinuxStatusNotifierItemService.MaximumProtocolLineLength + 1)
    ];
    foreach (var value in rejected)
        Assert.Equal(StatusNotifierMessage.Unknown, LinuxStatusNotifierItemService.ParseMessage(value));

    Assert.Equal(StatusNotifierMessage.Unsupported, LinuxStatusNotifierItemService.ParseMessage("CAPABILITY|future"));
    return Task.CompletedTask;
}

static async Task StatusNotifierBoundedReader()
{
    var exact = new string('a', LinuxStatusNotifierItemService.MaximumProtocolLineLength);
    using var reader = new StringReader(exact + "\n" + new string('b', 4096) + "\nACTION|show\n");
    Assert.Equal(exact, await LinuxStatusNotifierItemService.ReadBoundedLineAsync(reader));
    Assert.Equal(string.Empty, await LinuxStatusNotifierItemService.ReadBoundedLineAsync(reader));
    Assert.Equal("ACTION|show", await LinuxStatusNotifierItemService.ReadBoundedLineAsync(reader));
    Assert.Equal<string?>(null, await LinuxStatusNotifierItemService.ReadBoundedLineAsync(reader));
}

static Task StatusNotifierDispatch()
{
    using var service = new LinuxStatusNotifierItemService(null, null);
    var actions = new List<StatusNotifierAction>();
    service.ActionRequested += (_, _) => throw new InvalidOperationException("subscriber");
    service.ActionRequested += (_, args) => actions.Add(args.Action);
    foreach (var message in Enum.GetValues<StatusNotifierMessage>()) service.Dispatch(message);
    Assert.Equal(Enum.GetValues<StatusNotifierAction>().Length, actions.Count);
    Assert.Equal(string.Join(',', Enum.GetValues<StatusNotifierAction>()), string.Join(',', actions));
    return Task.CompletedTask;
}

static Task StatusNotifierLegacyEvents()
{
    using var service = new LinuxStatusNotifierItemService(null, null);
    var show = 0; var hide = 0; var quit = 0; var typed = 0;
    service.ShowRequested += (_, _) => show++;
    service.HideRequested += (_, _) => hide++;
    service.QuitRequested += (_, _) => quit++;
    service.ActionRequested += (_, _) => typed++;
    service.Dispatch(StatusNotifierMessage.Show);
    service.Dispatch(StatusNotifierMessage.Hide);
    service.Dispatch(StatusNotifierMessage.Quit);
    service.Dispatch(StatusNotifierMessage.Unknown);
    Assert.Equal(1, show); Assert.Equal(1, hide); Assert.Equal(1, quit); Assert.Equal(3, typed);
    return Task.CompletedTask;
}

static Task StatusNotifierHelperManifest()
{
    var sourcePath = Path.Combine(AppContext.BaseDirectory, "DesktopCompanions", "status-notifier.py");
    var source = File.ReadAllText(sourcePath);
    string[] actions =
    [
        "record-start", "record-stop", "microphone-default", "microphone-previous", "microphone-next",
        "mode-cycle", "transcribe-file", "history", "settings", "help", "support", "feedback", "show", "hide", "quit"
    ];
    foreach (var action in actions) Assert.True(source.Contains("\"" + action + "\"", StringComparison.Ordinal));
    Assert.True(source.Contains("if action in ACTIONS.values()", StringComparison.Ordinal));
    Assert.True(!source.Contains("_data)", StringComparison.Ordinal));
    Assert.True(!source.Contains("print(_data", StringComparison.Ordinal));
    Assert.True(!source.Contains("device_name", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static async Task StatusNotifierDisconnect()
{
    await WithTemporaryDirectoryAsync(async directory =>
    {
        var script = Path.Combine(directory, "helper.sh");
        File.WriteAllText(script, "printf 'CAPABILITY|available\\nACTION|history\\n'\n");
        var prior = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "test:tray");
        try
        {
            using var service = new LinuxStatusNotifierItemService("/bin/sh", script);
            var action = new TaskCompletionSource<StatusNotifierAction>(TaskCreationOptions.RunContinuationsAsynchronously);
            var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ActionRequested += (_, args) => action.TrySetResult(args.Action);
            service.Unavailable += (_, _) => unavailable.TrySetResult();
            Assert.Success(await service.StartAsync());
            Assert.Equal(StatusNotifierAction.OpenHistory, await action.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", prior); }
    });
}

static async Task StatusNotifierStartupCancellation()
{
    await WithTemporaryDirectoryAsync(async directory =>
    {
        var script = Path.Combine(directory, "helper.sh");
        File.WriteAllText(script, "sleep 30\n");
        var prior = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "test:tray");
        try
        {
            using var service = new LinuxStatusNotifierItemService("/bin/sh", script);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.StartAsync(cancellation.Token));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally { Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", prior); }
    });
}

static async Task X11GrabIntegration()
{
    if (Environment.GetEnvironmentVariable("HW_RUN_X11_GRAB_TEST") != "1") return;
    using var service = new X11GlobalShortcutService();
    var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    service.ShortcutPressed += (_, args) => { if (args.Name == "integration") pressed.TrySetResult(); };
    service.ShortcutReleased += (_, args) => { if (args.Name == "integration") released.TrySetResult(); };
    var registered = service.RegisterShortcuts([new NamedShortcut("integration", new(ShortcutModifiers.None, new("A")))]);
    Assert.Success(registered["integration"]); Assert.Success(service.Start());
    using var window = Process.Start(new ProcessStartInfo("xmessage", "-title hw-xgrab-test -buttons ok -timeout 5 test") { UseShellExecute = false });
    if (window is null) throw new InvalidOperationException("X11 test window failed to start");
    try
    {
        await Task.Delay(150);
        var search = new ProcessStartInfo("xdotool", "search --name hw-xgrab-test") { UseShellExecute = false, RedirectStandardOutput = true };
        using var finder = Process.Start(search);
        if (finder is null) throw new InvalidOperationException("xdotool search failed to start");
        var id = (await finder.StandardOutput.ReadToEndAsync()).Trim().Split('\n')[0];
        await finder.WaitForExitAsync(); Assert.Equal(0, finder.ExitCode);
        using var process = Process.Start(new ProcessStartInfo("xdotool", $"windowfocus {id} key a") { UseShellExecute = false });
        if (process is null) throw new InvalidOperationException("xdotool failed to start");
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(2)); await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    finally
    {
        if (!window.HasExited) window.Kill(entireProcessTree: true);
        await window.WaitForExitAsync();
        service.Dispose();
        // Let Xvfb process the final client close before the next host test
        // opens a fresh Display. Real desktop servers are long-lived, but the
        // in-process test sequence otherwise exposes an Xvfb teardown race.
        await Task.Delay(100);
    }
}

static async Task X11ConcurrentMutationIntegration()
{
    if (Environment.GetEnvironmentVariable("HW_RUN_X11_GRAB_TEST") != "1") return;
    using var service = new X11GlobalShortcutService();
    service.RegisterShortcuts([new NamedShortcut("concurrent", new(ShortcutModifiers.None, new("B")))]);
    Assert.Success(service.Start());
    await Task.Run(() =>
    {
        for (var index = 0; index < 100; index++)
        {
            var result = service.RegisterShortcuts([new NamedShortcut("concurrent", new(ShortcutModifiers.None, new("B")))]);
            Assert.Success(result["concurrent"]);
            if (index % 5 == 0) service.ResetKeyboardState();
        }
    });
}

static async Task PulseRecorderWritesWave()
{
    await WithTemporaryDirectoryAsync(async directory =>
    {
        var session = new FakeRecordSession([1, 0, 2, 0]);
        var api = new FakePulseApi { RecordSession = session };
        using var recorder = new PulseAudioRecorder(api, new FakeAppPaths(directory));
        var levels = 0;
        recorder.AudioLevelChanged += (_, _) => levels++;
        Assert.Success(recorder.Start(new AudioRecordingOptions("default")));
        await session.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = recorder.Stop();
        Assert.True(stopped.IsSuccess);
        Assert.Equal(48L, new FileInfo(stopped.Value!).Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stopped.Value!));
        Assert.Equal(1, levels);
    });
}

static Task PulseRecorderUnavailable()
{
    using var recorder = new PulseAudioRecorder(
        new FakePulseApi { Available = false },
        new FakeAppPaths("/tmp/not-used"));
    var result = recorder.Start(new AudioRecordingOptions("default"));
    Assert.True(result.IsFailure);
    Assert.Equal("pulse_unavailable", result.Error!.Code);
    return Task.CompletedTask;
}

static async Task PulsePlaybackDelegates()
{
    await WithTemporaryDirectoryAsync(async directory =>
    {
        var path = Path.Combine(directory, "sample.wav");
        using (var stream = File.Create(path))
        {
            WaveFile.WriteHeader(stream, new WaveFormat(16_000, 16, 1), 4);
            stream.Position = WaveFile.HeaderSize;
            stream.Write([1, 0, 2, 0]);
        }
        var playback = new FakePlaybackSession();
        using var service = new PulseAudioPlaybackService(new FakePulseApi { PlaybackSession = playback });
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PlaybackEnded += (_, _) => ended.TrySetResult();
        Assert.Success(service.Load(path));
        service.Play();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, playback.BytesWritten);
        Assert.Equal(1, playback.DrainCalls);
        var endedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PlaybackEnded += (_, _) => endedAgain.TrySetResult();
        service.Play();
        await endedAgain.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(8, playback.BytesWritten);
        Assert.Equal(2, playback.DrainCalls);
        var invalid = Path.Combine(directory, "invalid.wav");
        await File.WriteAllBytesAsync(invalid, [1, 2, 3]);
        Assert.True(service.Load(invalid).IsFailure);
        Assert.True(!service.IsLoaded);
        Assert.True(service.LoadedFilePath is null);
    });
}

static async Task PulsePlaybackSubscriberSafety()
{
    await WithTemporaryDirectoryAsync(async directory =>
    {
        var path = Path.Combine(directory, "sample.wav");
        using (var stream = File.Create(path))
        {
            WaveFile.WriteHeader(stream, new WaveFormat(16_000, 16, 1), 2);
            stream.Position = WaveFile.HeaderSize;
            stream.Write([1, 0]);
        }
        using var service = new PulseAudioPlaybackService(new FakePulseApi { PlaybackSession = new FakePlaybackSession() });
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PlaybackEnded += (_, _) => throw new InvalidOperationException("subscriber");
        service.PlaybackEnded += (_, _) => reached.TrySetResult();
        Assert.Success(service.Load(path));
        service.Play();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
    });
}

static async Task InjectionClipboardFallback()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(false);
    using var service = NewInjection(clipboard, uinput);
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal("transcript", clipboard.Text);
    Assert.Equal(1, uinput.PasteCalls);
}

static async Task InjectionUsesUInput()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(true);
    using var service = NewInjection(clipboard, uinput);
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.Pasted, outcome);
    Assert.Equal("transcript", clipboard.Text);
    Assert.Equal(1, uinput.PasteCalls);
}

static async Task InjectionRestoresClipboard()
{
    var clipboard = new FakeClipboard("old");
    using var service = NewInjection(clipboard, new FakeUInput(false));
    service.StartSession();
    await service.CopyToClipboardAsync("transcript");
    Assert.Success(await service.RestoreClipboardImmediatelyAsync());
    Assert.Equal("old", clipboard.Text);
}

static async Task InjectionRestoresAllFormats()
{
    var formats = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain;charset=utf-8"] = "old"u8.ToArray(),
        ["image/png"] = [0x89, 0x50, 0x4e, 0x47],
    };
    var clipboard = new FakeClipboard(formats);
    using var service = NewInjection(clipboard, new FakeUInput(false));
    service.StartSession();
    await service.CopyToClipboardAsync("transcript");
    Assert.Success(await service.RestoreClipboardImmediatelyAsync());
    Assert.Equal(2, clipboard.Formats.Count);
    Assert.SequenceEqual(formats["text/plain;charset=utf-8"], clipboard.Formats["text/plain;charset=utf-8"]);
    Assert.SequenceEqual(formats["image/png"], clipboard.Formats["image/png"]);
}

static async Task ClipboardPrivacyPolicyPropagation()
{
    var clipboard = new FakeClipboard("old");
    using var service = NewInjection(clipboard, new FakeUInput(true));
    service.SetClipboardHistoryPrivacyPolicy(ClipboardHistoryPrivacyPolicy.BestEffort);
    service.StartSession();
    Assert.Success(await service.CopyToClipboardAsync("copy-only"));
    Assert.Equal(ClipboardHistoryPrivacyPolicy.BestEffort, clipboard.LastPrivacyPolicy);
    service.CaptureTarget();
    Assert.Equal(TextInjectionOutcome.Pasted, await service.InjectTranscriptAsync("paste"));
    Assert.Equal(ClipboardHistoryPrivacyPolicy.BestEffort, clipboard.LastPrivacyPolicy);
    Assert.Success(await service.RestoreClipboardImmediatelyAsync());
    Assert.Equal("old", clipboard.Text);
    Assert.Equal(1, clipboard.Formats.Count);
}

static async Task ClipboardPrivacyPolicyConcurrency()
{
    var clipboard = new FakeClipboard("old");
    using var service = NewInjection(clipboard, new FakeUInput(false));
    var updates = Enumerable.Range(0, 200).Select(index => Task.Run(() =>
        service.SetClipboardHistoryPrivacyPolicy(index % 2 == 0
            ? ClipboardHistoryPrivacyPolicy.Disabled
            : ClipboardHistoryPrivacyPolicy.BestEffort)));
    await Task.WhenAll(updates);
    Assert.Success(await service.CopyToClipboardAsync("transcript"));
    Assert.True(clipboard.LastPrivacyPolicy is ClipboardHistoryPrivacyPolicy.Disabled
        or ClipboardHistoryPrivacyPolicy.BestEffort);
}

static async Task ClipboardPrivacyMimePolicy()
{
    var owner = new FakeNativeClipboardOwner();
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", false, owner);
    Assert.Equal(ClipboardHistoryPrivacyCapability.BestEffortAvailable,
        backend.GetCapabilities().ClipboardHistoryPrivacy);
    Assert.Success(await backend.SetTextAsync("private transcript", ClipboardHistoryPrivacyPolicy.BestEffort,
        CancellationToken.None));
    Assert.Equal(2, owner.Owned!.Formats.Count);
    Assert.SequenceEqual("private transcript"u8.ToArray(), owner.Owned.Formats["text/plain;charset=utf-8"]);
    Assert.SequenceEqual("secret"u8.ToArray(), owner.Owned.Formats["x-kde-passwordManagerHint"]);

    owner.Clear();
    _ = await backend.SetTextAsync("ordinary transcript", ClipboardHistoryPrivacyPolicy.Disabled,
        CancellationToken.None);
    Assert.True(owner.Owned is null);
}

static Task ClipboardPrivacyUnsupported()
{
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", true, null);
    Assert.Equal(ClipboardHistoryPrivacyCapability.Unsupported,
        backend.GetCapabilities().ClipboardHistoryPrivacy);
    ITextInjectionService fake = new FakeInteractionTextInjection();
    Assert.Equal(ClipboardHistoryPrivacyCapability.Unsupported, fake.ClipboardHistoryPrivacyCapability);
    fake.SetClipboardHistoryPrivacyPolicy(ClipboardHistoryPrivacyPolicy.BestEffort);
    return Task.CompletedTask;
}

static async Task InjectionRefusesSecureField()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(true);
    using var service = NewInjection(clipboard, uinput, new FakeSecureFieldGuard(SecureFieldState.Secure));
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("secret transcript");
    Assert.Equal(TextInjectionOutcome.SecureFieldSkipped, outcome);
    Assert.Equal("old", clipboard.Text);
    Assert.Equal(0, uinput.PasteCalls);
}

static async Task InjectionTargetLost()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(true);
    using var service = NewInjection(clipboard, uinput, targets: new FakeTargetService(TargetFocusState.Lost));
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal(0, uinput.PasteCalls);
}

static async Task InjectionTargetChanged()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(true);
    using var service = NewInjection(clipboard, uinput,
        targets: new FakeTargetService(TargetFocusState.Ready, TargetFocusState.Changed));
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal(0, uinput.PasteCalls);
}

static async Task InjectionCancellation()
{
    var clipboard = new FakeClipboard("old") { BlockWrites = true };
    using var service = NewInjection(clipboard, new FakeUInput(true));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await service.InjectTranscriptAsync("transcript", cancellation.Token));
}

static async Task InjectionDisposalSafety()
{
    var clipboard = new FakeClipboard("old");
    var service = NewInjection(clipboard, new FakeUInput(false));
    service.StartSession();
    await service.CopyToClipboardAsync("transcript");
    service.ScheduleClipboardRestore(TimeSpan.FromMilliseconds(200));
    service.Dispose();
    await Task.Delay(300);
    Assert.Equal(0, clipboard.RestoreCalls);
}

static async Task AtSpiTargetStable()
{
    var query = new FakeAtSpiQuery(
        new AtSpiFocusInfo("42:0.1", SecureFieldState.NotSecure),
        new AtSpiFocusInfo("42:0.1", SecureFieldState.NotSecure));
    var targets = new AtSpiCapturedTargetService(query);
    var captured = targets.Capture();
    Assert.True(captured.IsSuccess && captured.Value is not null);
    Assert.Equal(TargetFocusState.Ready,
        await targets.ValidateAndFocusAsync(captured.Value!, CancellationToken.None));
}

static async Task AtSpiTargetChanged()
{
    var query = new FakeAtSpiQuery(
        new AtSpiFocusInfo("42:0.1", SecureFieldState.NotSecure),
        new AtSpiFocusInfo("42:0.2", SecureFieldState.NotSecure));
    var targets = new AtSpiCapturedTargetService(query);
    var captured = targets.Capture();
    Assert.True(captured.IsSuccess && captured.Value is not null);
    Assert.Equal(TargetFocusState.Changed,
        await targets.ValidateAndFocusAsync(captured.Value!, CancellationToken.None));
}

static Task AtSpiInsertionContextClassification()
{
    Assert.Equal(InsertionCursorContext.Unknown,
        AtSpiInsertionContextProvider.ClassifyPreceding(-1, "ignored"));
    Assert.Equal(InsertionCursorContext.StartOfSentence,
        AtSpiInsertionContextProvider.ClassifyPreceding(0, ""));
    Assert.Equal(InsertionCursorContext.StartOfSentence,
        AtSpiInsertionContextProvider.ClassifyPreceding(4, "    "));
    Assert.Equal(InsertionCursorContext.MidSentence,
        AtSpiInsertionContextProvider.ClassifyPreceding(6, "hello "));
    Assert.Equal(InsertionCursorContext.MidSentence,
        AtSpiInsertionContextProvider.ClassifyPreceding(6, "label: "));
    foreach (var terminator in new[] { '.', '!', '?', '…', '¡', '¿', ';', '\n', '\r' })
        Assert.Equal(InsertionCursorContext.StartOfSentence,
            AtSpiInsertionContextProvider.ClassifyPreceding(3, $"{terminator}  "));
    Assert.Equal(InsertionCursorContext.MidSentence,
        AtSpiInsertionContextProvider.ClassifyPreceding(65, "." + new string('a', 64)));
    return Task.CompletedTask;
}

static Task AtSpiInsertionContextPrivacy()
{
    Assert.Equal(InsertionCursorContext.StartOfSentence,
        AtSpiInsertionContextProvider.ParseClassificationOutput("START\n"u8));
    Assert.Equal(InsertionCursorContext.MidSentence,
        AtSpiInsertionContextProvider.ParseClassificationOutput("MID"u8));
    Assert.Equal(InsertionCursorContext.Unknown,
        AtSpiInsertionContextProvider.ParseClassificationOutput("UNKNOWN"u8));
    Assert.Equal(InsertionCursorContext.Unknown,
        AtSpiInsertionContextProvider.ParseClassificationOutput("START|private text"u8));
    Assert.Equal(InsertionCursorContext.Unknown,
        AtSpiInsertionContextProvider.ParseClassificationOutput("MID\nprivate text"u8));
    return Task.CompletedTask;
}

static async Task ClipboardFailurePreventsUInput()
{
    var clipboard = new FakeClipboard("old") { FailWrites = true };
    var uinput = new FakeUInput(true);
    using var service = NewInjection(clipboard, uinput);
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.Failed, outcome);
    Assert.Equal(0, uinput.PasteCalls);
}

static async Task UInputExceptionFallsBack()
{
    var clipboard = new FakeClipboard("old");
    using var service = NewInjection(clipboard, new ThrowingUInput());
    service.CaptureTarget();
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal("transcript", clipboard.Text);
}

static async Task CommandClipboardCapability()
{
    using var backend = new CommandClipboardBackend("/bin/cat", "/bin/true", true, null);
    Assert.True(!backend.GetCapabilities().PreservesAllClipboardFormats);
    var result = await backend.RestoreAsync(new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain"] = "text"u8.ToArray(),
        ["text/html"] = "<b>text</b>"u8.ToArray(),
    }), CancellationToken.None);
    Assert.True(result.IsFailure && result.Error!.Code == "clipboard_restore_partial");
}

static async Task NativeWaylandRestore()
{
    var owner = new FakeNativeClipboardOwner();
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", true, owner);
    var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain;charset=utf-8"] = "text"u8.ToArray(),
        ["text/html"] = "<b>text</b>"u8.ToArray(),
        ["image/png"] = [0x89, 0x50, 0x00, 0x4e, 0x47],
    });
    Assert.Success(await backend.RestoreAsync(snapshot, CancellationToken.None));
    Assert.True(backend.GetCapabilities().PreservesAllClipboardFormats);
    Assert.Equal(3, owner.Owned!.Formats.Count);
    Assert.SequenceEqual(snapshot.Formats["image/png"], owner.Owned.Formats["image/png"]);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await backend.RestoreAsync(snapshot, cancelled.Token));
}

static async Task ClipboardSnapshotBound()
{
    var owner = new FakeNativeClipboardOwner();
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", true, owner);
    var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        { ["application/octet-stream"] = new byte[32 * 1024 * 1024 + 1] });
    var result = await backend.RestoreAsync(snapshot, CancellationToken.None);
    Assert.True(result.IsFailure && result.Error!.Code == "clipboard_snapshot_too_large");
    Assert.True(owner.Owned is null);
}

static async Task NativeX11Restore()
{
    var owner = new FakeNativeClipboardOwner();
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", false, owner);
    var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain"] = "text"u8.ToArray(),
        ["image/png"] = [1, 2, 3],
    });
    Assert.Success(await backend.RestoreAsync(snapshot, CancellationToken.None));
    Assert.True(backend.GetCapabilities().PreservesAllClipboardFormats);
    Assert.Equal(2, owner.Owned!.Formats.Count);
    Assert.SequenceEqual([1, 2, 3], owner.Owned.Formats["image/png"]);
}

static async Task NativeX11RoundTrip()
{
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) return;
    var xclip = CommandClipboardBackend.FindExecutable("xclip");
    if (xclip is null) return;
    var owner = new NativeX11ClipboardOwner(allowXWayland:
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase));
    try
    {
        Assert.True(owner.IsAvailable);
        var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["text/plain"] = "native-text"u8.ToArray(),
            ["text/html"] = "<b>native</b>"u8.ToArray(),
            ["image/png"] = [0x89, 0x50, 0x4e, 0x47, 0x00, 0xff],
        });
        Assert.Success(await owner.OwnAsync(snapshot, CancellationToken.None));
        var targets = await ExternalProcessRunner.RunAsync(xclip,
            ["-selection", "clipboard", "-target", "TARGETS", "-out"], null, CancellationToken.None);
        var targetNames = System.Text.Encoding.UTF8.GetString(targets.Output);
        Assert.True(targetNames.Contains("text/plain", StringComparison.Ordinal));
        Assert.True(targetNames.Contains("text/html", StringComparison.Ordinal));
        Assert.True(targetNames.Contains("image/png", StringComparison.Ordinal));
        var image = await ExternalProcessRunner.RunAsync(xclip,
            ["-selection", "clipboard", "-target", "image/png", "-out"], null, CancellationToken.None);
        Assert.SequenceEqual(snapshot.Formats["image/png"], image.Output);

        Assert.Success(await owner.OwnAsync(new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { ["text/plain"] = "replacement"u8.ToArray() }), CancellationToken.None));
        targets = await ExternalProcessRunner.RunAsync(xclip,
            ["-selection", "clipboard", "-target", "TARGETS", "-out"], null, CancellationToken.None);
        targetNames = System.Text.Encoding.UTF8.GetString(targets.Output);
        Assert.True(targetNames.Contains("text/plain", StringComparison.Ordinal)
            && !targetNames.Contains("image/png", StringComparison.Ordinal));
    }
    finally { owner.Dispose(); }
    owner.Dispose();
}

static async Task XWaylandRoundTrip()
{
    var required = string.Equals(Environment.GetEnvironmentVariable("HW_REQUIRE_XWAYLAND_CLIPBOARD_BRIDGE"), "1", StringComparison.Ordinal);
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
    {
        if (required) throw new InvalidOperationException("WAYLAND_DISPLAY is required for the XWayland clipboard bridge gate.");
        return;
    }
    var wlPaste = CommandClipboardBackend.FindExecutable("wl-paste");
    var wlCopy = CommandClipboardBackend.FindExecutable("wl-copy");
    if (wlPaste is null || wlCopy is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
    {
        if (required) throw new InvalidOperationException("DISPLAY, wl-copy, and wl-paste are required for the XWayland clipboard bridge gate.");
        return;
    }
    using var sourceOwner = new NativeX11ClipboardOwner(allowXWayland: true);
    using var restoreOwner = new NativeX11ClipboardOwner(allowXWayland: true);
    if (!sourceOwner.IsAvailable || !restoreOwner.IsAvailable)
    {
        if (required) throw new InvalidOperationException("The native XWayland clipboard owner is unavailable.");
        return;
    }
    using var backend = new CommandClipboardBackend(wlCopy, wlPaste, true, restoreOwner);
    var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain;charset=utf-8"] = "wayland text"u8.ToArray(),
        ["text/html"] = "<p>wayland <b>html</b></p>"u8.ToArray(),
        ["image/png"] = [0x89, 0x50, 0x4e, 0x47, 0x00, 0xff, 0x01],
    });
    Assert.Success(await sourceOwner.OwnAsync(snapshot, CancellationToken.None));
    var captured = await backend.CaptureAsync(CancellationToken.None);
    if (!captured.IsSuccess || captured.Value is null)
    {
        if (required) throw new InvalidOperationException(
            $"The compositor did not bridge the XWayland selection to wl-paste: {captured.Error?.Code}: {captured.Error?.Message}");
        return;
    }
    foreach (var pair in snapshot.Formats)
        Assert.SequenceEqual(pair.Value, captured.Value!.Formats[pair.Key]);
    Assert.Success(await backend.SetTextAsync("temporary transcript", ClipboardHistoryPrivacyPolicy.Disabled,
        CancellationToken.None));
    Assert.Success(await backend.RestoreAsync(captured.Value!, CancellationToken.None));
    foreach (var pair in snapshot.Formats)
    {
        var read = await ExternalProcessRunner.RunAsync(wlPaste, ["--type", pair.Key], null,
            CancellationToken.None, maximumOutputBytes: pair.Value.Length + 1);
        Assert.Equal(0, read.ExitCode);
        Assert.SequenceEqual(pair.Value, read.Output);
    }
}

static async Task ExternalHelperTimeout()
{
    await Assert.ThrowsAsync<TimeoutException>(async () =>
        await ExternalProcessRunner.RunAsync("/bin/sh", ["-c", "sleep 30"], null,
            CancellationToken.None, TimeSpan.FromMilliseconds(50)));
}

static async Task ExternalHelperOutputBound()
{
    var started = Stopwatch.StartNew();
    await Assert.ThrowsAsync<InvalidDataException>(async () =>
        await ExternalProcessRunner.RunAsync("/usr/bin/head", ["-c", "1048576", "/dev/zero"], null,
            CancellationToken.None, maximumOutputBytes: 16));
    Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
}

static async Task X11ApplicationContext()
{
    var runner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(0, "_NET_ACTIVE_WINDOW(WINDOW): window id # 0x420001\n"u8.ToArray()),
        new ExternalProcessResult(0, """
_NET_WM_PID(CARDINAL) = 999999
_NET_WM_NAME(UTF8_STRING) = "Document - Editor"
WM_CLASS(STRING) = "editor", "Code"
"""u8.ToArray()));
    using var provider = new LinuxApplicationContextProvider(runner, "/usr/bin/xprop", null, false);
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("Code", result.Value!.ProcessName);
    Assert.Equal("Document - Editor", result.Value.WindowTitle);
    Assert.Equal("-root,_NET_ACTIVE_WINDOW", string.Join(',', runner.Calls[0].Arguments));
    Assert.Equal("-id,0x420001,_NET_WM_PID,_NET_WM_NAME,WM_NAME,WM_CLASS", string.Join(',', runner.Calls[1].Arguments));
}

static async Task WaylandApplicationContext()
{
    var app = Convert.ToBase64String("Writer"u8);
    var title = Convert.ToBase64String("Draft"u8);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        System.Text.Encoding.UTF8.GetBytes($"CONTEXT|999999|{app}|{title}\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true);
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("Writer", result.Value!.ProcessName);
    Assert.Equal("Draft", result.Value.WindowTitle);
    Assert.Equal("-c", runner.Calls[0].Arguments[0]);
}

static async Task GnomeWaylandApplicationContext()
{
    var app = Convert.ToBase64String("Browser"u8);
    var title = Convert.ToBase64String("Private tab title"u8);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        System.Text.Encoding.UTF8.GetBytes($"('CONTEXT|999999|{app}|{title}',)\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true,
        gdbus: "/usr/bin/gdbus", desktop: "GNOME");
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("Browser", result.Value!.ProcessName);
    Assert.Equal("Private tab title", result.Value.WindowTitle);
    Assert.Equal("gnome-companion-dbus+atspi", provider.GetCapabilities().Backend);
    Assert.True(runner.Calls[0].Arguments.Contains(LinuxApplicationContextProvider.GnomeBusName));
}

static async Task KdeWaylandApplicationContext()
{
    var app = Convert.ToBase64String("Konsole"u8);
    var title = Convert.ToBase64String("Shell"u8);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        System.Text.Encoding.UTF8.GetBytes($"('CONTEXT|999999|{app}|{title}',)\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true,
        gdbus: "/usr/bin/gdbus", desktop: "KDE");
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("Konsole", result.Value!.ProcessName);
    Assert.Equal("kde-kwin-dbus+atspi", provider.GetCapabilities().Backend);
    Assert.True(runner.Calls[0].Arguments.Contains(LinuxApplicationContextProvider.KdeBusName));
}

// Linux shipped no classifier until issue #279, so every snapshot carried the
// `other` default and the Sensitive screen-OCR gate in the shared prompt builder
// could never fire. The rules live in hw-catalog and are proved by
// shared-conformance/app-type-vectors.json; what is asserted here is that this
// provider actually asks.
static async Task ApplicationContextIsClassified()
{
    var app = Convert.ToBase64String("KeePassXC"u8);
    var title = Convert.ToBase64String("Passwords - KeePassXC"u8);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        System.Text.Encoding.UTF8.GetBytes($"CONTEXT|999999|{app}|{title}\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true);
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("sensitive", result.Value!.AppType);
    Assert.Equal("strong", result.Value.AppTypeConfidence);
    Assert.Equal("processName", result.Value.AppTypeSource);
    Assert.Equal("Sensitive", result.Value.Category);
    Assert.Equal("text", result.Value.TextFormat);
}

static async Task ApplicationContextClassificationDefaults()
{
    var app = Convert.ToBase64String("some-unknown-binary"u8);
    var title = Convert.ToBase64String("Untitled"u8);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        System.Text.Encoding.UTF8.GetBytes($"CONTEXT|999999|{app}|{title}\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true);
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("other", result.Value!.AppType);
    Assert.Equal("unknown", result.Value.AppTypeConfidence);
    Assert.Equal("default", result.Value.AppTypeSource);
}

static async Task WaylandCompanionFallback()
{
    var app = Convert.ToBase64String("Writer"u8);
    var title = Convert.ToBase64String("Fallback"u8);
    var runner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(1, []),
        new ExternalProcessResult(0, System.Text.Encoding.UTF8.GetBytes($"CONTEXT|999999|{app}|{title}\n")));
    using var provider = new LinuxApplicationContextProvider(runner, null, "/usr/bin/python3", true,
        gdbus: "/usr/bin/gdbus", desktop: "GNOME");
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.Equal("Writer", result.Value!.ProcessName);
    Assert.Equal(2, runner.Calls.Count);
    Assert.Equal("-c", runner.Calls[1].Arguments[0]);
}

static async Task WaylandContextUnsupported()
{
    using var provider = new LinuxApplicationContextProvider(new FakeDesktopCommandRunner(), null, null, true);
    Assert.Equal(LinuxDesktopCapabilityState.Unsupported, provider.GetCapabilities().State);
    var result = await provider.GatherAsync();
    Assert.True(result.IsFailure);
    Assert.Equal("active_app_unsupported", result.Error!.Code);
}

static async Task ApplicationContextTimeout()
{
    var runner = new FakeDesktopCommandRunner(new TimeoutException("simulated"));
    using var provider = new LinuxApplicationContextProvider(runner, "/usr/bin/xprop", null, false);
    var result = await provider.GatherAsync();
    Assert.True(result.IsFailure);
    Assert.Equal("active_app_timeout", result.Error!.Code);
}

static Task OcrPrivateCleanup() => WithTemporaryDirectoryAsync(async directory =>
{
    var capture = new FakeCaptureHook([1, 2, 3, 4]);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, "recognized private text"u8.ToArray()));
    var service = new LinuxScreenOcrService(capture, runner, new FakeAppPaths(directory), "/usr/bin/tesseract");
    var result = await service.CaptureAndRecognizeAsync(10);
    Assert.True(result.IsSuccess);
    Assert.Equal("recognized", result.Value);
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, capture.ModeDuringCapture);
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, capture.DirectoryModeDuringCapture);
    Assert.Equal(0, Directory.GetFiles(directory).Length);
    Assert.Equal(0, Directory.GetDirectories(directory).Length);
    Assert.Equal("stdout", runner.Calls[0].Arguments[1]);
    Assert.True(Path.IsPathFullyQualified(runner.Calls[0].Arguments[0]));
});

static Task OcrCaptureFailureCleanup() => WithTemporaryDirectoryAsync(async directory =>
{
    var capture = new FakeCaptureHook([]) { Failure = PlatformResult.Failure("capture_failed", "test") };
    var service = new LinuxScreenOcrService(capture, new FakeDesktopCommandRunner(),
        new FakeAppPaths(directory), "/usr/bin/tesseract");
    var result = await service.CaptureAndRecognizeAsync();
    Assert.True(result.IsFailure);
    Assert.Equal(0, Directory.GetFiles(directory).Length);
    Assert.Equal(0, Directory.GetDirectories(directory).Length);
});

static Task OcrCancellationCleanup() => WithTemporaryDirectoryAsync(async directory =>
{
    var capture = new FakeCaptureHook([1]) { Cancel = true };
    var service = new LinuxScreenOcrService(capture, new FakeDesktopCommandRunner(),
        new FakeAppPaths(directory), "/usr/bin/tesseract");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await service.CaptureAndRecognizeAsync(cancellationToken: cancellation.Token));
    Assert.Equal(0, Directory.GetFiles(directory).Length);
    Assert.Equal(0, Directory.GetDirectories(directory).Length);
});

static Task OcrPortalCapability()
{
    var capture = new FakeCaptureHook([1]) { UsesPortal = true };
    var service = new LinuxScreenOcrService(capture, new FakeDesktopCommandRunner(),
        new FakeAppPaths("/tmp/not-used"), "/usr/bin/tesseract");
    var capability = service.GetCapabilities();
    Assert.True(capability.UsesDesktopPortal);
    Assert.True(capability.CaptureAvailable);
    return Task.CompletedTask;
}

static async Task PortalScreenshotOutcomes()
{
    foreach (var test in new[]
    {
        (Outcome: "SUCCESS", Error: (string?)null),
        (Outcome: "CANCELLED", Error: "screen_capture_cancelled"),
        (Outcome: "DENIED", Error: "screen_capture_denied"),
        (Outcome: "UNAVAILABLE", Error: "screen_capture_unavailable"),
        (Outcome: "TIMEOUT", Error: "screen_capture_timeout"),
        (Outcome: "INVALID", Error: "screen_capture_failed"),
    })
    {
        var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
            System.Text.Encoding.UTF8.GetBytes(test.Outcome + "\n")));
        var hook = new PortalScreenshotCaptureHook(runner, "/usr/bin/python3", true);
        var result = await hook.CaptureSelectionAsync("/tmp/private-capture-test.png");
        Assert.Equal(test.Error, result.Error?.Code);
        Assert.True(hook.GetCapabilities().UsesDesktopPortal);
        Assert.Equal("-c", runner.Calls[0].Arguments[0]);
        Assert.Equal("/tmp/private-capture-test.png", runner.Calls[0].Arguments[2]);
    }
}

static async Task PortalScreenshotCancellation()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var hook = new PortalScreenshotCaptureHook(new FakeDesktopCommandRunner(), "/usr/bin/python3", true);
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await hook.CaptureSelectionAsync("/tmp/not-created", cancellation.Token));
    var unavailable = new PortalScreenshotCaptureHook(new FakeDesktopCommandRunner(), null, false);
    var result = await unavailable.CaptureSelectionAsync("/tmp/not-created");
    Assert.Equal("screen_capture_unsupported", result.Error!.Code);
}

static async Task WaylandApplicationContextIntegration()
{
    if (Environment.GetEnvironmentVariable("HYPERWHISPER_WAYLAND_CONTEXT_INTEGRATION") != "1") return;
    using var provider = new LinuxApplicationContextProvider();
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.True(!string.IsNullOrWhiteSpace(result.Value!.ProcessName));
}

static Task PortalScreenshotIntegration() => WithTemporaryDirectoryAsync(async directory =>
{
    if (Environment.GetEnvironmentVariable("HYPERWHISPER_PORTAL_INTEGRATION") != "1") return;
    var python = CommandClipboardBackend.FindExecutable("python3");
    Assert.True(python is not null);
    var destination = Path.Combine(directory, "portal.png");
    using (new FileStream(destination, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    })) { }
    var hook = new PortalScreenshotCaptureHook(new DesktopCommandRunner(), python, true);
    var result = await hook.CaptureSelectionAsync(destination);
    Assert.True(result.IsSuccess);
    Assert.True(new FileInfo(destination).Length > 0);
});

static async Task X11ApplicationContextIntegration()
{
    if (Environment.GetEnvironmentVariable("HYPERWHISPER_X11_INTEGRATION") != "1") return;
    using var provider = new LinuxApplicationContextProvider();
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.True(result.Value!.WindowTitle.Contains("HyperWhisper Context Test", StringComparison.Ordinal));
}

static Task RuntimeLocatorVariants() => WithTemporaryDirectory(directory =>
{
    var whisper = Path.Combine(directory, "runtimes", "vulkan", "linux-x64", "libwhisper.so");
    var whisperCpu = Path.Combine(directory, "runtimes", "linux-x64", "libwhisper.so");
    var llama = Path.Combine(directory, "runtimes", "linux-x64", "native", "cuda12", "libllama.so");
    var parakeet = Path.Combine(directory, "parakeet-engine", "parakeet-engine");
    Directory.CreateDirectory(Path.GetDirectoryName(whisper)!); File.WriteAllBytes(whisper, [1]);
    Directory.CreateDirectory(Path.GetDirectoryName(whisperCpu)!); File.WriteAllBytes(whisperCpu, [1]);
    Directory.CreateDirectory(Path.GetDirectoryName(llama)!); File.WriteAllBytes(llama, [1]);
    Directory.CreateDirectory(Path.GetDirectoryName(parakeet)!); File.WriteAllBytes(parakeet, [1]);
    File.SetUnixFileMode(parakeet, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    var locator = new LinuxNativeRuntimeLocator(directory, System.Runtime.InteropServices.Architecture.X64);
    Assert.True(locator.FindLibrary("whisper", NativeComputeBackend.Vulkan).IsSuccess);
    Assert.True(locator.FindLibrary("whisper", NativeComputeBackend.Cpu).IsSuccess);
    Assert.True(locator.FindLibrary("llama", NativeComputeBackend.Cuda).IsSuccess);
    Assert.True(locator.FindExecutable("parakeet").IsSuccess);
    Assert.True(locator.Capabilities.ComputeBackends.Contains(NativeComputeBackend.Cuda));
});

static Task RuntimeLocatorPublishedOutput()
{
    var output = Path.GetFullPath("app/linux/HyperWhisper.Linux/bin/Release/net10.0");
    if (!Directory.Exists(output)) return Task.CompletedTask;
    var locator = new LinuxNativeRuntimeLocator(output, System.Runtime.InteropServices.Architecture.X64);
    var cpu = locator.FindLibrary("whisper", NativeComputeBackend.Cpu);
    var vulkan = locator.FindLibrary("whisper", NativeComputeBackend.Vulkan);
    Assert.True(cpu.IsSuccess && cpu.Value?.EndsWith("runtimes/linux-x64/libwhisper.so", StringComparison.Ordinal) == true);
    Assert.True(vulkan.IsSuccess && vulkan.Value?.EndsWith("runtimes/vulkan/linux-x64/libwhisper.so", StringComparison.Ordinal) == true);
    return Task.CompletedTask;
}

static async Task ChildProcessLiteralArgv()
{
    var launcher = new LinuxChildProcessLauncher();
    var started = launcher.Start(new ChildProcessStartRequest
    {
        ExecutablePath = "/usr/bin/printf", Arguments = ["%s", "$(touch /tmp/should-not-exist)"], RedirectStandardOutput = true,
    });
    Assert.True(started.IsSuccess);
    await using var child = started.Value!;
    var text = await new StreamReader(child.StandardOutput!).ReadToEndAsync();
    Assert.Equal(0, await child.WaitForExitAsync());
    Assert.Equal("$(touch /tmp/should-not-exist)", text);
}

static async Task ChildProcessTermination()
{
    var started = new LinuxChildProcessLauncher().Start(new ChildProcessStartRequest
    { ExecutablePath = "/bin/sh", Arguments = ["-c", "sleep 30 & wait"] });
    Assert.True(started.IsSuccess);
    await using var child = started.Value!;
    await child.TerminateAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    Assert.True(child.HasExited);
}

static Task GpuRejectsSoftwareRenderer()
{
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0,
        "deviceName = llvmpipe (LLVM 20)\ndeviceType = PHYSICAL_DEVICE_TYPE_CPU\n"u8.ToArray()));
    var provider = new LinuxGpuInfoProvider(runner, "/usr/bin/vulkaninfo", null);
    var result = provider.GetBestGpu();
    Assert.True(result.IsSuccess && result.Value is null);
    Assert.True(provider.GetCapabilities().SoftwareRendererDetected);
    return Task.CompletedTask;
}

static Task GpuCudaEvidence()
{
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, "NVIDIA RTX Test, 8192\n"u8.ToArray()));
    var provider = new LinuxGpuInfoProvider(runner, null, "/usr/bin/nvidia-smi");
    var result = provider.GetBestGpu();
    var gpu = result.Value;
    Assert.True(result.IsSuccess && gpu is not null && gpu.SupportsCuda);
    Assert.Equal(8192L * 1024 * 1024, gpu!.DedicatedMemoryBytes);
    Assert.True(!gpu.SupportsVulkan);
    return Task.CompletedTask;
}

static Task HostGpuEvidence()
{
    var provider = new LinuxGpuInfoProvider();
    var result = provider.GetBestGpu();
    if (result.IsSuccess && provider.GetCapabilities().SoftwareRendererDetected) Assert.True(result.Value is null);
    if (result.IsSuccess && result.Value is not null)
        Assert.True(result.Value.SupportsCuda || result.Value.SupportsVulkan);
    return Task.CompletedTask;
}

static Task PulseInputEnumeration()
{
    var json = """[{"name":"mic.one","description":"Microphone","monitor_of_sink":null},{"name":"sink.monitor","description":"Monitor","monitor_of_sink":1}]""";
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, System.Text.Encoding.UTF8.GetBytes(json)),
        new ExternalProcessResult(0, "mic.one\n"u8.ToArray()));
    using var service = new PulseAudioInputDeviceService(runner, "/usr/bin/pactl");
    var result = service.GetAvailableDevices();
    Assert.True(result.IsSuccess);
    Assert.Equal(1, result.Value!.Count);
    Assert.True(result.Value[0].IsDefault);
    return Task.CompletedTask;
}

static async Task StreamingAudioCapture()
{
    var source = new FakeStreamingAudioSource(new MemoryStream([1, 0, 2, 0]));
    using var capture = new PulseStreamingAudioCapture(new FakeStreamingAudioSourceFactory(source));
    var chunk = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    capture.AudioChunkAvailable += (_, _) => throw new InvalidOperationException("subscriber");
    capture.AudioChunkAvailable += (_, value) => chunk.TrySetResult(value);
    capture.CaptureStopped += (_, _) => stopped.TrySetResult();
    Assert.Success(capture.Start(new AudioRecordingOptions("default")));
    Assert.SequenceEqual([1, 0, 2, 0], (await chunk.Task.WaitAsync(TimeSpan.FromSeconds(2))).ToArray());
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(capture.Duration > TimeSpan.Zero);
}

static async Task StreamingAudioBlockedStop()
{
    var stream = new BlockingAudioStream();
    var source = new FakeStreamingAudioSource(stream);
    using var capture = new PulseStreamingAudioCapture(new FakeStreamingAudioSourceFactory(source));
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    capture.CaptureStopped += (_, _) => stopped.TrySetResult();
    Assert.Success(capture.Start(new AudioRecordingOptions("default")));
    await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    capture.Stop();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(!capture.IsCapturing);
    Assert.Equal(1, source.TerminateCalls);
}

static Task PrivateCredentialFallback() => WithTemporaryDirectory(directory =>
{
    var backend = new PrivateFileCredentialBackend(new LinuxPrivateFileService(), directory);
    var store = new LinuxCredentialStore(backend, new("private-file-0600", false, true));
    Assert.Success(store.Write("service", "account", [4, 5, 6]));
    Assert.SequenceEqual([4, 5, 6], store.Read("service", "account").Value!);
    var file = Directory.GetFiles(directory).Single();
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    Assert.True(!Path.GetFileName(file).Contains("service", StringComparison.Ordinal));
    Assert.Success(store.Delete("service", "account"));
});

static Task SecretServiceArgvPrivacy()
{
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, []));
    var backend = new SecretToolCredentialBackend(runner, "/usr/bin/secret-tool");
    Assert.Success(backend.Write("resource", "account", "private-value"u8));
    Assert.True(!string.Join(' ', runner.Calls[0].Arguments).Contains("private-value", StringComparison.Ordinal));
    Assert.True(runner.Calls[0].Input is { Length: > 0 });
    return Task.CompletedTask;
}

static Task SingleInstanceSocket() => WithTemporaryDirectoryAsync(async directory =>
{
    using var primary = new LinuxSingleInstanceCoordinator(new FakeAppPaths(directory));
    using var secondary = new LinuxSingleInstanceCoordinator(new FakeAppPaths(directory));
    Assert.True(primary.TryAcquire().Value);
    Assert.True(!secondary.TryAcquire().Value);
    var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    primary.ActivationRequested += (_, _) => activated.TrySetResult();
    Assert.Success(secondary.SignalExistingInstance());
    await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    secondary.Dispose();
    Assert.True(File.Exists(Path.Combine(directory, "instance.sock")));
});

static Task XdgAutostart() => WithTemporaryDirectory(directory =>
{
    var service = new LinuxAutostartService(new FakeAppPaths(directory), "/bin/true", new LinuxPrivateFileService());
    Assert.Success(service.Enable());
    var path = Path.Combine(directory, "autostart", "hyperwhisper.desktop");
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    Assert.True(service.IsEnabled().Value);
    Assert.Success(service.Disable());
    Assert.True(!File.Exists(path));
});

static Task PushToTalkPrivacy()
{
    var shortcuts = new FakeGlobalShortcutService(); var scheduler = new FakePushToTalkScheduler();
    using var monitor = new LinuxPushToTalkMonitor(shortcuts, scheduler);
    var pressed = 0; var released = 0;
    monitor.Pressed += (_, _) => pressed++; monitor.Released += (_, _) => released++;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier, ModifierSide.LeftAlt));
    Assert.Success(monitor.Start());
    Assert.Equal("push-to-talk", shortcuts.Registered!.Name);
    Assert.Equal("LeftAlt", shortcuts.Registered.Shortcut.Key.Value);
    shortcuts.Emit("unrelated", true); shortcuts.Emit("push-to-talk", true);
    scheduler.Advance(TimeSpan.FromMilliseconds(249)); Assert.Equal(0, pressed);
    scheduler.Advance(TimeSpan.FromMilliseconds(1)); shortcuts.Emit("push-to-talk", false);
    scheduler.Advance(TimeSpan.FromMilliseconds(100));
    Assert.Equal(1, pressed); Assert.Equal(1, released);
    return Task.CompletedTask;
}

static Task PushToTalkDoubleLock()
{
    var shortcuts = new FakeGlobalShortcutService(); var scheduler = new FakePushToTalkScheduler();
    using var monitor = new LinuxPushToTalkMonitor(shortcuts, scheduler);
    var pressed = 0; var released = 0; monitor.Pressed += (_, _) => pressed++; monitor.Released += (_, _) => released++;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier, DoublePressLock: true)); Assert.Success(monitor.Start());
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false); scheduler.Advance(TimeSpan.FromMilliseconds(100));
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    Assert.Equal(1, pressed); Assert.Equal(0, released);
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    Assert.Equal(0, released); // ignored during the two-second post-lock bounce window
    scheduler.Advance(TimeSpan.FromMilliseconds(2000));
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    Assert.Equal(1, released);
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false);
    scheduler.Advance(TimeSpan.FromMilliseconds(100)); scheduler.Advance(TimeSpan.FromMilliseconds(1500));
    Assert.Equal(2, pressed); Assert.Equal(2, released);
    return Task.CompletedTask;
}

static Task PushToTalkHoldDebounce()
{
    var shortcuts = new FakeGlobalShortcutService(); var scheduler = new FakePushToTalkScheduler();
    using var monitor = new LinuxPushToTalkMonitor(shortcuts, scheduler);
    var pressed = 0; var released = 0; monitor.Pressed += (_, _) => pressed++; monitor.Released += (_, _) => released++;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier)); Assert.Success(monitor.Start());
    shortcuts.Emit("push-to-talk", true); scheduler.Advance(TimeSpan.FromMilliseconds(250)); Assert.Equal(1, pressed);
    shortcuts.Emit("push-to-talk", false); scheduler.Advance(TimeSpan.FromMilliseconds(50));
    shortcuts.Emit("push-to-talk", true); scheduler.Advance(TimeSpan.FromMilliseconds(100)); Assert.Equal(0, released);
    shortcuts.Emit("push-to-talk", false); scheduler.Advance(TimeSpan.FromMilliseconds(100)); Assert.Equal(1, released);
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false); scheduler.Advance(TimeSpan.FromMilliseconds(100));
    Assert.Equal(1, pressed); Assert.Equal(1, released); // quick tap without lock is silent
    return Task.CompletedTask;
}

static Task PushToTalkSubscriberIsolation()
{
    var shortcuts = new FakeGlobalShortcutService(); var scheduler = new FakePushToTalkScheduler();
    using var monitor = new LinuxPushToTalkMonitor(shortcuts, scheduler); var reached = false;
    monitor.Pressed += (_, _) => throw new InvalidOperationException("subscriber"); monitor.Pressed += (_, _) => reached = true;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier)); Assert.Success(monitor.Start());
    shortcuts.Emit("push-to-talk", true); scheduler.Advance(TimeSpan.FromMilliseconds(250)); Assert.True(reached);
    return Task.CompletedTask;
}

static async Task PushToTalkInterferencePrivacy()
{
    var source = new FakeSource("keyboard", Frame(56, 1), Frame(48, 1));
    using var shortcuts = new LinuxGlobalShortcutService(new FakeSourceFactory(source), null);
    using var monitor = new LinuxPushToTalkMonitor(shortcuts);
    var interfered = 0; monitor.Interfered += (_, _) => interfered++;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier, ModifierSide.LeftAlt)); Assert.Success(monitor.Start());
    await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, interfered);
}

static Task PushToTalkActiveInterference()
{
    var shortcuts = new FakeGlobalShortcutService(); var scheduler = new FakePushToTalkScheduler();
    using var monitor = new LinuxPushToTalkMonitor(shortcuts, scheduler); var reached = false;
    monitor.Interfered += (_, _) => throw new InvalidOperationException("subscriber");
    monitor.Interfered += (_, _) => reached = true;
    monitor.Configure(new PushToTalkConfiguration(PushToTalkMode.Modifier, DoublePressLock: true)); Assert.Success(monitor.Start());
    shortcuts.Emit("push-to-talk", true); shortcuts.Emit("push-to-talk", false); scheduler.Advance(TimeSpan.FromMilliseconds(100));
    Assert.True(shortcuts.InterferenceArmed); shortcuts.EmitInterference(); Assert.True(reached);
    Assert.True(!shortcuts.InterferenceArmed); scheduler.Advance(TimeSpan.FromMilliseconds(1500));
    return Task.CompletedTask;
}

static Task InteractionActionRegistration()
{
    var shortcuts = new FakeInteractionShortcutService();
    var pushToTalk = new FakeInteractionPushToTalk();
    var recording = new FakeInteractionRecordingSession { Active = true };
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, pushToTalk, new FakeInteractionTextInjection(), recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    Assert.Equal(
        string.Join(',', LinuxInteractionCoordinator.ToggleActionName, LinuxInteractionCoordinator.CancelActionName,
            LinuxInteractionCoordinator.ChangeModeActionName, LinuxInteractionCoordinator.SessionCancelActionName),
        string.Join(',', shortcuts.Current.Select(item => item.Name)));
    Assert.Equal(1, shortcuts.StartCalls);
    Assert.Equal(1, pushToTalk.StartCalls);

    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, true);
    Assert.Equal(1, recording.CancelCalls);
    return Task.CompletedTask;
}

static Task InteractionActionPrivacy()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession { Active = true };
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(), recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));

    var modeChanges = 0;
    coordinator.ChangeModeRequested += (_, args) => Assert.True(ReferenceEquals(args, EventArgs.Empty));
    coordinator.ChangeModeRequested += (_, _) => throw new InvalidOperationException("subscriber");
    coordinator.ChangeModeRequested += (_, _) => modeChanges++;
    shortcuts.Emit(LinuxInteractionCoordinator.ChangeModeActionName, true);
    shortcuts.Emit(LinuxInteractionCoordinator.ChangeModeActionName, true);
    Assert.Equal(1, modeChanges);
    shortcuts.Emit(LinuxInteractionCoordinator.ChangeModeActionName, false);
    shortcuts.Emit(LinuxInteractionCoordinator.ChangeModeActionName, true);
    Assert.Equal(2, modeChanges);

    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, true);
    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, true);
    Assert.Equal(1, recording.CancelCalls);
    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, false);
    recording.Active = true;
    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, true);
    Assert.Equal(2, recording.CancelCalls);

    shortcuts.EmitUnregistered("raw-key-material", new GlobalShortcut(ShortcutModifiers.Meta, new("Q")));
    Assert.Equal(2, modeChanges);
    Assert.Equal(2, recording.CancelCalls);
    return Task.CompletedTask;
}

static async Task StreamingStartupCancellation()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new BlockingInteractionRecordingSession();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(), recording,
        new ImmediateUiDispatcher());
    var configuration = InteractionConfiguration() with
    {
        StreamingEnabled = true,
        StreamingShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, new("S")),
    };
    Assert.Success(coordinator.ConfigureAndStart(configuration));
    var failures = new List<PlatformError>();
    coordinator.OperationFailed += (_, error) => failures.Add(error);

    shortcuts.Emit(LinuxInteractionCoordinator.StreamingActionName, true);
    await recording.StreamingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    shortcuts.Emit(LinuxInteractionCoordinator.ToggleActionName, true);
    Assert.Equal("interaction.batch_while_streaming_starting", failures.Single().Code);
    shortcuts.Emit(LinuxInteractionCoordinator.StreamingActionName, false);
    shortcuts.Emit(LinuxInteractionCoordinator.StreamingActionName, true);
    await recording.StreamingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Task.Delay(30);
    Assert.True(recording.StartKinds.SequenceEqual([InteractionRecordingKind.Streaming]));
}

static Task InteractionKindMutualExclusion()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession { Active = true, Streaming = true };
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(), recording,
        new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration() with
    {
        StreamingEnabled = true,
        StreamingShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, new("S")),
    }));
    var failures = new List<PlatformError>();
    coordinator.OperationFailed += (_, error) => failures.Add(error);
    shortcuts.Emit(LinuxInteractionCoordinator.ToggleActionName, true);
    Assert.Equal("interaction.batch_while_streaming", failures.Single().Code);
    recording.Streaming = false;
    shortcuts.Emit(LinuxInteractionCoordinator.StreamingActionName, true);
    Assert.Equal("interaction.streaming_while_batch", failures.Last().Code);
    Assert.Equal(0, recording.StopCalls);
    return Task.CompletedTask;
}

static Task InteractionFlexibleShortcutValidation()
{
    using var coordinator = new LinuxInteractionCoordinator(
        new FakeInteractionShortcutService(), new FakeInteractionPushToTalk(),
        new FakeInteractionTextInjection(), new FakeInteractionRecordingSession(),
        new ImmediateUiDispatcher());
    var valid = InteractionConfiguration() with
    {
        ToggleShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt),
        ChangeModeShortcut = null,
        SessionCancelShortcut = null,
    };
    Assert.Success(coordinator.ConfigureAndStart(valid));
    var invalid = coordinator.ConfigureAndStart(valid with
    {
        ToggleShortcut = new(ShortcutModifiers.Control),
    });
    Assert.Equal("interaction.toggle-transcription_invalid", invalid.Error!.Code);
    return Task.CompletedTask;
}

static Task InteractionActionRollback()
{
    var shortcuts = new FakeInteractionShortcutService();
    var pushToTalk = new FakeInteractionPushToTalk();
    var recording = new FakeInteractionRecordingSession { Active = true };
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, pushToTalk, new FakeInteractionTextInjection(), recording, new ImmediateUiDispatcher());
    var original = InteractionConfiguration();
    Assert.Success(coordinator.ConfigureAndStart(original));
    var originalNames = shortcuts.Current.Select(item => item.Name).ToArray();
    var originalRegistrationCalls = shortcuts.RegistrationHistory.Count;

    var unsafeCancel = coordinator.ConfigureAndStart(original with
    {
        CancelShortcut = new(ShortcutModifiers.None, new("Escape")),
    });
    Assert.True(unsafeCancel.IsFailure);
    Assert.Equal("interaction.cancel_shortcut_unsafe", unsafeCancel.Error!.Code);
    Assert.Equal(originalRegistrationCalls, shortcuts.RegistrationHistory.Count);

    var conflicts = new[]
    {
        original with { CancelShortcut = original.ToggleShortcut },
        original with { ChangeModeShortcut = original.ToggleShortcut },
        original with { ChangeModeShortcut = original.CancelShortcut },
        original with { SessionCancelShortcut = original.ToggleShortcut },
        original with { PushToTalk = new(PushToTalkMode.CustomShortcut, CustomShortcut: original.ToggleShortcut) },
        original with { PushToTalk = new(PushToTalkMode.CustomShortcut, CustomShortcut: original.CancelShortcut) },
        original with { PushToTalk = new(PushToTalkMode.CustomShortcut, CustomShortcut: original.ChangeModeShortcut) },
    };
    foreach (var conflict in conflicts)
    {
        var result = coordinator.ConfigureAndStart(conflict);
        Assert.True(result.IsFailure);
        Assert.Equal("interaction.shortcut_conflict", result.Error!.Code);
        Assert.Equal(originalRegistrationCalls, shortcuts.RegistrationHistory.Count);
        Assert.Equal(string.Join(',', originalNames), string.Join(',', shortcuts.Current.Select(item => item.Name)));
    }

    var replacement = original with
    {
        ToggleShortcut = new(ShortcutModifiers.Control, new("F8")),
        CancelShortcut = new(ShortcutModifiers.Control, new("F9")),
        ChangeModeShortcut = new(ShortcutModifiers.Control, new("F10")),
    };
    shortcuts.FailNextName = LinuxInteractionCoordinator.ChangeModeActionName;
    Assert.True(coordinator.ConfigureAndStart(replacement).IsFailure);
    Assert.Equal(string.Join(',', originalNames), string.Join(',', shortcuts.Current.Select(item => item.Name)));
    Assert.Equal(originalRegistrationCalls + 2, shortcuts.RegistrationHistory.Count);
    shortcuts.Emit(LinuxInteractionCoordinator.CancelActionName, true);
    Assert.Equal(1, recording.CancelCalls);
    return Task.CompletedTask;
}

static LinuxInteractionConfiguration InteractionConfiguration() => new(
    new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new("Space")),
    new PushToTalkConfiguration(PushToTalkMode.Disabled),
    TimeSpan.FromSeconds(10),
    new GlobalShortcut(ShortcutModifiers.Control, new("Escape")),
    new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new("Period")));

static Task InteractionX11Rollback()
{
    var connection = new FakeX11Connection();
    using var shortcuts = new X11GlobalShortcutService(new FakeX11Factory(connection));
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(),
        new FakeInteractionRecordingSession(), new ImmediateUiDispatcher());
    var original = InteractionConfiguration();
    Assert.Success(coordinator.ConfigureAndStart(original));
    var originalGrabs = connection.Grabs.ToArray();
    Assert.True(originalGrabs.Length > 0);

    connection.FailNextGrabForKeycode = connection.Keycode(0xffc7); // F10
    var replacement = original with
    {
        ToggleShortcut = new(ShortcutModifiers.Control, new("F8")),
        CancelShortcut = new(ShortcutModifiers.Control, new("F9")),
        ChangeModeShortcut = new(ShortcutModifiers.Control, new("F10")),
    };
    var failed = coordinator.ConfigureAndStart(replacement);
    Assert.True(failed.IsFailure);
    Assert.Equal("shortcut_grab_failed", failed.Error!.Code);
    Assert.Equal(
        string.Join(',', originalGrabs.Select(value => $"{value.Keycode}:{value.Modifiers}")),
        string.Join(',', connection.Grabs.Select(value => $"{value.Keycode}:{value.Modifiers}")));
    return Task.CompletedTask;
}

static async Task SessionCancelLifecycle()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(),
        recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    Assert.True(shortcuts.Current.All(item => item.Name != LinuxInteractionCoordinator.SessionCancelActionName));

    shortcuts.EmitUnregistered(
        LinuxInteractionCoordinator.SessionCancelActionName,
        new GlobalShortcut(ShortcutModifiers.None, new("Escape")));
    Assert.Equal(0, recording.CancelCalls);

    await coordinator.StartRecordingAsync();
    Assert.True(shortcuts.Current.Any(item => item.Name == LinuxInteractionCoordinator.SessionCancelActionName));
    shortcuts.Emit(LinuxInteractionCoordinator.SessionCancelActionName, true);
    Assert.Equal(1, recording.CancelCalls);
    Assert.True(shortcuts.Current.All(item => item.Name != LinuxInteractionCoordinator.SessionCancelActionName));
    Assert.True(shortcuts.Current.Any(item => item.Name == LinuxInteractionCoordinator.ToggleActionName));
    Assert.True(shortcuts.Current.Any(item => item.Name == LinuxInteractionCoordinator.ChangeModeActionName));
}

static async Task SessionCancelConfirmationPreservesSession()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession { DeferCancelRequest = true };
    var injection = new FakeInteractionTextInjection();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), injection, recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    await coordinator.StartRecordingAsync();

    await coordinator.CancelRecordingAsync();
    Assert.Equal(1, recording.CancelRequestCalls);
    Assert.Equal(0, recording.CancelCalls);
    Assert.True(recording.IsActive);
    Assert.Equal(0, injection.EndSessionCalls);
    Assert.True(shortcuts.Current.Any(item => item.Name == LinuxInteractionCoordinator.SessionCancelActionName));

    await coordinator.ConfirmCancelRecordingAsync();
    Assert.Equal(1, recording.CancelCalls);
    Assert.True(!recording.IsActive);
    Assert.Equal(1, injection.EndSessionCalls);
    Assert.True(shortcuts.Current.All(item => item.Name != LinuxInteractionCoordinator.SessionCancelActionName));
}

static async Task SessionCancelRegistrationRollback()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(),
        recording, new ImmediateUiDispatcher());
    var configuration = InteractionConfiguration();
    Assert.Success(coordinator.ConfigureAndStart(configuration));
    var idleNames = shortcuts.Current.Select(item => item.Name).ToArray();
    PlatformError? failure = null;
    coordinator.OperationFailed += (_, error) => failure = error;

    shortcuts.FailNextName = LinuxInteractionCoordinator.SessionCancelActionName;
    await coordinator.StartRecordingAsync();
    Assert.True(recording.IsActive);
    Assert.Equal("interaction.session_cancel_registration_failed", failure!.Code);
    Assert.Equal(string.Join(',', idleNames), string.Join(',', shortcuts.Current.Select(item => item.Name)));
    await coordinator.StopRecordingAsync();

    recording.StartResult = PlatformResult.Failure("recording.failed", "failed");
    await coordinator.StartRecordingAsync();
    Assert.True(shortcuts.Current.All(item => item.Name != LinuxInteractionCoordinator.SessionCancelActionName));
}

static async Task SessionCancelX11Rollback()
{
    var connection = new FakeX11Connection();
    using var shortcuts = new X11GlobalShortcutService(new FakeX11Factory(connection));
    var recording = new FakeInteractionRecordingSession();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(),
        recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    var idleGrabs = connection.Grabs.ToArray();
    connection.FailNextGrabForKeycode = connection.Keycode(0xff1b);

    await coordinator.StartRecordingAsync();
    Assert.True(recording.IsActive);
    Assert.Equal(
        string.Join(',', idleGrabs.Select(value => $"{value.Keycode}:{value.Modifiers}")),
        string.Join(',', connection.Grabs.Select(value => $"{value.Keycode}:{value.Modifiers}")));
}

static async Task SessionCancelDisposalRace()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession();
    var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(),
        recording, new ImmediateUiDispatcher());
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    shortcuts.Registering = desired =>
    {
        if (desired.Any(item => item.Name == LinuxInteractionCoordinator.SessionCancelActionName))
            coordinator.Dispose();
    };

    await coordinator.StartRecordingAsync();
    Assert.Equal(0, shortcuts.Current.Count);
}

static async Task InteractionDurationLimitStopsOnce()
{
    var shortcuts = new FakeInteractionShortcutService();
    var recording = new FakeInteractionRecordingSession();
    var scheduler = new FakeInteractionDurationScheduler();
    using var coordinator = new LinuxInteractionCoordinator(
        shortcuts, new FakeInteractionPushToTalk(), new FakeInteractionTextInjection(), recording,
        new ImmediateUiDispatcher(), scheduler, TimeSpan.FromMilliseconds(25));
    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration()));
    var errors = new List<PlatformError>();
    coordinator.OperationFailed += (_, error) => errors.Add(error);

    await coordinator.StartRecordingAsync();
    scheduler.Advance(TimeSpan.FromMilliseconds(25));
    Assert.Equal(1, recording.StopCalls);
    Assert.Equal("interaction.recording_duration_limit_reached", errors.Single().Code);
    scheduler.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(1, recording.StopCalls);

    Assert.Success(coordinator.ConfigureAndStart(InteractionConfiguration() with
    {
        StreamingEnabled = true,
        StreamingShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, new("S")),
    }));
    await coordinator.StartStreamingAsync();
    scheduler.Advance(TimeSpan.FromMilliseconds(25));
    Assert.Equal(2, recording.StopCalls);
    Assert.Equal(2, errors.Count);
    Assert.Equal("interaction.streaming_duration_limit_reached", errors[1].Code);
}

static async Task InteractionDurationLimitManualCompletion()
{
    var recording = new FakeInteractionRecordingSession();
    var scheduler = new FakeInteractionDurationScheduler();
    var dispatcher = new QueuedInteractionUiDispatcher();
    using var coordinator = new LinuxInteractionCoordinator(
        new FakeInteractionShortcutService(), new FakeInteractionPushToTalk(),
        new FakeInteractionTextInjection(), recording, dispatcher, scheduler,
        TimeSpan.FromMilliseconds(25));

    await coordinator.StartRecordingAsync();
    await coordinator.StopRecordingAsync();
    scheduler.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(1, recording.StopCalls);

    await coordinator.StartRecordingAsync();
    await coordinator.CancelRecordingAsync();
    scheduler.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(1, recording.StopCalls);
    Assert.Equal(1, recording.CancelCalls);

    await coordinator.StartRecordingAsync();
    scheduler.Advance(TimeSpan.FromMilliseconds(25));
    await coordinator.StopRecordingAsync();
    dispatcher.RunAll();
    Assert.Equal(2, recording.StopCalls);
}

static async Task InteractionDurationLimitDisposal()
{
    var recording = new FakeInteractionRecordingSession();
    var scheduler = new FakeInteractionDurationScheduler();
    var dispatcher = new QueuedInteractionUiDispatcher();
    var coordinator = new LinuxInteractionCoordinator(
        new FakeInteractionShortcutService(), new FakeInteractionPushToTalk(),
        new FakeInteractionTextInjection(), recording, dispatcher, scheduler,
        TimeSpan.FromMilliseconds(25));
    await coordinator.StartRecordingAsync();
    scheduler.Advance(TimeSpan.FromMilliseconds(25));
    coordinator.Dispose();
    dispatcher.RunAll();
    scheduler.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(0, recording.StopCalls);
}

static Task DeviceIdentityHashesMachineId()
{
    var raw = "0123456789abcdef0123456789abcdef"u8.ToArray();
    var provider = new LinuxDeviceIdentityProvider(new FakeMachineIdentitySource(raw), new LinuxPrivateFileService(), "/tmp/not-used", () => new byte[32]);
    var result = provider.GetDeviceIdentity();
    Assert.True(result.IsSuccess); Assert.Equal(DeviceIdentitySource.PlatformMachineId, result.Value!.Source);
    Assert.True(result.Value.Id != "0123456789abcdef0123456789abcdef"); Assert.Equal(64, result.Value.Id.Length);
    return Task.CompletedTask;
}

static Task DeviceIdentityFallback() => WithTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "identity");
    var generated = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    var provider = new LinuxDeviceIdentityProvider(new FakeMachineIdentitySource(null), new LinuxPrivateFileService(), path,
        () => generated);
    var first = provider.GetDeviceIdentity(); var second = provider.GetDeviceIdentity();
    Assert.Equal(DeviceIdentitySource.GeneratedFallback, first.Value!.Source);
    Assert.Equal(DeviceIdentitySource.StoredFallback, second.Value!.Source); Assert.Equal(first.Value.Id, second.Value.Id);
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    Assert.True(generated.All(value => value == 0));
});

static Task HostDeviceIdentity()
{
    var result = new LinuxDeviceIdentityProvider().GetDeviceIdentity();
    Assert.True(result.IsSuccess); Assert.Equal(64, result.Value!.Id.Length);
    Assert.True(!result.Value.Id.Contains('-', StringComparison.Ordinal));
    return Task.CompletedTask;
}

static Task MicrophoneVolumeRoundTrip()
{
    var runner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(0, "Volume: front-left: 32768 / 50% / 0.00 dB, front-right: 39322 / 60% / 0.00 dB\n"u8.ToArray()),
        new ExternalProcessResult(0, []), new ExternalProcessResult(0, []));
    var service = new LinuxMicrophoneVolumeService(runner, "/usr/bin/pactl");
    Assert.Success(service.BoostIfNeeded("mic")); Assert.Success(service.Restore());
    Assert.Equal("set-source-volume,mic,100%", string.Join(',', runner.Calls[1].Arguments));
    Assert.Equal("set-source-volume,mic,50%,60%", string.Join(',', runner.Calls[2].Arguments));
    return Task.CompletedTask;
}

static Task MicrophoneVolumeUnsupported()
{
    var service = new LinuxMicrophoneVolumeService(new FakeDesktopCommandRunner(), null);
    Assert.True(service.ReadLevel("default").IsFailure); Assert.True(service.BoostIfNeeded("default").IsFailure);
    Assert.Success(service.Restore()); return Task.CompletedTask;
}

static Task MicrophoneKeepWarmLifecycle()
{
    var first = new FakeStreamingAudioSource(new BlockingAudioStream());
    var second = new FakeStreamingAudioSource(new BlockingAudioStream());
    var factory = new CyclingStreamingSourceFactory(first, second);
    using var service = new LinuxMicrophoneKeepWarmService(factory);
    Assert.True(service.GetCapabilities().Available);
    service.Configure(true, "mic"); service.SuspendForRecording(); service.ResumeAfterRecording("mic2"); service.Dispose();
    Assert.Equal(2, factory.OpenCalls); Assert.Equal(1, first.TerminateCalls); Assert.Equal(1, second.TerminateCalls);
    return Task.CompletedTask;
}

static Task SoundEffectsPaths() => WithTemporaryDirectory(directory =>
{
    var unsupported = new LinuxSoundEffectsService(new FakeDesktopCommandRunner(), null, directory);
    Assert.True(unsupported.Play(SoundEffect.RecordingStarted).IsFailure);
    File.WriteAllBytes(Path.Combine(directory, "start1.wav"), [1]);
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, []));
    using var supported = new LinuxSoundEffectsService(runner, "/usr/bin/pw-play", directory);
    Assert.True(supported.ConfigureVolume(0.375).IsSuccess);
    Assert.Success(supported.Play(SoundEffect.RecordingStarted));
    Assert.Equal("--volume=0.375", runner.Calls[0].Arguments[0]);
    Assert.Equal(Path.Combine(directory, "start1.wav"), runner.Calls[0].Arguments[1]);
    Assert.True(supported.ConfigureVolume(double.NaN).IsFailure);
    Assert.Equal("--volume=32768", LinuxSoundEffectsService.PlaybackArguments("/usr/bin/paplay", "sound.wav", 0.5)[0]);
    supported.Dispose(); Assert.True(supported.Play(SoundEffect.RecordingStarted).IsFailure);
    Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "start1.wav")));
    Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "stop1.wav")));
});

static async Task PackageUpdateProbe()
{
    var runner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(0, "Installed: 1.2.0-1\nCandidate: 1.3.0-1\n"u8.ToArray()),
        new ExternalProcessResult(0, "Installed: 1.3.0-1\nCandidate: 1.3.0-1\n"u8.ToArray()));
    var probe = new LinuxPackageUpdateProbe(runner, "/usr/bin/apt-cache", null);
    var available = await probe.CheckAsync();
    Assert.Equal(LinuxPackageUpdateState.UpdateAvailable, available.State);
    Assert.Equal("1.2.0-1", available.InstalledVersion);
    Assert.Equal("1.3.0-1", available.CandidateVersion);
    Assert.Equal("policy,hyperwhisper", string.Join(',', runner.Calls[0].Arguments));
    Assert.True(!runner.Calls[0].Arguments.Contains("update"));
    Assert.Equal(LinuxPackageUpdateState.Current, (await probe.CheckAsync()).State);

    var unmanaged = LinuxPackageUpdateProbe.ParseAptPolicy("Installed: (none)\nCandidate: 1.3\n"u8.ToArray());
    Assert.Equal(LinuxPackageUpdateState.NotPackageManaged, unmanaged.State);
    var malformed = LinuxPackageUpdateProbe.ParseAptPolicy("Installed: 1.2 $(bad)\nCandidate: 1.3\n"u8.ToArray());
    Assert.Equal(LinuxPackageUpdateState.NotPackageManaged, malformed.State);

    var packageKitRunner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(0, "Available hyperwhisper;1.4;amd64;updates\n"u8.ToArray()));
    var packageKit = new LinuxPackageUpdateProbe(packageKitRunner, null, "/usr/bin/pkcon");
    Assert.Equal(LinuxPackageUpdateState.UpdateAvailable, (await packageKit.CheckAsync()).State);
    Assert.Equal("--noninteractive,--plain,get-updates", string.Join(',', packageKitRunner.Calls[0].Arguments));
    Assert.Equal(LinuxPackageUpdateState.Unavailable,
        (await new LinuxPackageUpdateProbe(new FakeDesktopCommandRunner(), null, null).CheckAsync()).State);
}

static async Task AudioEnvironmentMuteRestore()
{
    var runner = new FakeDesktopCommandRunner(new ExternalProcessResult(0, "Mute: no\n"u8.ToArray()),
        new ExternalProcessResult(0, []), new ExternalProcessResult(0, []));
    var service = new LinuxAudioEnvironmentService(runner, "/usr/bin/pactl");
    var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.MuteOtherAudio, TimeSpan.Zero);
    Assert.True(prepared.IsSuccess); await prepared.Value!.RestoreAsync(); await prepared.Value.RestoreAsync();
    Assert.Equal(3, runner.Calls.Count);
    Assert.Equal("set-sink-mute,@DEFAULT_SINK@,0", string.Join(',', runner.Calls[2].Arguments));
}

static async Task AudioEnvironmentUnchanged()
{
    var service = new LinuxAudioEnvironmentService(new FakeDesktopCommandRunner(), null);
    var prepared = service.PrepareForRecording(AudioEnvironmentPolicy.Unchanged, TimeSpan.Zero);
    Assert.True(prepared.IsSuccess); await prepared.Value!.RestoreAsync();
    Assert.True(service.PrepareForRecording(AudioEnvironmentPolicy.DuckOtherAudio, TimeSpan.Zero).IsFailure);
    Assert.True(service.PrepareForRecording(AudioEnvironmentPolicy.Unchanged, TimeSpan.FromMilliseconds(-1)).IsFailure);
}

static async Task AudioEnvironmentDuckRestore()
{
    var runner = new FakeDesktopCommandRunner(
        new ExternalProcessResult(0, "Volume: left: 40% right: 55%\n"u8.ToArray()),
        new ExternalProcessResult(0, []), new ExternalProcessResult(0, []));
    var prepared = new LinuxAudioEnvironmentService(runner, "/usr/bin/pactl")
        .PrepareForRecording(AudioEnvironmentPolicy.DuckOtherAudio, TimeSpan.Zero);
    Assert.True(prepared.IsSuccess); await prepared.Value!.DisposeAsync();
    Assert.Equal("set-sink-volume,@DEFAULT_SINK@,35%,35%", string.Join(',', runner.Calls[1].Arguments));
    Assert.Equal("set-sink-volume,@DEFAULT_SINK@,40%,55%", string.Join(',', runner.Calls[2].Arguments));
}

static async Task UiDispatcherContext()
{
    var context = new PumpSynchronizationContext(); var dispatcher = new SynchronizationContextUiDispatcher(context); var reached = 0;
    dispatcher.Post(() => reached++); context.RunOne(); Assert.Equal(1, reached);
    var invoked = dispatcher.InvokeAsync(() => { reached++; return ValueTask.CompletedTask; }).AsTask();
    context.RunOne(); await invoked; Assert.Equal(2, reached);
}

static async Task UiDispatcherCancellation()
{
    var context = new PumpSynchronizationContext(); var dispatcher = new SynchronizationContextUiDispatcher(context);
    var reached = false; using var cancellation = new CancellationTokenSource();
    var pending = dispatcher.InvokeAsync(() => { reached = true; return ValueTask.CompletedTask; }, cancellation.Token).AsTask();
    cancellation.Cancel(); await Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
    context.RunOne(); Assert.True(!reached);
}

static LinuxTextInjectionService NewInjection(FakeClipboard clipboard, IUInputPasteBackend uinput,
    ISecureFieldGuard? guard = null, ICapturedTargetService? targets = null) =>
    new(clipboard, uinput, guard ?? new FakeSecureFieldGuard(SecureFieldState.NotSecure),
        targets ?? new FakeTargetService(TargetFocusState.Ready, TargetFocusState.Ready));

static async Task WithTemporaryDirectory(Action<string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-platform-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        action(directory);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    await Task.CompletedTask;
}

static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-platform-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try { await action(directory); }
    finally { Directory.Delete(directory, recursive: true); }
}

static byte[] Frame(ushort code, int value)
{
    var frame = new byte[EvdevParser.X64FrameSize];
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16, 2), EvdevEvent.KeyType);
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18, 2), code);
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20, 4), value);
    return frame;
}

sealed class FakeEnvironment(string home, IReadOnlyDictionary<string, string> values) : IProcessEnvironment
{
    public string? HomeDirectory => home;
    public string? Get(string name) => values.GetValueOrDefault(name);
}

sealed class FakeUser(uint id) : IUserIdentity
{
    public uint EffectiveUserId => id;
}

sealed class FailingReplace : IAtomicReplace
{
    public void Replace(string temporaryPath, string targetPath) => throw new IOException("simulated");
    public void TryDelete(string path) => File.Delete(path);
}

sealed class FakeSourceFactory(params IEvdevSource[] sources) : IEvdevSourceFactory
{
    public int OpenCalls { get; private set; }
    public PlatformOpenResult OpenKeyboardSources() { OpenCalls++; return new(sources); }
}

sealed class FakeX11Factory(FakeX11Connection connection) : IX11HotkeyConnectionFactory
{ public PlatformResult<IX11HotkeyConnection> Open() => PlatformResult<IX11HotkeyConnection>.Success(connection); }

sealed class FakeX11Connection(params X11HotkeyEvent[] events) : IX11HotkeyConnection
{
    private readonly Queue<X11HotkeyEvent> _events = new(events);
    public List<(byte Keycode, uint Modifiers)> Grabs { get; } = [];
    public byte? FailNextGrabForKeycode { get; set; }
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public byte Keycode(uint keysym) => keysym switch
    {
        0x20 => 65, 0x2e => 60, 0x41 => 38, 0xff1b => 9,
        0xffc5 => 74, 0xffc6 => 75, 0xffc7 => 76,
        0xffe3 => 37, 0xffe4 => 105, _ => 0,
    };
    public bool Grab(byte keycode, uint modifiers)
    {
        if (FailNextGrabForKeycode == keycode)
        {
            FailNextGrabForKeycode = null;
            return false;
        }
        Grabs.Add((keycode, modifiers));
        return true;
    }
    public void UngrabAll() => Grabs.Clear();
    public bool TryRead(out X11HotkeyEvent value)
    { if (_events.TryDequeue(out value)) return true; Drained.TrySetResult(); return false; }
    public void Dispose() { }
}

sealed class FakeSource(string id, params byte[][] frames) : IEvdevSource
{
    private readonly Queue<byte[]> _frames = new(frames);
    public string Id => id;
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        if (!_frames.TryDequeue(out var next))
        {
            Completed.TrySetResult();
            return ValueTask.FromResult(false);
        }
        next.CopyTo(frame);
        return ValueTask.FromResult(true);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class UncooperativeSource : IEvdevSource
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id => "uncooperative";
    public ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken) =>
        new(_completion.Task);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Release() => _completion.TrySetResult(false);
}

sealed class ProbeSource : IEvdevSource
{
    public string Id => "probe";
    public int ReadCount { get; private set; }
    public bool Disposed { get; private set; }
    public ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        ReadCount++;
        return ValueTask.FromResult(false);
    }
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

sealed class FakeDiagnostics : IGlobalShortcutDiagnostics
{
    public int SubscriberFailures { get; private set; }
    public void MalformedFrame() { }
    public void SourceFailed() { }
    public void SubscriberFailed() => SubscriberFailures++;
}

sealed class FakeAppPaths(string root) : IAppPaths
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

sealed class FakePulseApi : IPulseAudioApi
{
    public bool Available { get; init; } = true;
    public FakeRecordSession? RecordSession { get; init; }
    public FakePlaybackSession? PlaybackSession { get; init; }
    public PulseAudioCapabilities GetCapabilities() => new(Available, Available ? "fake" : "none", "test");
    public PlatformResult<IPulseAudioRecordSession> OpenRecord(AudioRecordingOptions options) =>
        PlatformResult<IPulseAudioRecordSession>.Success(RecordSession ?? new FakeRecordSession([]));
    public PlatformResult<IPulseAudioPlaybackSession> OpenPlayback(WaveFormat format) =>
        PlatformResult<IPulseAudioPlaybackSession>.Success(PlaybackSession ?? new FakePlaybackSession());
}

sealed class FakeRecordSession(byte[] bytes) : IPulseAudioRecordSession
{
    private bool _read;
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PlatformResult<int> Read(byte[] buffer)
    {
        if (_read)
        {
            Completed.TrySetResult();
            return PlatformResult<int>.Success(0);
        }
        _read = true;
        bytes.CopyTo(buffer, 0);
        return PlatformResult<int>.Success(bytes.Length);
    }
    public void Dispose() { }
}

sealed class FakePlaybackSession : IPulseAudioPlaybackSession
{
    public int BytesWritten { get; private set; }
    public int DrainCalls { get; private set; }
    public PlatformResult Write(byte[] buffer, int count) { BytesWritten += count; return PlatformResult.Success(); }
    public PlatformResult Drain() { DrainCalls++; return PlatformResult.Success(); }
    public void Dispose() { }
}

sealed class FakeStreamingAudioSourceFactory(FakeStreamingAudioSource source) : IStreamingAudioSourceFactory
{
    public bool IsAvailable => true;
    public string Backend => "fake";
    public PlatformResult<IStreamingAudioSource> Open(AudioRecordingOptions options) =>
        PlatformResult<IStreamingAudioSource>.Success(source);
}

sealed class FakeStreamingAudioSource(Stream output) : IStreamingAudioSource
{
    public Stream Output { get; } = output;
    public int TerminateCalls { get; private set; }
    public ValueTask TerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TerminateCalls++;
        if (Output is BlockingAudioStream blocked) blocked.Release();
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { Output.Dispose(); return ValueTask.CompletedTask; }
}

sealed class BlockingAudioStream : Stream
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => _released.TrySetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { ReadStarted.TrySetResult(); await _released.Task.ConfigureAwait(false); return 0; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class FakeClipboard : ILinuxClipboardBackend
{
    public FakeClipboard(string initial) : this(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        { ["text/plain;charset=utf-8"] = System.Text.Encoding.UTF8.GetBytes(initial) }) { }
    public FakeClipboard(IReadOnlyDictionary<string, byte[]> initial) => Formats = Clone(initial);
    public IReadOnlyDictionary<string, byte[]> Formats { get; private set; }
    public string Text => Formats.TryGetValue("text/plain;charset=utf-8", out var value)
        ? System.Text.Encoding.UTF8.GetString(value) : string.Empty;
    public bool FailWrites { get; init; }
    public bool BlockWrites { get; init; }
    public int RestoreCalls { get; private set; }
    public ClipboardHistoryPrivacyPolicy LastPrivacyPolicy { get; private set; }
    public LinuxTextInjectionCapabilities GetCapabilities() => new(true, "fake", false, true, true, true,
        ClipboardHistoryPrivacyCapability.BestEffortAvailable);
    public ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PlatformResult<ClipboardSnapshot?>.Success(new ClipboardSnapshot(Clone(Formats))));
    public ValueTask<PlatformResult> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCalls++;
        Formats = Clone(snapshot.Formats);
        return ValueTask.FromResult(PlatformResult.Success());
    }
    public async ValueTask<PlatformResult> SetTextAsync(string text, ClipboardHistoryPrivacyPolicy privacyPolicy,
        CancellationToken cancellationToken)
    {
        if (BlockWrites) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (FailWrites) return PlatformResult.Failure("clipboard_failed", "test");
        LastPrivacyPolicy = privacyPolicy;
        var formats = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { ["text/plain;charset=utf-8"] = System.Text.Encoding.UTF8.GetBytes(text) };
        if (privacyPolicy == ClipboardHistoryPrivacyPolicy.BestEffort)
            formats["x-kde-passwordManagerHint"] = "secret"u8.ToArray();
        Formats = formats;
        return PlatformResult.Success();
    }
    private static Dictionary<string, byte[]> Clone(IReadOnlyDictionary<string, byte[]> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
}

sealed class FakeSecureFieldGuard(SecureFieldState state) : ISecureFieldGuard
{
    public bool IsAvailable => true;
    public ValueTask<SecureFieldState> GetFocusedFieldStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(state);
    }
}

sealed class FakeTargetService(params TargetFocusState[] states) : ICapturedTargetService
{
    private readonly Queue<TargetFocusState> _states = new(states);
    public bool CanRestoreFocus => true;
    public PlatformResult<CapturedTarget?> Capture() =>
        PlatformResult<CapturedTarget?>.Success(new CapturedTarget("opaque-test-target"));
    public ValueTask<TargetFocusState> ValidateAndFocusAsync(CapturedTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_states.TryDequeue(out var state) ? state : TargetFocusState.Ready);
    }
}

sealed class FakeAtSpiQuery(params AtSpiFocusInfo?[] focused) : IAtSpiFocusQuery
{
    private readonly Queue<AtSpiFocusInfo?> _focused = new(focused);
    public bool IsAvailable => true;
    public ValueTask<AtSpiFocusInfo?> GetFocusedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_focused.TryDequeue(out var value) ? value : null);
    }
}

sealed class FakeNativeClipboardOwner : INativeClipboardOwner
{
    public bool IsAvailable => true;
    public ClipboardSnapshot? Owned { get; private set; }
    public void Clear() => Owned = null;
    public ValueTask<PlatformResult> OwnAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Owned = snapshot;
        return ValueTask.FromResult(PlatformResult.Success());
    }
    public void Dispose() { }
}

sealed record DesktopCommandCall(string Executable, IReadOnlyList<string> Arguments, byte[]? Input, TimeSpan Timeout);

sealed class FakeDesktopCommandRunner(params object[] outcomes) : IDesktopCommandRunner
{
    private readonly Queue<object> _outcomes = new(outcomes);
    public List<DesktopCommandCall> Calls { get; } = [];
    public Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        byte[]? input, CancellationToken cancellationToken, TimeSpan timeout)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new DesktopCommandCall(executable, arguments.ToArray(), input?.ToArray(), timeout));
        if (!_outcomes.TryDequeue(out var outcome)) throw new InvalidOperationException("No fake command result was configured.");
        if (outcome is Exception exception) return Task.FromException<ExternalProcessResult>(exception);
        return Task.FromResult((ExternalProcessResult)outcome);
    }
}

sealed class FakeCaptureHook(byte[] contents) : IScreenCaptureHook
{
    public PlatformResult? Failure { get; init; }
    public bool Cancel { get; init; }
    public bool UsesPortal { get; init; }
    public UnixFileMode ModeDuringCapture { get; private set; }
    public UnixFileMode DirectoryModeDuringCapture { get; private set; }
    public ScreenCaptureCapabilities GetCapabilities() => new(true, "fake", UsesPortal);
    public ValueTask<PlatformResult> CaptureSelectionAsync(string privateDestinationPath,
        CancellationToken cancellationToken = default)
    {
        if (Cancel) cancellationToken.ThrowIfCancellationRequested();
        ModeDuringCapture = File.GetUnixFileMode(privateDestinationPath);
        DirectoryModeDuringCapture = File.GetUnixFileMode(Path.GetDirectoryName(privateDestinationPath)!);
        File.WriteAllBytes(privateDestinationPath, contents);
        return ValueTask.FromResult(Failure ?? PlatformResult.Success());
    }
}

sealed class FakeGlobalShortcutService : IGlobalShortcutService, IShortcutInterferenceSource
{
    public NamedShortcut? Registered { get; private set; }
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;
    public event EventHandler? Interfered;
    public bool InterferenceArmed { get; private set; }
    public int StartCalls { get; private set; }
    public PlatformResult Start() { StartCalls++; return PlatformResult.Success(); }
    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(IReadOnlyCollection<NamedShortcut> shortcuts)
    { Registered = shortcuts.Single(); return new Dictionary<string, PlatformResult> { [Registered.Name] = PlatformResult.Success() }; }
    public void Emit(string name, bool pressed)
    {
        var shortcut = Registered?.Shortcut ?? new GlobalShortcut(ShortcutModifiers.None, new ShortcutKeyCode("A"));
        var args = new ShortcutTriggeredEventArgs(name, shortcut);
        if (pressed) ShortcutPressed?.Invoke(this, args); else ShortcutReleased?.Invoke(this, args);
    }
    public void EmitInterference() => Interfered?.Invoke(this, EventArgs.Empty);
    public void SetInterferenceArmed(bool armed) => InterferenceArmed = armed;
    public void Clear() => Registered = null;
    public void ResetKeyboardState() { }
    public void Dispose() { }
}

sealed class FakeInteractionShortcutService : IGlobalShortcutService
{
    public IReadOnlyList<NamedShortcut> Current { get; private set; } = [];
    public List<IReadOnlyList<NamedShortcut>> RegistrationHistory { get; } = [];
    public string? FailNextName { get; set; }
    public Action<IReadOnlyCollection<NamedShortcut>>? Registering { get; set; }
    public int StartCalls { get; private set; }
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutPressed;
    public event EventHandler<ShortcutTriggeredEventArgs>? ShortcutReleased;
    public PlatformResult Start() { StartCalls++; return PlatformResult.Success(); }
    public IReadOnlyDictionary<string, PlatformResult> RegisterShortcuts(IReadOnlyCollection<NamedShortcut> shortcuts)
    {
        var snapshot = shortcuts.ToArray();
        RegistrationHistory.Add(snapshot);
        Current = snapshot.Where(item => item.Name != FailNextName).ToArray();
        var results = snapshot.ToDictionary(
            item => item.Name,
            item => item.Name == FailNextName
                ? PlatformResult.Failure("shortcut.conflict", "The shortcut is already registered.")
                : PlatformResult.Success(),
            StringComparer.Ordinal);
        FailNextName = null;
        Registering?.Invoke(snapshot);
        return results;
    }
    public void Emit(string name, bool pressed)
    {
        var configured = Current.FirstOrDefault(item => item.Name == name);
        if (configured is null) return;
        EmitUnregistered(name, configured.Shortcut, pressed);
    }
    public void EmitUnregistered(string name, GlobalShortcut shortcut, bool pressed = true)
    {
        var args = new ShortcutTriggeredEventArgs(name, shortcut);
        if (pressed) ShortcutPressed?.Invoke(this, args); else ShortcutReleased?.Invoke(this, args);
    }
    public void Clear() => Current = [];
    public void ResetKeyboardState() { }
    public void Dispose() { }
}

sealed class FakeInteractionPushToTalk : IPushToTalkMonitor
{
    public int StartCalls { get; private set; }
    public PushToTalkConfiguration Configuration { get; private set; } = new(PushToTalkMode.Disabled);
    public event EventHandler? Pressed { add { } remove { } }
    public event EventHandler? Released { add { } remove { } }
    public event EventHandler? Interfered { add { } remove { } }
    public void Configure(PushToTalkConfiguration configuration) => Configuration = configuration;
    public PlatformResult Start() { StartCalls++; return PlatformResult.Success(); }
    public void Reset() { }
    public void ResetToIdle() { }
    public void Dispose() { }
}

sealed class FakeInteractionRecordingSession : IInteractionRecordingSession
{
    public bool Active { get; set; }
    public bool IsActive => Active;
    public bool Streaming { get; set; }
    public bool IsStreaming => Streaming;
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int CancelCalls { get; private set; }
    public int CancelRequestCalls { get; private set; }
    public bool DeferCancelRequest { get; set; }
    public PlatformResult StartResult { get; set; } = PlatformResult.Success();
    public List<InteractionRecordingKind> StartKinds { get; } = [];
    public ValueTask<PlatformResult> StartAsync(
        InteractionRecordingKind kind,
        CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); StartCalls++; StartKinds.Add(kind); Streaming = kind == InteractionRecordingKind.Streaming; Active = StartResult.IsSuccess; return ValueTask.FromResult(StartResult); }
    public ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); StopCalls++; Active = false; return ValueTask.FromResult(new InteractionStopOutcome(PlatformResult.Success())); }
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); CancelCalls++; Active = false; return ValueTask.CompletedTask; }
    public ValueTask<bool> RequestCancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelRequestCalls++;
        if (DeferCancelRequest) return ValueTask.FromResult(false);
        CancelCalls++; Active = false;
        return ValueTask.FromResult(true);
    }
}

sealed class BlockingInteractionRecordingSession : IInteractionRecordingSession
{
    public bool IsActive => false;
    public List<InteractionRecordingKind> StartKinds { get; } = [];
    public TaskCompletionSource StreamingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StreamingCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<PlatformResult> StartAsync(
        InteractionRecordingKind kind,
        CancellationToken cancellationToken = default)
    {
        StartKinds.Add(kind);
        if (kind == InteractionRecordingKind.Batch) return PlatformResult.Success();
        StreamingStarted.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StreamingCancelled.TrySetResult();
            throw;
        }
        return PlatformResult.Success();
    }
    public ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new InteractionStopOutcome(PlatformResult.Success()));
    public ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

sealed class FakeInteractionDurationScheduler : IInteractionDurationScheduler
{
    private readonly List<ScheduledInteractionDuration> _scheduled = [];
    private TimeSpan _now;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var scheduled = new ScheduledInteractionDuration(_now + delay, callback);
        _scheduled.Add(scheduled);
        return scheduled;
    }

    public void Advance(TimeSpan amount)
    {
        _now += amount;
        var due = _scheduled.Where(item => !item.IsDisposed && !item.HasRun && item.Due <= _now).ToArray();
        foreach (var item in due)
        {
            item.HasRun = true;
            item.Callback();
        }
    }

    private sealed class ScheduledInteractionDuration(TimeSpan due, Action callback) : IDisposable
    {
        public TimeSpan Due { get; } = due;
        public Action Callback { get; } = callback;
        public bool HasRun { get; set; }
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}

sealed class FakeInteractionTextInjection : ITextInjectionService
{
    public int EndSessionCalls { get; private set; }
    public bool IsCapturedTargetAvailable => true;
    public void CaptureTarget() { }
    public void StartSession() { }
    public void EndSession() => EndSessionCalls++;
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) { }
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(PlatformResult.Success()); }
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(PlatformResult.Success()); }
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(TextInjectionOutcome.Pasted); }
    public void Dispose() { }
}

sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return action(); }
}

sealed class QueuedInteractionUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _actions = new();
    public bool CheckAccess() => true;
    public void Post(Action action) => _actions.Enqueue(action);
    public ValueTask InvokeAsync(Func<ValueTask> action, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return action(); }
    public void RunAll()
    {
        while (_actions.TryDequeue(out var action)) action();
    }
}

sealed class FakePushToTalkScheduler : IPushToTalkScheduler
{
    private readonly List<Scheduled> _scheduled = [];
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
    public IDisposable Schedule(TimeSpan delay, Action action)
    { var value = new Scheduled(Now + delay, action); _scheduled.Add(value); return value; }
    public void Advance(TimeSpan amount)
    {
        Now += amount;
        while (_scheduled.Where(value => !value.Cancelled && value.Due <= Now).OrderBy(value => value.Due).FirstOrDefault() is { } next)
        { _scheduled.Remove(next); next.Fire(); }
        _scheduled.RemoveAll(value => value.Cancelled);
    }
    private sealed class Scheduled(DateTimeOffset due, Action action) : IDisposable
    {
        private Action? _action = action;
        public DateTimeOffset Due { get; } = due;
        public bool Cancelled => _action is null;
        public void Fire() => Interlocked.Exchange(ref _action, null)?.Invoke();
        public void Dispose() => _action = null;
    }
}

sealed class FakeMachineIdentitySource(byte[]? raw) : IMachineIdentitySource
{
    public byte[]? ReadRaw() => raw?.ToArray();
}

sealed class CyclingStreamingSourceFactory(params FakeStreamingAudioSource[] sources) : IStreamingAudioSourceFactory
{
    private readonly Queue<FakeStreamingAudioSource> _sources = new(sources);
    public int OpenCalls { get; private set; }
    public bool IsAvailable => true;
    public string Backend => "fake";
    public PlatformResult<IStreamingAudioSource> Open(AudioRecordingOptions options)
    { OpenCalls++; return _sources.TryDequeue(out var source) ? PlatformResult<IStreamingAudioSource>.Success(source)
        : PlatformResult<IStreamingAudioSource>.Failure("fake_empty", "test"); }
}

sealed class PumpSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public void RunOne()
    {
        var work = _queue.Dequeue(); var prior = Current;
        try { SetSynchronizationContext(this); work.Callback(work.State); }
        finally { SetSynchronizationContext(prior); }
    }
}

sealed class FakeUInput(bool succeeds) : IUInputPasteBackend
{
    public bool IsAvailable => succeeds;
    public int PasteCalls { get; private set; }
    public PlatformResult Paste()
    {
        PasteCalls++;
        return succeeds ? PlatformResult.Success() : PlatformResult.Failure("uinput_unavailable", "test");
    }
}

sealed class ThrowingUInput : IUInputPasteBackend
{
    public bool IsAvailable => true;
    public PlatformResult Paste() => throw new IOException("simulated");
}

static class Assert
{
    public static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
    public static void Success(PlatformResult result)
    {
        if (result.IsFailure) throw new InvalidOperationException($"{result.Error!.Code}: {result.Error.Message}");
    }
    public static void SequenceEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Byte sequences differ.");
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
