using HyperWhisper.Platform.Abstractions.Audio;
using HyperWhisper.PortableApplication.Audio;

// #2411: the live "mic hears nothing" warning's shared rule, which the Windows and Linux heads
// drive from their capture threads. Manual clock throughout, except one case that proves the
// production timer ticks by itself.
static class MicSilenceWatcherTests
{
    public static void Run()
    {
        PeakIsReadFromRawSamplesInDbfs();
        ThresholdIsMinusSixtyDbfsInclusive();
        WarnsAfterTwoSecondsOfNothingHeard();
        WarnsWhenTheDeviceDeliversNoBlocksAtAll();
        FirstHeardBlockHidesAndDisarmsForTheRestOfTheRecording();
        SoundBeforeTwoSecondsNeverWarns();
        TheAppsOwnStartChimeIsNotTheMicrophone();
        EveryRecordingStartsAgain();
        StopHidesAndStopsWatching();
        EventsAreDeduplicatedAndIsolated();
        ProductionTimerTicksWithoutBlocks();
        Console.WriteLine("Mic silence watcher tests passed.");
    }

    private static void PeakIsReadFromRawSamplesInDbfs()
    {
        Check(double.IsNegativeInfinity(AudioBlockPeak.Pcm16Dbfs(new byte[640])), "a block of zeros is not -infinity");
        Check(double.IsNegativeInfinity(AudioBlockPeak.Pcm16Dbfs([])), "an empty block is not -infinity");
        Check(double.IsNegativeInfinity(AudioBlockPeak.Pcm16Dbfs([0x7F])), "a lone odd byte was read as a sample");
        Check(Near(AudioBlockPeak.Pcm16Dbfs(Pcm16(0, short.MinValue, 0)), 0), "short.MinValue is not 0 dBFS");
        Check(Near(AudioBlockPeak.Pcm16Dbfs(Pcm16(16384, -3)), -6.0206), "half scale is not -6 dBFS");
        // 33/32768 is -59.94 dBFS, 32/32768 is -60.21: the threshold sits between the two codes.
        Check(Near(AudioBlockPeak.Pcm16Dbfs(Pcm16(1, -33, 2)), 20 * Math.Log10(33 / 32768.0)),
            "the peak was not the largest magnitude, sign ignored");
        Check(MicSilenceWatcher.IsHeard(AudioBlockPeak.Pcm16Dbfs(Pcm16(-33))), "-59.9 dBFS of room noise was not heard");
        Check(!MicSilenceWatcher.IsHeard(AudioBlockPeak.Pcm16Dbfs(Pcm16(32, -32, 1))), "-60.2 dBFS was heard");
        Check(Near(AudioBlockPeak.FloatDbfs([0.001f, -0.1f, float.NaN, float.PositiveInfinity]), -20),
            "the float peak did not skip non-finite samples");
        Check(double.IsNegativeInfinity(AudioBlockPeak.FloatDbfs([0f, -0f])), "float zeros are not -infinity");
        Check(double.IsNegativeInfinity(AudioBlockPeak.ToDbfs(0)), "a zero amplitude is not -infinity");
    }

    private static void ThresholdIsMinusSixtyDbfsInclusive()
    {
        Check(MicSilenceWatcher.SilenceThresholdDbfs == -60, "the threshold drifted from -60 dBFS");
        Check(MicSilenceWatcher.WarnAfter == TimeSpan.FromSeconds(2), "the delay drifted from 2.0 s");
        Check(MicSilenceWatcher.IsHeard(-60), "exactly -60 dBFS was not heard");
        Check(!MicSilenceWatcher.IsHeard(-60.01), "-60.01 dBFS was heard");
        Check(!MicSilenceWatcher.IsHeard(double.NegativeInfinity), "digital silence was heard");
        Check(!MicSilenceWatcher.IsHeard(double.NaN), "NaN was heard");
    }

    private static void WarnsAfterTwoSecondsOfNothingHeard()
    {
        var (watcher, clock, events) = Create();
        watcher.Start();
        for (var block = 0; block < 19; block++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            watcher.ObserveBlockPeak(-75);
            watcher.Tick();
        }
        Check(!watcher.IsWarning && events.Count == 0, "the warning showed before 2.0 s");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        watcher.ObserveBlockPeak(double.NegativeInfinity);
        watcher.Tick();
        Check(watcher.IsWarning && watcher.IsArmed, "the warning did not show at 2.0 s of silence");
        Check(events.SequenceEqual([true]), $"expected one 'show', got [{string.Join(",", events)}]");
    }

    private static void WarnsWhenTheDeviceDeliversNoBlocksAtAll()
    {
        var (watcher, clock, events) = Create();
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(1.5));
        watcher.Tick();
        Check(!watcher.IsWarning, "warned at 1.5 s");
        clock.Advance(TimeSpan.FromSeconds(0.5));
        watcher.Tick();
        Check(watcher.IsWarning && events.SequenceEqual([true]), "a device that delivers nothing did not warn");
    }

    // The start chime plays while capture runs; on a loopback/virtual input fed by the output, or a
    // mic that hears only the speakers, it arrives as loud blocks. It must not disarm the watcher.
    private static void TheAppsOwnStartChimeIsNotTheMicrophone()
    {
        var (watcher, clock, events) = Create();
        watcher.IgnoreHeardBlocksFor(TimeSpan.FromSeconds(6)); // before Start: nothing to ignore yet
        watcher.Start();
        Check(watcher.IsArmed, "Start did not arm");
        watcher.IgnoreHeardBlocksFor(TimeSpan.FromSeconds(6));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        watcher.ObserveBlockPeak(-20);
        Check(watcher.IsArmed && events.Count == 0, "the chime disarmed the watcher");
        // The chime ended; its last buffered blocks still arrive within the tail.
        watcher.IgnoreHeardBlocksFor(TimeSpan.FromMilliseconds(500));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        watcher.ObserveBlockPeak(-30);
        clock.Advance(TimeSpan.FromSeconds(1.3));
        watcher.Tick();
        Check(watcher.IsWarning && events.SequenceEqual([true]), "a silent mic behind the chime did not warn at 2.0 s");
        // After the tail, real sound hides the warning as before.
        watcher.ObserveBlockPeak(-40);
        Check(!watcher.IsWarning && !watcher.IsArmed && events.SequenceEqual([true, false]), "sound after the chime did not hide the warning");
        // A new recording forgets the last window.
        watcher.Start();
        watcher.ObserveBlockPeak(-40);
        Check(!watcher.IsArmed, "a window from the last recording ignored this one's sound");
        var threw = false;
        try { watcher.IgnoreHeardBlocksFor(TimeSpan.FromSeconds(-1)); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "a negative window was accepted");
    }

    private static void FirstHeardBlockHidesAndDisarmsForTheRestOfTheRecording()
    {
        var (watcher, clock, events) = Create();
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(3));
        watcher.Tick();
        Check(watcher.IsWarning, "did not warn");
        watcher.ObserveBlockPeak(-42);
        Check(!watcher.IsWarning && !watcher.IsArmed, "a heard block did not hide the warning and disarm");
        // A noise-gated headset: exact digital silence between words, for a long time.
        for (var second = 0; second < 30; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            watcher.ObserveBlockPeak(double.NegativeInfinity);
            watcher.Tick();
        }
        Check(!watcher.IsWarning, "a pause after speech brought the warning back");
        Check(events.SequenceEqual([true, false]), $"expected show then hide, got [{string.Join(",", events)}]");
    }

    private static void SoundBeforeTwoSecondsNeverWarns()
    {
        var (watcher, clock, events) = Create();
        watcher.Start();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        watcher.ObserveBlockPeak(-55);
        clock.Advance(TimeSpan.FromSeconds(10));
        watcher.Tick();
        Check(!watcher.IsWarning && events.Count == 0, "warned after the mic was heard");
    }

    private static void EveryRecordingStartsAgain()
    {
        var (watcher, clock, events) = Create();
        watcher.ObserveBlockPeak(double.NegativeInfinity);
        clock.Advance(TimeSpan.FromSeconds(5));
        watcher.Tick();
        Check(!watcher.IsWarning && !watcher.IsArmed, "warned before any recording started");

        watcher.Start();
        watcher.ObserveBlockPeak(-20);
        watcher.Stop();

        // The second recording: disarmed by the first one's speech, re-armed by Start.
        watcher.Start();
        Check(watcher.IsArmed, "a new recording was not re-armed");
        clock.Advance(TimeSpan.FromSeconds(2));
        watcher.Tick();
        Check(watcher.IsWarning, "the second, silent recording did not warn");

        // A third recording starting while the warning is up hides it and restarts the clock.
        clock.Advance(TimeSpan.FromSeconds(1));
        watcher.Start();
        Check(!watcher.IsWarning, "a new recording kept the last one's warning");
        clock.Advance(TimeSpan.FromSeconds(1.9));
        watcher.Tick();
        Check(!watcher.IsWarning, "the clock was not reset by the new recording");
        clock.Advance(TimeSpan.FromSeconds(0.1));
        watcher.Tick();
        Check(watcher.IsWarning, "the third recording did not warn");
        Check(events.SequenceEqual([true, false, true]), $"got [{string.Join(",", events)}]");
    }

    private static void StopHidesAndStopsWatching()
    {
        var (watcher, clock, events) = Create();
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(2));
        watcher.Tick();
        watcher.Stop();
        Check(!watcher.IsWarning && !watcher.IsArmed, "stop left the warning up");
        clock.Advance(TimeSpan.FromSeconds(5));
        watcher.Tick();
        watcher.ObserveBlockPeak(-10);
        Check(!watcher.IsWarning, "a tick after stop warned");
        watcher.Stop();
        Check(events.SequenceEqual([true, false]), $"got [{string.Join(",", events)}]");

        watcher.Dispose();
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(5));
        watcher.Tick();
        Check(!watcher.IsWarning && events.Count == 2, "a disposed watcher still warned");
        watcher.Dispose();
    }

    private static void EventsAreDeduplicatedAndIsolated()
    {
        var clock = new ManualClock();
        using var watcher = new MicSilenceWatcher(clock.Now, startTimer: false);
        var seen = new List<bool>();
        watcher.WarningChanged += _ => throw new InvalidOperationException("a broken subscriber");
        watcher.WarningChanged += seen.Add;
        watcher.Start();
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(4));
        watcher.Tick();
        watcher.Tick();
        watcher.ObserveBlockPeak(-30);
        watcher.ObserveBlockPeak(-30);
        Check(seen.SequenceEqual([true, false]), $"events were duplicated or lost: [{string.Join(",", seen)}]");
    }

    private static void ProductionTimerTicksWithoutBlocks()
    {
        var clock = new ManualClock();
        using var watcher = new MicSilenceWatcher(clock.Now, startTimer: true);
        using var shown = new ManualResetEventSlim();
        watcher.WarningChanged += warning => { if (warning) shown.Set(); };
        watcher.Start();
        clock.Advance(TimeSpan.FromSeconds(2));
        Check(shown.Wait(TimeSpan.FromSeconds(10)), "the watcher's own timer never evaluated the clock");
        Check(watcher.IsWarning, "the timer's tick did not set the warning");
        watcher.Stop();
        Check(!watcher.IsWarning, "stop did not hide the timer's warning");

        using var real = new MicSilenceWatcher();
        real.Start();
        real.ObserveBlockPeak(-1);
        Check(!real.IsArmed && !real.IsWarning, "the default watcher did not disarm on sound");
    }

    private static (MicSilenceWatcher Watcher, ManualClock Clock, List<bool> Events) Create()
    {
        var clock = new ManualClock();
        var watcher = new MicSilenceWatcher(clock.Now, startTimer: false);
        var events = new List<bool>();
        watcher.WarningChanged += events.Add;
        return (watcher, clock, events);
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            bytes[index * 2] = (byte)(samples[index] & 0xFF);
            bytes[index * 2 + 1] = (byte)((samples[index] >> 8) & 0xFF);
        }
        return bytes;
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.001;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("MicSilenceWatcher: " + message);
    }

    private sealed class ManualClock
    {
        private long _ticks;
        public TimeSpan Now() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
