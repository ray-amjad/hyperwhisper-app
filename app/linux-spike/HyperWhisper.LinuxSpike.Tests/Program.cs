using System.Buffers.Binary;
using HyperWhisper.LinuxSpike.ActiveApp;
using HyperWhisper.LinuxSpike.Audio;
using HyperWhisper.LinuxSpike.Hotkeys;
using HyperWhisper.LinuxSpike.Injection;

var tests = new (string Name, Func<Task> Run)[]
{
    ("unconfigured keys do not cross the privacy boundary", HotkeyPrivacyBoundary),
    ("configured chord emits action identity only", ConfiguredChord),
    ("evdev x64 frames parse deterministically", EvdevFrameParsing),
    ("monitor emits configured actions and drops unrelated frames", MonitorPrivacyBoundary),
    ("uinput failure preserves clipboard fallback", InjectionFallback),
    ("uinput exceptions preserve clipboard fallback", InjectionExceptionFallback),
    ("successful uinput reports injected", InjectionSuccess),
    ("PulseAudio seam gates unavailable native library", PulseUnavailable),
    ("PulseAudio seam delegates capture", PulseDelegates),
    ("GNOME Wayland reports explicit fallback", GnomeFallback),
    ("KDE Wayland reports full D-Bus capability", KdeCapability),
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

static Task HotkeyPrivacyBoundary()
{
    var filter = new HotkeyPrivacyFilter(
        [new HotkeyBinding("record", 30, new HashSet<ushort> { 29 })]);

    var signals = filter.Process(new EvdevInputEvent(EvdevInputEvent.KeyType, 48, 1));
    Assert.Equal(0, signals.Count);
    signals = filter.Process(new EvdevInputEvent(EvdevInputEvent.KeyType, 48, 0));
    Assert.Equal(0, signals.Count);
    return Task.CompletedTask;
}

static Task ConfiguredChord()
{
    var filter = new HotkeyPrivacyFilter(
        [new HotkeyBinding("record", 30, new HashSet<ushort> { 29 })]);

    Assert.Equal(0, filter.Process(new EvdevInputEvent(EvdevInputEvent.KeyType, 29, 1)).Count);
    var pressed = filter.Process(new EvdevInputEvent(EvdevInputEvent.KeyType, 30, 1));
    Assert.Equal(new HotkeySignal("record", HotkeySignalKind.Pressed), pressed.Single());
    var released = filter.Process(new EvdevInputEvent(EvdevInputEvent.KeyType, 29, 0));
    Assert.Equal(new HotkeySignal("record", HotkeySignalKind.Released), released.Single());
    return Task.CompletedTask;
}

static async Task MonitorPrivacyBoundary()
{
    var source = new FakeEvdevFrameSource(
        EvdevFrame(code: 48, value: 1),
        EvdevFrame(code: 48, value: 0),
        EvdevFrame(code: 29, value: 1),
        EvdevFrame(code: 30, value: 1),
        EvdevFrame(code: 30, value: 0));
    await using var monitor = new EvdevHotkeyMonitor(
        source,
        [new HotkeyBinding("record", 30, new HashSet<ushort> { 29 })]);
    var signals = new List<HotkeySignal>();
    monitor.Signal += (_, signal) => signals.Add(signal);

    await monitor.RunAsync(CancellationToken.None);

    Assert.Equal(2, signals.Count);
    Assert.Equal(new HotkeySignal("record", HotkeySignalKind.Pressed), signals[0]);
    Assert.Equal(new HotkeySignal("record", HotkeySignalKind.Released), signals[1]);
}

static Task EvdevFrameParsing()
{
    var frame = new byte[EvdevEventParser.X64FrameSize];
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16, 2), EvdevInputEvent.KeyType);
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18, 2), 30);
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20, 4), 1);

    Assert.True(EvdevEventParser.TryParseX64(frame, out var inputEvent));
    Assert.Equal((ushort)30, inputEvent.Code);
    Assert.Equal(1, inputEvent.Value);
    Assert.False(EvdevEventParser.TryParseX64(frame.AsSpan(1), out _));
    return Task.CompletedTask;
}

static async Task InjectionFallback()
{
    var clipboard = new FakeClipboard(true);
    var injector = new TranscriptInjector(clipboard, new FakeUInput(false));
    var result = await injector.InjectAsync("known transcript");

    Assert.Equal(InjectionOutcome.ClipboardOnly, result.Outcome);
    Assert.Equal("known transcript", clipboard.Text);
}

static async Task InjectionSuccess()
{
    var injector = new TranscriptInjector(new FakeClipboard(true), new FakeUInput(true));
    var result = await injector.InjectAsync("known transcript");
    Assert.Equal(InjectionOutcome.Injected, result.Outcome);
}

static async Task InjectionExceptionFallback()
{
    var clipboard = new FakeClipboard(true);
    var injector = new TranscriptInjector(clipboard, new ThrowingUInput());
    var result = await injector.InjectAsync("known transcript");

    Assert.Equal(InjectionOutcome.ClipboardOnly, result.Outcome);
    Assert.Equal("uinput-error", result.Reason);
    Assert.Equal("known transcript", clipboard.Text);
}

static async Task PulseUnavailable()
{
    var backend = new FakePulseBackend();
    var service = new PulseAudioCaptureService(new FakeLibraryProbe(false), backend);
    Assert.False(service.GetCapability().Available);
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => service.CaptureAsync(new AudioCaptureRequest(null), new MemoryStream()));
    Assert.False(backend.Called);
}

static async Task PulseDelegates()
{
    var backend = new FakePulseBackend();
    var service = new PulseAudioCaptureService(new FakeLibraryProbe(true), backend);
    await service.CaptureAsync(new AudioCaptureRequest("default"), new MemoryStream());
    Assert.True(backend.Called);
}

static Task GnomeFallback()
{
    var capability = new ActiveAppCapabilityReporter(
        new FakeEnvironment(new Dictionary<string, string>
        {
            ["XDG_SESSION_TYPE"] = "wayland",
            ["XDG_CURRENT_DESKTOP"] = "GNOME",
        }),
        new FakeActiveAppBackends()).GetCapability();

    Assert.Equal(ActiveAppCapabilityLevel.DefaultModeFallback, capability.Level);
    Assert.Equal("gnome-wayland", capability.Backend);
    return Task.CompletedTask;
}

static Task KdeCapability()
{
    var capability = new ActiveAppCapabilityReporter(
        new FakeEnvironment(new Dictionary<string, string>
        {
            ["XDG_SESSION_TYPE"] = "wayland",
            ["XDG_CURRENT_DESKTOP"] = "KDE",
        }),
        new FakeActiveAppBackends { KdeDbusAvailable = true }).GetCapability();

    Assert.Equal(ActiveAppCapabilityLevel.Full, capability.Level);
    Assert.Equal("kde-dbus", capability.Backend);
    return Task.CompletedTask;
}

static byte[] EvdevFrame(ushort code, int value)
{
    var frame = new byte[EvdevEventParser.X64FrameSize];
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16, 2), EvdevInputEvent.KeyType);
    BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18, 2), code);
    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20, 4), value);
    return frame;
}

sealed class FakeClipboard(bool succeeds) : IClipboardWriter
{
    public string? Text { get; private set; }

    public Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken)
    {
        if (succeeds)
        {
            Text = text;
        }

        return Task.FromResult(succeeds);
    }
}

sealed class FakeUInput(bool succeeds) : IUInputPasteBackend
{
    public Task<bool> TryPasteAsync(CancellationToken cancellationToken) => Task.FromResult(succeeds);
}

sealed class ThrowingUInput : IUInputPasteBackend
{
    public Task<bool> TryPasteAsync(CancellationToken cancellationToken) =>
        throw new IOException("Simulated device failure.");
}

sealed class FakeEvdevFrameSource(params byte[][] frames) : IEvdevFrameSource
{
    private readonly Queue<byte[]> _frames = new(frames);

    public ValueTask<bool> ReadFrameAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        if (!_frames.TryDequeue(out var next))
        {
            return ValueTask.FromResult(false);
        }

        next.CopyTo(frame);
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeLibraryProbe(bool available) : INativeLibraryProbe
{
    public bool CanLoad(string libraryName) => available;
}

sealed class FakePulseBackend : IPulseAudioCaptureBackend
{
    public bool Called { get; private set; }

    public Task CaptureAsync(
        AudioCaptureRequest request,
        Stream destination,
        CancellationToken cancellationToken)
    {
        Called = true;
        return Task.CompletedTask;
    }
}

sealed class FakeEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentReader
{
    public string? Get(string name) => values.GetValueOrDefault(name);
}

sealed class FakeActiveAppBackends : IActiveAppBackendProbe
{
    public bool X11Available { get; init; }

    public bool KdeDbusAvailable { get; init; }

    public bool GnomeExtensionAvailable { get; init; }
}

static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void False(bool condition) => True(!condition);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
