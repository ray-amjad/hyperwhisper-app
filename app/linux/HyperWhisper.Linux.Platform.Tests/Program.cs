using System.Buffers.Binary;
using System.Runtime.Versioning;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Desktop;
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
    ("Wayland context reports unsupported without AT-SPI", WaylandContextUnsupported),
    ("active application timeout is explicit", ApplicationContextTimeout),
    ("OCR uses private files, truncates, and cleans up", OcrPrivateCleanup),
    ("OCR capture failure cleans up", OcrCaptureFailureCleanup),
    ("OCR cancellation propagates and cleans up", OcrCancellationCleanup),
    ("OCR exposes portal capture hook capability", OcrPortalCapability),
    ("Xvfb active-window integration", X11ApplicationContextIntegration),
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

static async Task X11ApplicationContextIntegration()
{
    if (Environment.GetEnvironmentVariable("HYPERWHISPER_X11_INTEGRATION") != "1") return;
    using var provider = new LinuxApplicationContextProvider();
    var result = await provider.GatherAsync();
    Assert.True(result.IsSuccess && result.Value is not null);
    Assert.True(result.Value!.WindowTitle.Contains("HyperWhisper Context Test", StringComparison.Ordinal));
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
    public PlatformOpenResult OpenKeyboardSources() => new(sources);
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
