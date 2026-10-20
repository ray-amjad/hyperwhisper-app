using System.IO.Pipes;
using System.Text;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Platform.Abstractions;

// The microphone picker's device watch (spec: mic picker in Settings, 2026-10-19). The watcher is
// `pactl subscribe`; these drive it with scripted subscribe output, so they need no sound server.
static class DeviceWatcherTests
{
    public static Task OnlySourceAndServerEventsCount()
    {
        string[] counted =
        [
            "Event 'new' on source #52",
            "Event 'remove' on source #52",
            "Event 'change' on server #-1",
        ];
        string[] ignored =
        [
            "Event 'new' on source-output #91",   // a capture stream opened (our own recording too)
            "Event 'remove' on source-output #91",
            "Event 'change' on source #52",       // a volume move: the mic boost makes these
            "Event 'new' on sink #3",
            "Event 'change' on sink-input #12",
            "Event 'new' on card #4",              // the card's source arrives as its own event
            "",
        ];
        foreach (var line in counted) Assert.True(PulseAudioDeviceWatcher.IsDeviceEvent(line), line);
        foreach (var line in ignored) Assert.True(!PulseAudioDeviceWatcher.IsDeviceEvent(line), line);
        return Task.CompletedTask;
    }

    public static async Task BurstIsOneDebouncedEvent()
    {
        var launcher = new ScriptedSubscribeLauncher();
        using var watcher = new PulseAudioDeviceWatcher(launcher, "/usr/bin/pactl", TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(30));
        var raised = 0;
        watcher.DevicesMayHaveChanged += (_, _) => Interlocked.Increment(ref raised);
        watcher.Start();
        watcher.Start();
        var child = await launcher.NextChild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = launcher.Requests.Single();
        Assert.True(request.Arguments.SequenceEqual(["subscribe"]), "the watcher did not run `pactl subscribe`");
        Assert.True(request.Environment.TryGetValue("LC_ALL", out var locale) && locale == "C",
            "pactl subscribe was not run under LC_ALL=C, so its event wording could be translated");

        await child.WriteLinesAsync("Event 'new' on source-output #91", "Event 'change' on source #52");
        await Task.Delay(400);
        Assert.Equal(0, Volatile.Read(ref raised));

        // A USB microphone arriving: card, source and the server's default change, in one burst.
        await child.WriteLinesAsync("Event 'new' on card #4", "Event 'new' on source #52", "Event 'change' on server #-1");
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "a plugged-in source raised nothing");
        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref raised));

        await child.WriteLinesAsync("Event 'remove' on source #52");
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 2, "an unplugged source raised nothing");
        Assert.True(!child.Terminated, "the subscribe child was ended while still watching");
        watcher.Dispose();
        Assert.True(child.Terminated, "disposing the watcher left pactl subscribe running");
    }

    public static async Task ServerRestartRaisesAndResubscribes()
    {
        var launcher = new ScriptedSubscribeLauncher();
        using var watcher = new PulseAudioDeviceWatcher(launcher, "/usr/bin/pactl", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        var raised = 0;
        watcher.DevicesMayHaveChanged += (_, _) => Interlocked.Increment(ref raised);
        watcher.Start();
        var first = await launcher.NextChild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        launcher.NextChild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.EndOutput();   // the sound server went away
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 1, "a lost subscription did not trigger a re-read");
        _ = await launcher.NextChild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, launcher.Requests.Count);
    }

    public static async Task NoSubscribeDegradesToPolling()
    {
        var launcher = new ScriptedSubscribeLauncher { FailStarts = true };
        using var watcher = new PulseAudioDeviceWatcher(launcher, "/usr/bin/pactl", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));
        var raised = 0;
        watcher.DevicesMayHaveChanged += (_, _) => Interlocked.Increment(ref raised);
        watcher.Start();
        await WaitUntilAsync(() => Volatile.Read(ref raised) >= 3, "a watcher that cannot subscribe did not fall back to polling");

        using var absent = new PulseAudioDeviceWatcher(launcher, null, TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(!absent.IsAvailable, "a watcher without pactl claims to be available");
        var before = launcher.Requests.Count;
        absent.Start();
        await Task.Delay(100);
        Assert.True(launcher.Requests.Count - before <= 2, "a watcher without pactl tried to start a child");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    private sealed class ScriptedSubscribeLauncher : IChildProcessLauncher
    {
        private readonly object _gate = new();
        private readonly List<ChildProcessStartRequest> _requests = [];
        public bool FailStarts { get; init; }
        public TaskCompletionSource<ScriptedChild> NextChild { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ChildProcessStartRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
        public PlatformResult<IChildProcess> Start(ChildProcessStartRequest request)
        {
            lock (_gate) _requests.Add(request);
            if (FailStarts) return PlatformResult<IChildProcess>.Failure("pulse_unavailable", "Connection refused");
            var child = new ScriptedChild();
            NextChild.TrySetResult(child);
            return PlatformResult<IChildProcess>.Success(child);
        }
    }

    private sealed class ScriptedChild : IChildProcess
    {
        private readonly AnonymousPipeServerStream _writer = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _reader;
        public ScriptedChild() => _reader = new(PipeDirection.In, _writer.ClientSafePipeHandle);
        public int Id => 4242;
        public bool HasExited => Terminated;
        public bool Terminated { get; private set; }
        public int? ExitCode => Terminated ? 0 : null;
        public Stream? StandardInput => null;
        public Stream? StandardOutput => _reader;
        public Stream? StandardError => null;
        public async Task WriteLinesAsync(params string[] lines)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
            await _writer.WriteAsync(bytes);
            await _writer.FlushAsync();
        }
        public void EndOutput() { try { _writer.Dispose(); } catch (Exception) { } }
        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
        public ValueTask TerminateAsync(CancellationToken cancellationToken = default)
        {
            Terminated = true;
            EndOutput();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            EndOutput();
            try { _reader.Dispose(); } catch (Exception) { }
            return ValueTask.CompletedTask;
        }
    }
}
