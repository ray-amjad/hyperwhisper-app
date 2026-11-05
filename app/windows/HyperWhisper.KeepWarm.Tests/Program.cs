using HyperWhisper.Services;
using NAudio.Wave;

// Console harness for MicrophoneKeepWarmService (issue #2501). Prints PASS/FAIL
// per check and "x/y tests passed"; exits 1 on any failure.
//
// HW_KEEPWARM_RACE_ROUNDS overrides how many rounds the race check runs.

var failures = 0;
var checks = 0;

Check("keep-warm: turned off during a recording stays off when it ends", () =>
{
    var captures = new List<FakeCapture>();
    using var keepWarm = new MicrophoneKeepWarmService(device =>
    {
        var capture = new FakeCapture();
        lock (captures) captures.Add(capture);
        return capture;
    });

    keepWarm.Configure(true, 1);
    Assert(captures.Count == 1 && captures[0].Running, "keep-warm on did not open the microphone");
    keepWarm.SuspendForRecording();
    Assert(!captures[0].Running, "a recording did not suspend keep-warm");
    keepWarm.Configure(false, 1);
    keepWarm.ResumeAfterRecording(1);
    Assert(captures.Count == 1, "the recording's end reopened a microphone keep-warm was turned off for");
});

Check("keep-warm: a turn-off racing the recording's end leaves the microphone closed (#2501)", () =>
{
    var rounds = int.TryParse(Environment.GetEnvironmentVariable("HW_KEEPWARM_RACE_ROUNDS"), out var r) ? r : 20000;
    var reopened = 0;
    var random = new Random(2501);

    for (var round = 0; round < rounds; round++)
    {
        var open = 0;
        using var keepWarm = new MicrophoneKeepWarmService(_ => new FakeCapture(
            onStart: () => Interlocked.Increment(ref open),
            onStop: () => Interlocked.Decrement(ref open)));

        keepWarm.Configure(true, 1);
        keepWarm.SuspendForRecording();

        // The recording ends on one thread while the settings thread turns
        // keep-warm off. A random spin on the settings side sweeps its
        // Configure across every point of ResumeAfterRecording.
        var spin = random.Next(0, 400);
        using var go = new ManualResetEventSlim(false);
        var recorder = new Thread(() => { go.Wait(); keepWarm.ResumeAfterRecording(1); });
        var settings = new Thread(() => { go.Wait(); Thread.SpinWait(spin); keepWarm.Configure(false, 1); });
        recorder.Start();
        settings.Start();
        go.Set();
        recorder.Join();
        settings.Join();

        if (Volatile.Read(ref open) != 0)
            reopened++;
    }

    Assert(reopened == 0, $"the microphone was open after keep-warm was turned off in {reopened}/{rounds} rounds");
});

Console.WriteLine($"{checks - failures}/{checks} tests passed");
return failures == 0 ? 0 : 1;

void Check(string name, Action body)
{
    checks++;
    try
    {
        body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class FakeCapture(Action? onStart = null, Action? onStop = null) : IWaveIn
{
    public bool Running { get; private set; }
    public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);

#pragma warning disable CS0067 // the service subscribes; the fake never raises
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
#pragma warning restore CS0067

    public void StartRecording()
    {
        Running = true;
        onStart?.Invoke();
    }

    public void StopRecording()
    {
        if (!Running)
            return;
        Running = false;
        onStop?.Invoke();
    }

    public void Dispose() { }
}
