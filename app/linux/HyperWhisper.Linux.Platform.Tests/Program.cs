using System.Buffers.Binary;
using System.Runtime.Versioning;
using HyperWhisper.Linux.Platform.Files;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
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
    ("injection falls back losslessly to clipboard", InjectionClipboardFallback),
    ("injection uses uinput after clipboard", InjectionUsesUInput),
    ("injection restores captured clipboard", InjectionRestoresClipboard),
    ("clipboard failure prevents uinput", ClipboardFailurePreventsUInput),
    ("uinput exception preserves clipboard fallback", UInputExceptionFallsBack),
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

static async Task InjectionClipboardFallback()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(false);
    using var service = new LinuxTextInjectionService(clipboard, uinput);
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal("transcript", clipboard.Text);
    Assert.Equal(1, uinput.PasteCalls);
}

static async Task InjectionUsesUInput()
{
    var clipboard = new FakeClipboard("old");
    var uinput = new FakeUInput(true);
    using var service = new LinuxTextInjectionService(clipboard, uinput);
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.Pasted, outcome);
    Assert.Equal("transcript", clipboard.Text);
    Assert.Equal(1, uinput.PasteCalls);
}

static async Task InjectionRestoresClipboard()
{
    var clipboard = new FakeClipboard("old");
    using var service = new LinuxTextInjectionService(clipboard, new FakeUInput(false));
    service.StartSession();
    await service.CopyToClipboardAsync("transcript");
    Assert.Success(await service.RestoreClipboardImmediatelyAsync());
    Assert.Equal("old", clipboard.Text);
}

static async Task ClipboardFailurePreventsUInput()
{
    var clipboard = new FakeClipboard("old") { FailWrites = true };
    var uinput = new FakeUInput(true);
    using var service = new LinuxTextInjectionService(clipboard, uinput);
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.Failed, outcome);
    Assert.Equal(0, uinput.PasteCalls);
}

static async Task UInputExceptionFallsBack()
{
    var clipboard = new FakeClipboard("old");
    using var service = new LinuxTextInjectionService(clipboard, new ThrowingUInput());
    var outcome = await service.InjectTranscriptAsync("transcript");
    Assert.Equal(TextInjectionOutcome.CopiedToClipboard, outcome);
    Assert.Equal("transcript", clipboard.Text);
}

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

sealed class FakeClipboard(string initial) : ILinuxClipboardBackend
{
    public string Text { get; private set; } = initial;
    public bool FailWrites { get; init; }
    public LinuxTextInjectionCapabilities GetCapabilities() => new(true, "fake", false, false, false);
    public ValueTask<PlatformResult<ClipboardSnapshot?>> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PlatformResult<ClipboardSnapshot?>.Success(new ClipboardSnapshot(Text)));
    public ValueTask<PlatformResult> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (FailWrites) return ValueTask.FromResult(PlatformResult.Failure("clipboard_failed", "test"));
        Text = text;
        return ValueTask.FromResult(PlatformResult.Success());
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
}
