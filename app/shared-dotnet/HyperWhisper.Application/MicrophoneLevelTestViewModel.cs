using System.Windows.Input;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// The Settings → Sound "Test" button and the level bar under the microphone picker.
/// </summary>
/// <remarks>
/// Nothing opens the microphone until <see cref="Start"/>: visiting the page does not. A test runs
/// for <see cref="TestDuration"/> on the device <c>deviceId()</c> names at the moment it starts (the
/// picker's chosen device, or the OS default it falls back to), and ends on the timer, a second
/// click, <see cref="Stop"/> (the page is left, a recording starts), or the capture ending by
/// itself (the device was unplugged). Idle, <see cref="Level"/> is 0 and the bar draws empty.
///
/// Capture events arrive on the capture's own thread; every state change is posted to the
/// synchronization context the view model was built on, and a callback from an older test is
/// recognised by its session number and dropped.
/// </remarks>
public sealed class MicrophoneLevelTestViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(10);

    /// <summary>The quietest level the bar shows, in dBFS of the mean absolute sample. Speech
    /// at a normal distance sits around -35 to -15, so -60..0 puts it in the middle of the bar.</summary>
    private const double FloorDecibels = -60;

    private readonly object _gate = new();
    private readonly Func<IStreamingAudioCapture>? _captureFactory;
    private readonly Func<string?> _deviceId;
    private readonly Func<bool>? _isRecordingActive;
    private readonly TimeProvider _time;
    private readonly Func<string, string>? _localize;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private IStreamingAudioCapture? _capture;
    private ITimer? _timer;
    private int _session;
    private bool _isTesting;
    private double _level;
    private string? _errorMessage;
    private bool _disposed;

    public MicrophoneLevelTestViewModel(
        Func<IStreamingAudioCapture>? captureFactory,
        Func<string?> deviceId,
        TimeProvider? time = null,
        Func<string, string>? localize = null,
        Func<bool>? isRecordingActive = null)
    {
        _captureFactory = captureFactory;
        _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        _time = time ?? TimeProvider.System;
        _localize = localize;
        _isRecordingActive = isRecordingActive;
        ToggleCommand = new AsyncCommand(_ => { Toggle(); return Task.CompletedTask; }, _ => IsAvailable);
    }

    /// <summary>False when the platform gave no capture source; the view hides the button.</summary>
    public bool IsAvailable => _captureFactory is not null;
    public bool IsTesting { get => _isTesting; private set { if (Set(ref _isTesting, value)) Notify(nameof(ButtonText)); } }

    /// <summary>0..1 for the bar: the last chunk's level on a -60..0 dBFS scale. 0 while idle.</summary>
    public double Level { get => _level; private set => Set(ref _level, value); }

    /// <summary>"Test" idle, "Stop" while a test runs.</summary>
    public string ButtonText => IsTesting
        ? Text("linux.settings.sound.microphone.stop", "Stop")
        : Text("linux.settings.sound.microphone.test", "Test");

    /// <summary>Why the last test could not open the microphone, or null.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { if (Set(ref _errorMessage, value)) Notify(nameof(HasError)); }
    }
    public bool HasError => _errorMessage is not null;
    public ICommand ToggleCommand { get; }

    public void Toggle()
    {
        if (IsTesting) Stop(); else Start();
    }

    /// <summary>Opens the device <c>deviceId()</c> names and starts the bar. A test already running
    /// is restarted on the current device.</summary>
    public void Start()
    {
        if (_disposed || _captureFactory is null) return;
        Stop();
        if (_isRecordingActive?.Invoke() == true) return;
        ErrorMessage = null;
        IStreamingAudioCapture capture;
        try { capture = _captureFactory(); }
        catch (Exception) { ErrorMessage = OpenFailedText(); return; }
        int session;
        lock (_gate) { session = ++_session; _capture = capture; }
        capture.AudioLevelChanged += OnAudioLevelChanged;
        capture.CaptureStopped += OnCaptureStopped;
        var deviceId = _deviceId();
        PlatformResult started;
        try { started = capture.Start(new AudioRecordingOptions(string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId)); }
        catch (Exception) { started = PlatformResult.Failure("microphone_test.start_failed", OpenFailedText()); }
        if (started.IsFailure)
        {
            Release(session);
            ErrorMessage = OpenFailedText();
            return;
        }
        var timer = _time.CreateTimer(_ => Post(() => { if (IsCurrent(session)) Stop(); }), null, TestDuration, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            if (_session == session) _timer = timer;
            else { timer.Dispose(); return; }
        }
        IsTesting = true;
    }

    /// <summary>Ends the test, if one runs, and empties the bar. Safe from any thread and when idle.</summary>
    public void Stop()
    {
        int session;
        lock (_gate) session = _session;
        Release(session);
        if (_context is null || SynchronizationContext.Current == _context) ResetIdle();
        else _context.Post(_ => ResetIdle(), null);
    }

    private void ResetIdle()
    {
        IsTesting = false;
        Level = 0;
    }

    private void Release(int session)
    {
        IStreamingAudioCapture? capture;
        ITimer? timer;
        lock (_gate)
        {
            if (_session != session) return;
            _session++;
            capture = _capture; _capture = null;
            timer = _timer; _timer = null;
        }
        timer?.Dispose();
        if (capture is null) return;
        capture.AudioLevelChanged -= OnAudioLevelChanged;
        capture.CaptureStopped -= OnCaptureStopped;
        try { capture.Stop(); } catch (Exception) { }
        try { capture.Dispose(); } catch (Exception) { }
    }

    private bool IsCurrent(int session)
    {
        lock (_gate) return _session == session && _capture is not null;
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        int session;
        lock (_gate) { if (!ReferenceEquals(sender, _capture)) return; session = _session; }
        var display = ToDisplayLevel(level);
        Post(() => { if (IsCurrent(session)) Level = display; });
    }

    private void OnCaptureStopped(object? sender, PlatformError? error)
    {
        int session;
        lock (_gate) { if (!ReferenceEquals(sender, _capture)) return; session = _session; }
        Post(() =>
        {
            if (!IsCurrent(session)) return;
            Stop();
            if (error is not null) ErrorMessage = OpenFailedText();
        });
    }

    /// <summary>Maps a mean-absolute level (0..1) onto the bar's -60..0 dBFS scale.</summary>
    public static double ToDisplayLevel(float level)
    {
        if (!(level > 0)) return 0;
        var decibels = 20 * Math.Log10(Math.Min(1, level));
        return Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
    }

    private void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context) action();
        else _context.Post(_ => { if (!_disposed) action(); }, null);
    }

    private string OpenFailedText() => Text("linux.settings.sound.microphone.testFailed", "The microphone could not be opened.");

    private string Text(string key, string fallback)
    {
        var value = _localize?.Invoke(key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        int session;
        lock (_gate) session = _session;
        Release(session);
        _disposed = true;
    }
}
