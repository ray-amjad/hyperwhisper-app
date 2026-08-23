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
    ("private writes create exact 0600 files", PrivateFileMode),
    ("private overwrite restores exact 0600", PrivateOverwriteMode),
    ("atomic failure preserves prior contents", AtomicFailurePreservesTarget),
    ("private reads reject permissive files", RejectPermissiveRead),
    ("evdev drops unrelated keys at boundary", DropsUnrelatedKeys),
    ("evdev emits configured logical shortcut", EmitsConfiguredShortcut),
    ("X11 mapper preserves logical shortcut privacy", X11ShortcutPrivacy),
    ("X11 modifier-only shortcuts emit press and release", X11ModifierShortcut),
    ("true Xorg selects XGrabKey instead of evdev", XorgSelectsXGrabKey),
    ("X11 XGrabKey host integration", X11GrabIntegration),
    ("X11 Display mutation is serialized with reader", X11ConcurrentMutationIntegration),
    ("StatusNotifierItem action protocol is bounded", StatusNotifierProtocol),
    ("event dispatch isolates failing subscribers", IsolatesSubscribers),
    ("Pulse recorder writes private canonical WAV", PulseRecorderWritesWave),
    ("Pulse recorder reports unavailable capability", PulseRecorderUnavailable),
    ("Pulse playback delegates PCM and ends safely", PulsePlaybackDelegates),
    ("Pulse playback isolates failing subscribers", PulsePlaybackSubscriberSafety),
    ("injection falls back losslessly to clipboard", InjectionClipboardFallback),
    ("injection uses uinput after clipboard", InjectionUsesUInput),
    ("injection restores captured clipboard", InjectionRestoresClipboard),
    ("injection restores every captured MIME format", InjectionRestoresAllFormats),
    ("injection refuses a secure field before clipboard mutation", InjectionRefusesSecureField),
    ("injection falls back when captured target is lost", InjectionTargetLost),
    ("injection falls back when target changes before paste", InjectionTargetChanged),
    ("injection propagates cancellation", InjectionCancellation),
    ("disposing injection cancels scheduled restore", InjectionDisposalSafety),
    ("Wayland AT-SPI target accepts stable focused identity", AtSpiTargetStable),
    ("Wayland AT-SPI target rejects changed identity", AtSpiTargetChanged),
    ("clipboard failure prevents uinput", ClipboardFailurePreventsUInput),
    ("uinput exception preserves clipboard fallback", UInputExceptionFallsBack),
    ("Wayland helper advertises partial multi-MIME restore", CommandClipboardCapability),
    ("native X11 owner receives every MIME format", NativeX11Restore),
    ("native X11 owner serves every MIME format", NativeX11RoundTrip),
    ("external desktop helpers have a hard timeout", ExternalHelperTimeout),
    ("X11 application context parses active window safely", X11ApplicationContext),
    ("Wayland application context uses AT-SPI", WaylandApplicationContext),
    ("GNOME Wayland prefers companion D-Bus", GnomeWaylandApplicationContext),
    ("KDE Wayland prefers KWin companion D-Bus", KdeWaylandApplicationContext),
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
    Assert.True(connection.Grabs.All(value => value.Modifiers == 1u << 15));
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
    if (!window.HasExited) window.Kill();
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

static Task CommandClipboardCapability()
{
    using var backend = new CommandClipboardBackend("/bin/true", "/bin/true", true, null);
    Assert.True(!backend.GetCapabilities().PreservesAllClipboardFormats);
    return Task.CompletedTask;
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
    using var owner = new NativeX11ClipboardOwner();
    Assert.True(owner.IsAvailable);
    var snapshot = new ClipboardSnapshot(new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["text/plain"] = "native-text"u8.ToArray(),
        ["image/png"] = [0x89, 0x50, 0x4e, 0x47],
    });
    Assert.Success(await owner.OwnAsync(snapshot, CancellationToken.None));
    var targets = await ExternalProcessRunner.RunAsync(xclip,
        ["-selection", "clipboard", "-target", "TARGETS", "-out"], null, CancellationToken.None);
    var targetNames = System.Text.Encoding.UTF8.GetString(targets.Output);
    Assert.True(targetNames.Contains("text/plain", StringComparison.Ordinal));
    Assert.True(targetNames.Contains("image/png", StringComparison.Ordinal));
    var image = await ExternalProcessRunner.RunAsync(xclip,
        ["-selection", "clipboard", "-target", "image/png", "-out"], null, CancellationToken.None);
    Assert.SequenceEqual(snapshot.Formats["image/png"], image.Output);
}

static async Task ExternalHelperTimeout()
{
    await Assert.ThrowsAsync<TimeoutException>(async () =>
        await ExternalProcessRunner.RunAsync("/bin/sh", ["-c", "sleep 30"], null,
            CancellationToken.None, TimeSpan.FromMilliseconds(50)));
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
    Assert.True(service.GetCapabilities().UsesDesktopPortal);
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
    Assert.Success(supported.Play(SoundEffect.RecordingStarted));
    Assert.Equal(Path.Combine(directory, "start1.wav"), runner.Calls[0].Arguments[0]);
    supported.Dispose(); Assert.True(supported.Play(SoundEffect.RecordingStarted).IsFailure);
    Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "start1.wav")));
    Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "stop1.wav")));
});

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
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public byte Keycode(uint keysym) => keysym switch { 0x41 => 38, 0xffe3 => 37, 0xffe4 => 105, _ => 0 };
    public bool Grab(byte keycode, uint modifiers) { Grabs.Add((keycode, modifiers)); return true; }
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
    public LinuxTextInjectionCapabilities GetCapabilities() => new(true, "fake", false, true, true, true);
    public ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PlatformResult<ClipboardSnapshot?>.Success(new ClipboardSnapshot(Clone(Formats))));
    public ValueTask<PlatformResult> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCalls++;
        Formats = Clone(snapshot.Formats);
        return ValueTask.FromResult(PlatformResult.Success());
    }
    public async ValueTask<PlatformResult> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (BlockWrites) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (FailWrites) return PlatformResult.Failure("clipboard_failed", "test");
        Formats = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { ["text/plain;charset=utf-8"] = System.Text.Encoding.UTF8.GetBytes(text) };
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
