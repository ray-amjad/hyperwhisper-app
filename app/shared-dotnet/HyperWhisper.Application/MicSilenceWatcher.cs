using System.Diagnostics;
using HyperWhisper.Platform.Abstractions.Audio;

namespace HyperWhisper.PortableApplication.Audio;

/// <summary>
/// The live "mic hears nothing" rule (#2411), the same on every head: while a recording captures,
/// the warning shows once <see cref="WarnAfter"/> has passed with NO block heard yet, and it is gone
/// for the rest of that recording the moment one block is heard.
/// </summary>
/// <remarks>
/// <para>A block is heard when its peak (<see cref="AudioBlockPeak"/>) is at least
/// <see cref="SilenceThresholdDbfs"/>. Speech peaks around -40..-6 dBFS, and a live microphone's room
/// noise peaks above -60 dBFS in an ordinary room, while a muted, privacy-blocked, unplugged or idle
/// virtual input reads exact zero or below -70. -60 is also where every head's waveform is flat.</para>
/// <para>The first heard block DISARMS the watcher: noise-gated headsets (Bluetooth, AirPods, Voice
/// Isolation) deliver true digital silence between words, so a "2 s of continuous silence" rule would
/// fire on ordinary pauses. The clock starts at <see cref="Start"/>, which a head calls once capture
/// has really started; it is driven by <see cref="Tick"/> (a timer in production), so a device that
/// delivers no blocks at all still warns.</para>
/// <para>Thread-safe: blocks arrive on a capture thread and ticks on a timer thread.
/// <see cref="WarningChanged"/> is raised outside the state lock but serialized, and only when the
/// published value differs from the last one, so the last call a subscriber sees is always the
/// current state. A subscriber must not block on the UI thread (post, do not invoke): the UI thread
/// calls <see cref="Start"/> and <see cref="Stop"/>, which publish too.</para>
/// </remarks>
public sealed class MicSilenceWatcher : IDisposable
{
    /// <summary>A block whose peak is at least this is "heard".</summary>
    public const double SilenceThresholdDbfs = -60.0;

    /// <summary>How long a recording captures with nothing heard before the warning shows.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    /// <summary>How often the production timer evaluates the clock.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly Func<TimeSpan> _clock;
    private readonly Timer? _timer;
    private TimeSpan _startedAt;
    private TimeSpan _ignoreHeardUntil;
    private bool _armed;
    private bool _warning;
    private bool _published;
    private bool _disposed;

    /// <summary>A watcher on the monotonic clock, ticked by its own timer while armed.</summary>
    public MicSilenceWatcher()
        : this(MonotonicClock(), startTimer: true)
    {
    }

    /// <param name="clock">Monotonic elapsed time; only differences are used.</param>
    /// <param name="startTimer">False in tests, which call <see cref="Tick"/> themselves.</param>
    public MicSilenceWatcher(Func<TimeSpan> clock, bool startTimer)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (startTimer)
            _timer = new Timer(static state => ((MicSilenceWatcher)state!).Tick(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The warning appeared (true) or went away (false).</summary>
    public event Action<bool>? WarningChanged;

    /// <summary>The warning should be on screen now.</summary>
    public bool IsWarning
    {
        get { lock (_gate) return _warning; }
    }

    /// <summary>A recording is capturing and has not heard a block yet.</summary>
    public bool IsArmed
    {
        get { lock (_gate) return _armed; }
    }

    /// <summary>True when a block with this peak counts as sound. NaN never does.</summary>
    public static bool IsHeard(double peakDbfs) => peakDbfs >= SilenceThresholdDbfs;

    /// <summary>Capture has started for a new recording: forget the last one and start the clock.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _startedAt = _clock();
            _ignoreHeardUntil = _startedAt;
            _armed = true;
            _warning = false;
            ChangeTimerLocked(running: true);
        }
        Publish();
    }

    /// <summary>
    /// The app itself is making a sound (the start chime) for up to <paramref name="span"/> from now: a
    /// heard block before then may be that sound, picked up by the microphone or fed straight into a
    /// loopback input, not the room. It must not disarm the watcher, or the chime alone would hide the
    /// warning on every recording with sound effects on. The 2 s clock keeps running. Each call replaces
    /// the last window, so call it again with a short tail once the sound has ended.
    /// </summary>
    public void IgnoreHeardBlocksFor(TimeSpan span)
    {
        if (span < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(span));
        lock (_gate)
        {
            if (_disposed || !_armed) return;
            _ignoreHeardUntil = _clock() + span;
        }
    }

    /// <summary>The recording stopped, was cancelled or failed: hide the warning and stop watching.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _armed = false;
            _warning = false;
            ChangeTimerLocked(running: false);
        }
        Publish();
    }

    /// <summary>One captured block's peak, in dBFS. The first heard block hides the warning for good.</summary>
    public void ObserveBlockPeak(double peakDbfs)
    {
        lock (_gate)
        {
            if (_disposed || !_armed || !IsHeard(peakDbfs)) return;
            if (_clock() < _ignoreHeardUntil) return;
            _armed = false;
            _warning = false;
            ChangeTimerLocked(running: false);
        }
        Publish();
    }

    /// <summary>Evaluates the clock. Called by the timer, or by a test.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || !_armed || _warning) return;
            if (_clock() - _startedAt < WarnAfter) return;
            _warning = true;
        }
        Publish();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _warning = false;
        }
        _timer?.Dispose();
        WarningChanged = null;
    }

    private void ChangeTimerLocked(bool running)
    {
        try
        {
            _timer?.Change(running ? TickInterval : Timeout.InfiniteTimeSpan,
                running ? TickInterval : Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }

    private void Publish()
    {
        lock (_publishGate)
        {
            bool current;
            lock (_gate) current = _warning;
            if (current == _published) return;
            _published = current;
            var handlers = WarningChanged;
            if (handlers is null) return;
            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                // One bad subscriber must not stop the others, or the capture thread.
                try { handler(current); } catch { }
            }
        }
    }

    private static Func<TimeSpan> MonotonicClock()
    {
        var origin = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(origin);
    }
}
