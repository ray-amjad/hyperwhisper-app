using System.Reflection;
using System.Globalization;
using HyperWhisper.Linux.Overlay;
using HyperWhisper.Linux.Localization;

var tests = new (string Name, Func<Task> Run)[]
{
    ("mode labels are bounded and control-free", ModeLabelsAreSanitized),
    ("recording duration and transcribing states advance", RecordingDurationAdvances),
    ("mode changes replace and resume recording", ModeChangeResumesRecording),
    ("errors are normalized and auto-hide", ErrorsAreNormalized),
    ("expired feedback cannot overwrite its replacement", ExpiredFeedbackCannotOverwriteReplacement),
    ("cancel feedback transitions to hidden", CancelTransitionsToHidden),
    ("render and dispatch failures never escape", FailuresAreBestEffort),
    ("dispose cancels feedback and owns surface lifetime", DisposalIsSafe),
    ("public event API cannot accept content payloads", PublicApiIsContentFree),
    ("placement defaults to safe bottom center", PlacementDefaultsSafely),
    ("placement restores across work areas and scales", PlacementRestoresAcrossScales),
    ("invalid and offscreen placement is clamped", PlacementIsValidatedAndClamped),
    ("placement writes are debounced", PlacementWritesAreDebounced),
    ("placement persists in the private config store", PlacementRoundTripsPrivately),
    ("placement store failures are isolated", PlacementStoreFailuresAreIsolated),
    ("overlay interaction policy cannot activate or focus", OverlayDoesNotActivate),
};

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} overlay tests passed");
return failed == 0 ? 0 : 1;

static Task ModeLabelsAreSanitized()
{
    var label = LinuxOverlayModeLabel.Create("  Hyper\0\r\nMode " + new string('x', 100));
    Assert(label.Value.Length <= LinuxOverlayModeLabel.MaximumCharacters, "mode label exceeded its bound");
    Assert(!label.Value.Any(char.IsControl), "mode label retained control characters");
    Assert(LinuxOverlayModeLabel.Create("\0\r").Value == "Default", "empty label did not use safe fallback");
    return Task.CompletedTask;
}

static Task RecordingDurationAdvances()
{
    var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    using var fixture = OverlayFixture.Create(() => now);
    fixture.Controller.ShowRecording(LinuxOverlayModeLabel.Create("Hyper"));
    Assert(fixture.ViewModel.State == LinuxRecordingOverlayState.Recording, "recording was not shown");
    Assert(fixture.ViewModel.DurationText == "00:00", "recording did not start at zero");
    now = now.AddSeconds(65);
    fixture.Controller.TickDuration();
    Assert(fixture.ViewModel.DurationText == "01:05", "recording duration was formatted incorrectly");
    fixture.Controller.ShowTranscribing();
    Assert(fixture.ViewModel.State == LinuxRecordingOverlayState.Transcribing, "transcribing was not shown");
    Assert(fixture.ViewModel.StatusText == TestText.Get("recording.state.transcribing"), "transcribing text drifted");
    return Task.CompletedTask;
}

static async Task ModeChangeResumesRecording()
{
    var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    using var fixture = OverlayFixture.Create(() => now);
    fixture.Controller.ShowRecording(LinuxOverlayModeLabel.Create("Default"));
    now = now.AddSeconds(3);
    fixture.Controller.TickDuration();
    fixture.Controller.ShowModeChanged(LinuxOverlayModeLabel.Create("Draft"));
    Assert(fixture.ViewModel.State == LinuxRecordingOverlayState.ModeChanged, "mode toast was not shown");
    fixture.Controller.ShowModeChanged(LinuxOverlayModeLabel.Create("Coding"));
    Assert(fixture.ViewModel.ModeText == "Coding", "mode name was lost");
    fixture.Delay.CompleteLatest();
    await WaitUntil(() => fixture.ViewModel.State == LinuxRecordingOverlayState.Recording);
    Assert(fixture.ViewModel.ModeText == "Coding" && fixture.ViewModel.DurationText == "00:03",
        "recording did not resume after mode toast");
}

static async Task ErrorsAreNormalized()
{
    using var fixture = OverlayFixture.Create();
    fixture.Controller.ShowError(LinuxRecordingOverlayError.ProviderUnavailable);
    Assert(fixture.ViewModel.State == LinuxRecordingOverlayState.Error, "error was not shown");
    Assert(fixture.ViewModel.StatusText == TestText.Get("linux.overlay.error.provider"), "error text was not normalized");
    fixture.Delay.CompleteLatest();
    await WaitUntil(() => fixture.ViewModel.State == LinuxRecordingOverlayState.Hidden);
}

static async Task ExpiredFeedbackCannotOverwriteReplacement()
{
    var viewModel = new LinuxRecordingOverlayViewModel();
    var surface = new FakeSurface();
    var delay = new FakeDelay();
    using var dispatcher = new BlockingDispatcher();
    using var controller = new LinuxRecordingOverlayController(viewModel, dispatcher, surface,
        delay, () => DateTimeOffset.UtcNow, false, TestText.Get);
    controller.ShowModeChanged(LinuxOverlayModeLabel.Create("Old"));
    var oldTimer = delay.Latest ?? throw new InvalidOperationException("old timer was not created");

    dispatcher.BlockNextPost();
    var replacement = Task.Run(() => controller.ShowError(LinuxRecordingOverlayError.RecordingFailed));
    Assert(dispatcher.WaitUntilBlocked(), "replacement render did not enter the race gate");
    _ = oldTimer.TrySetResult();
    await Task.Delay(50);
    Assert(viewModel.State == LinuxRecordingOverlayState.Error,
        "expired feedback overwrote the replacement state");
    dispatcher.Release();
    await replacement;
    Assert(viewModel.State == LinuxRecordingOverlayState.Error,
        "replacement state changed after its timer was installed");
}

static async Task CancelTransitionsToHidden()
{
    using var fixture = OverlayFixture.Create();
    fixture.Controller.ShowRecording(LinuxOverlayModeLabel.Create("Default"));
    fixture.Controller.Cancel();
    Assert(fixture.ViewModel.State == LinuxRecordingOverlayState.Cancelled, "cancel feedback was not shown");
    fixture.Delay.CompleteLatest();
    await WaitUntil(() => fixture.ViewModel.State == LinuxRecordingOverlayState.Hidden);
}

static Task FailuresAreBestEffort()
{
    var viewModel = new LinuxRecordingOverlayViewModel();
    using (var controller = new LinuxRecordingOverlayController(viewModel, new ImmediateDispatcher(),
        new ThrowingSurface(), new FakeDelay(), () => DateTimeOffset.UtcNow, false, TestText.Get))
        controller.ShowRecording(LinuxOverlayModeLabel.Create("Safe"));
    Assert(viewModel.State == LinuxRecordingOverlayState.Recording, "surface failure prevented state transition");

    using var dispatchFailure = new LinuxRecordingOverlayController(new(), new ThrowingDispatcher(),
        new FakeSurface(), new FakeDelay(), () => DateTimeOffset.UtcNow, false, TestText.Get);
    dispatchFailure.ShowTranscribing();
    dispatchFailure.Hide();
    return Task.CompletedTask;
}

static Task DisposalIsSafe()
{
    var fixture = OverlayFixture.Create();
    fixture.Controller.ShowError(LinuxRecordingOverlayError.Unknown);
    fixture.Controller.Dispose();
    fixture.Controller.Dispose();
    fixture.Delay.CompleteLatest();
    Assert(fixture.Surface.DisposeCalls == 1 && fixture.Surface.HideCalls >= 1,
        "surface lifetime was not owned idempotently");
    fixture.Dispose();
    return Task.CompletedTask;
}

static Task PublicApiIsContentFree()
{
    var forbidden = new[] { typeof(string), typeof(byte[]), typeof(Stream), typeof(ReadOnlyMemory<byte>) };
    var methods = typeof(LinuxRecordingOverlayController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.DeclaringType == typeof(LinuxRecordingOverlayController) && !method.IsSpecialName);
    foreach (var parameter in methods.SelectMany(method => method.GetParameters()))
        Assert(!forbidden.Contains(parameter.ParameterType), $"content-bearing parameter leaked: {parameter.ParameterType}");
    Assert(methods.Select(method => method.Name).Order().SequenceEqual(
        new[] { "Cancel", "Dispose", "Hide", "ShowError", "ShowModeChanged", "ShowRecording", "ShowTranscribing" }.Order()),
        "controller public event surface drifted");
    return Task.CompletedTask;
}

static Task PlacementDefaultsSafely()
{
    var point = LinuxOverlayPlacementCalculator.Restore(null, new(100, 200, 1920, 1040), 260, 52, 1);
    Assert(point == new LinuxOverlayPixelPoint(930, 1168), $"unexpected default placement: {point}");
    return Task.CompletedTask;
}

static Task PlacementRestoresAcrossScales()
{
    var stored = new LinuxOverlayPlacement(.25, .75);
    var normal = LinuxOverlayPlacementCalculator.Restore(stored, new(0, 0, 1920, 1080), 260, 52, 1);
    var scaled = LinuxOverlayPlacementCalculator.Restore(stored, new(1920, -100, 2560, 1440), 390, 78, 1.5);
    Assert(normal == new LinuxOverlayPixelPoint(415, 771), "normal-scale ratio drifted");
    Assert(scaled == new LinuxOverlayPixelPoint(2462, 922), $"scaled ratio drifted: {scaled}");
    var captured = LinuxOverlayPlacementCalculator.Capture(scaled, new(1920, -100, 2560, 1440), 390, 78);
    Assert(Math.Abs(captured.XRatio - .25) < .001 && Math.Abs(captured.YRatio - .75) < .001,
        "scaled placement did not round-trip");
    return Task.CompletedTask;
}

static Task PlacementIsValidatedAndClamped()
{
    var area = new LinuxOverlayPixelRect(-1000, 50, 800, 600);
    var invalid = LinuxOverlayPlacementCalculator.Restore(new(double.NaN, 20), area, 260, 52, 1);
    Assert(invalid.X == -730 && invalid.Y >= area.Y && invalid.Y + 52 <= area.Bottom,
        "invalid placement did not fall back within the work area");
    var left = LinuxOverlayPlacementCalculator.Capture(new(-9000, -9000), area, 260, 52);
    var right = LinuxOverlayPlacementCalculator.Capture(new(9000, 9000), area, 260, 52);
    Assert(left == new LinuxOverlayPlacement(0, 0) && right == new LinuxOverlayPlacement(1, 1),
        "offscreen position was not clamped");
    return Task.CompletedTask;
}

static Task PlacementWritesAreDebounced()
{
    var store = new FakePlacementStore();
    var scheduler = new FakePlacementScheduler();
    using var persistence = new LinuxOverlayPlacementPersistence(store, TimeSpan.FromMilliseconds(1), scheduler.Schedule);
    persistence.SaveDebounced(new(.1, .2));
    persistence.SaveDebounced(new(.3, .4));
    Assert(scheduler.PendingCount == 1 && store.Saves.Count == 0, "drag writes were not coalesced");
    scheduler.CompleteLatest();
    Assert(store.Saves.SequenceEqual(new[] { new LinuxOverlayPlacement(.3, .4) }), "latest placement was not saved");
    return Task.CompletedTask;
}

static Task PlacementStoreFailuresAreIsolated()
{
    var scheduler = new FakePlacementScheduler();
    using var persistence = new LinuxOverlayPlacementPersistence(new ThrowingPlacementStore(),
        TimeSpan.Zero, scheduler.Schedule);
    Assert(persistence.LoadBestEffort() is null, "failed load escaped or returned data");
    persistence.SaveDebounced(new(.5, .5));
    scheduler.CompleteLatest();
    return Task.CompletedTask;
}

static Task PlacementRoundTripsPrivately()
{
    var root = Path.Combine(Path.GetTempPath(), "hyperwhisper-overlay-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new JsonLinuxOverlayPlacementStore(root);
        var expected = new LinuxOverlayPlacement(.125, .875);
        store.Save(expected);
        Assert(store.Load() == expected, "placement did not survive a config-store round trip");
        if (!OperatingSystem.IsWindows())
        {
            var path = Path.Combine(root, "hyperwhisper", "overlay-placement.json");
            var mode = File.GetUnixFileMode(path);
            Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite)) == 0, "placement config was readable by another user");
        }
    }
    finally { try { Directory.Delete(root, true); } catch { } }
    return Task.CompletedTask;
}

static Task OverlayDoesNotActivate()
{
    Assert(!LinuxOverlayInteractionPolicy.ShowActivated && !LinuxOverlayInteractionPolicy.Focusable,
        "overlay interaction policy could steal the dictated target");
    return Task.CompletedTask;
}

static async Task WaitUntil(Func<bool> condition)
{
    for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(5);
    Assert(condition(), "asynchronous overlay transition did not complete");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class OverlayFixture : IDisposable
{
    public required LinuxRecordingOverlayViewModel ViewModel { get; init; }
    public required LinuxRecordingOverlayController Controller { get; init; }
    public required FakeSurface Surface { get; init; }
    public required FakeDelay Delay { get; init; }

    public static OverlayFixture Create(Func<DateTimeOffset>? clock = null)
    {
        var viewModel = new LinuxRecordingOverlayViewModel();
        var surface = new FakeSurface();
        var delay = new FakeDelay();
        return new()
        {
            ViewModel = viewModel,
            Surface = surface,
            Delay = delay,
            Controller = new(viewModel, new ImmediateDispatcher(), surface, delay,
                clock ?? (() => DateTimeOffset.UtcNow), false, TestText.Get),
        };
    }

    public void Dispose() => Controller.Dispose();
}

static class TestText
{
    public static string Get(string key)
    {
        using var bridge = new AvaloniaLocalizationBridge(CultureInfo.GetCultureInfo("en"));
        return bridge.GetRequired(key);
    }
}

sealed class ImmediateDispatcher : ILinuxOverlayDispatcher
{
    public void Post(Action action) => action();
}

sealed class ThrowingDispatcher : ILinuxOverlayDispatcher
{
    public void Post(Action action) => throw new InvalidOperationException("expected dispatcher failure");
}

sealed class BlockingDispatcher : ILinuxOverlayDispatcher, IDisposable
{
    private readonly ManualResetEventSlim _blocked = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _blockNext;

    public void BlockNextPost() => Interlocked.Exchange(ref _blockNext, 1);
    public bool WaitUntilBlocked() => _blocked.Wait(TimeSpan.FromSeconds(2));
    public void Release() => _release.Set();
    public void Post(Action action)
    {
        action();
        if (Interlocked.Exchange(ref _blockNext, 0) == 0) return;
        _blocked.Set();
        _release.Wait(TimeSpan.FromSeconds(2));
    }
    public void Dispose() { _blocked.Dispose(); _release.Dispose(); }
}

class FakeSurface : ILinuxRecordingOverlaySurface
{
    public int ShowCalls { get; private set; }
    public int HideCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public virtual void ShowBestEffort() => ShowCalls++;
    public virtual void HideBestEffort() => HideCalls++;
    public virtual void Dispose() => DisposeCalls++;
}

sealed class ThrowingSurface : FakeSurface
{
    public override void ShowBestEffort() => throw new InvalidOperationException("expected surface failure");
}

sealed class FakeDelay : ILinuxOverlayDelay
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _pending = [];
    public TaskCompletionSource? Latest
    {
        get { lock (_gate) return _pending.LastOrDefault(); }
    }

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        lock (_gate) _pending.Add(completion);
        return completion.Task;
    }

    public void CompleteLatest()
    {
        TaskCompletionSource? completion;
        lock (_gate) completion = _pending.LastOrDefault(item => !item.Task.IsCompleted);
        completion?.TrySetResult();
    }
}

sealed class FakePlacementStore : ILinuxOverlayPlacementStore
{
    public List<LinuxOverlayPlacement> Saves { get; } = [];
    public LinuxOverlayPlacement? Load() => null;
    public void Save(LinuxOverlayPlacement placement) => Saves.Add(placement);
}

sealed class ThrowingPlacementStore : ILinuxOverlayPlacementStore
{
    public LinuxOverlayPlacement? Load() => throw new IOException("expected");
    public void Save(LinuxOverlayPlacement placement) => throw new IOException("expected");
}

sealed class FakePlacementScheduler
{
    private readonly List<Entry> _entries = [];
    public int PendingCount => _entries.Count(entry => !entry.Cancelled && !entry.Completed);
    public IDisposable Schedule(TimeSpan _, Action callback)
    {
        var entry = new Entry(callback);
        _entries.Add(entry);
        return new Cancellation(() => entry.Cancelled = true);
    }
    public void CompleteLatest()
    {
        var entry = _entries.Last(item => !item.Cancelled && !item.Completed);
        entry.Completed = true;
        entry.Callback();
    }
    private sealed class Entry(Action callback) { public Action Callback { get; } = callback; public bool Cancelled; public bool Completed; }
    private sealed class Cancellation(Action cancel) : IDisposable { public void Dispose() => cancel(); }
}
