using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Input;

internal interface IPushToTalkScheduler
{
    DateTimeOffset Now { get; }
    IDisposable Schedule(TimeSpan delay, Action action);
}
internal sealed class PushToTalkScheduler : IPushToTalkScheduler
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public IDisposable Schedule(TimeSpan delay, Action action)
    { Timer? timer = null; timer = new Timer(_ => { timer?.Dispose(); action(); }, null, delay, Timeout.InfiniteTimeSpan); return timer; }
}

public sealed class LinuxPushToTalkMonitor : IPushToTalkMonitor
{
    private const string ActionName = "push-to-talk";
    private static readonly TimeSpan ActivationDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeyUpDebounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MinimumLockDuration = TimeSpan.FromMilliseconds(2000);
    private readonly object _gate = new();
    private readonly IGlobalShortcutService _shortcuts;
    private readonly IShortcutInterferenceSource? _interference;
    private readonly IPushToTalkScheduler _scheduler;
    private PushToTalkConfiguration _configuration = new(PushToTalkMode.Disabled);
    private MonitorState _state;
    private IDisposable? _activationTimer, _latchTimer, _debounceTimer;
    private DateTimeOffset? _firstTap;
    private DateTimeOffset _lastLock;
    private bool _enteredViaHold, _keyDown, _disposed;
    public LinuxPushToTalkMonitor() : this(new LinuxGlobalShortcutService(), new PushToTalkScheduler()) { }
    internal LinuxPushToTalkMonitor(IGlobalShortcutService shortcuts, IPushToTalkScheduler? scheduler = null)
    {
        _shortcuts = shortcuts; _scheduler = scheduler ?? new PushToTalkScheduler();
        _shortcuts.ShortcutPressed += OnPressed; _shortcuts.ShortcutReleased += OnReleased;
        _interference = _shortcuts as IShortcutInterferenceSource;
        if (_interference is not null) _interference.Interfered += OnInterfered;
    }
    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? Interfered;
    public void Configure(PushToTalkConfiguration configuration)
    { ArgumentNullException.ThrowIfNull(configuration); lock (_gate) { _configuration = configuration; ResetState(); } }
    public PlatformResult Start()
    {
        if (_disposed) return PlatformResult.Failure("push_to_talk.disposed", "The push-to-talk monitor is disposed.");
        _shortcuts.Clear();
        if (_configuration.Mode == PushToTalkMode.Disabled) return PlatformResult.Success();
        var shortcut = _configuration.Mode == PushToTalkMode.CustomShortcut ? _configuration.CustomShortcut : MapModifier(_configuration.Modifier);
        if (shortcut is null || shortcut.IsEmpty) return PlatformResult.Failure("push_to_talk.invalid", "The configured push-to-talk input is invalid.");
        var registered = _shortcuts.RegisterShortcuts([new NamedShortcut(ActionName, shortcut)]);
        if (!registered.TryGetValue(ActionName, out var result) || result.IsFailure)
            return result ?? PlatformResult.Failure("push_to_talk.registration_failed", "Push-to-talk registration failed.");
        return _shortcuts.Start();
    }
    private static GlobalShortcut MapModifier(ModifierSide modifier) => modifier switch
    {
        ModifierSide.Control => new(ShortcutModifiers.Control), ModifierSide.Alt => new(ShortcutModifiers.Alt),
        ModifierSide.Shift => new(ShortcutModifiers.Shift), ModifierSide.Meta => new(ShortcutModifiers.Meta),
        _ => new(ShortcutModifiers.None, new ShortcutKeyCode(modifier.ToString())),
    };
    private void OnPressed(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (args.Name != ActionName) return;
        lock (_gate)
        {
            _keyDown = true;
            switch (_state)
            {
                case MonitorState.Idle:
                    _state = MonitorState.WaitingForActivation; ArmInterference(true); StartActivationTimer(); break;
                case MonitorState.WaitingForActivation: break;
                case MonitorState.PttActive: Cancel(ref _debounceTimer); Cancel(ref _latchTimer); break;
                case MonitorState.LatchActive:
                    if (_scheduler.Now - _lastLock >= MinimumLockDuration)
                    { _state = MonitorState.UnlatchPending; _firstTap = null; }
                    break;
                case MonitorState.UnlatchPending: break;
            }
        }
    }
    private void OnReleased(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (args.Name != ActionName) return;
        var release = false;
        lock (_gate)
        {
            _keyDown = false;
            switch (_state)
            {
                case MonitorState.WaitingForActivation: Cancel(ref _activationTimer); StartDebounce(); break;
                case MonitorState.PttActive:
                    if (_enteredViaHold) StartDebounce();
                    else if (_configuration.DoublePressLock && _firstTap is { } first && _scheduler.Now - first <= DoublePressWindow)
                    { Cancel(ref _latchTimer); _state = MonitorState.LatchActive; _lastLock = _scheduler.Now; ArmInterference(false); }
                    else { Cancel(ref _latchTimer); _state = MonitorState.Idle; ArmInterference(false); release = true; }
                    break;
                case MonitorState.UnlatchPending:
                    if (_firstTap is null) { _firstTap = _scheduler.Now; StartLatchTimer(); }
                    else if (_scheduler.Now - _firstTap <= DoublePressWindow)
                    { Cancel(ref _latchTimer); _state = MonitorState.Idle; release = true; }
                    else { Cancel(ref _latchTimer); _state = MonitorState.LatchActive; }
                    break;
            }
        }
        if (release) Raise(Released);
    }
    private void StartActivationTimer()
    {
        Cancel(ref _activationTimer); _activationTimer = _scheduler.Schedule(ActivationDelay, () =>
        {
            var press = false; lock (_gate)
            { _activationTimer = null; if (_state == MonitorState.WaitingForActivation && _keyDown) { _state = MonitorState.PttActive; _enteredViaHold = true; _firstTap = _scheduler.Now; press = true; } }
            if (press) Raise(Pressed);
        });
    }
    private void StartDebounce()
    {
        Cancel(ref _debounceTimer); _debounceTimer = _scheduler.Schedule(KeyUpDebounce, () =>
        {
            var press = false; var release = false;
            lock (_gate)
            {
                _debounceTimer = null;
                if (_keyDown) { if (_state == MonitorState.WaitingForActivation) StartActivationTimer(); return; }
                if (_state == MonitorState.PttActive && _enteredViaHold)
                { _state = MonitorState.Idle; ArmInterference(false); release = true; }
                else if (_state == MonitorState.WaitingForActivation)
                {
                    if (_configuration.DoublePressLock)
                    { _state = MonitorState.PttActive; _enteredViaHold = false; _firstTap = _scheduler.Now; press = true; StartLatchTimer(); }
                    else { _state = MonitorState.Idle; ArmInterference(false); }
                }
            }
            if (press) Raise(Pressed); if (release) Raise(Released);
        });
    }
    private void StartLatchTimer()
    {
        Cancel(ref _latchTimer); _latchTimer = _scheduler.Schedule(DoublePressWindow, () =>
        {
            var release = false; lock (_gate)
            {
                _latchTimer = null;
                if (_state == MonitorState.PttActive) { _state = MonitorState.Idle; ArmInterference(false); release = true; }
                else if (_state == MonitorState.UnlatchPending) _state = MonitorState.LatchActive;
            }
            if (release) Raise(Released);
        });
    }
    private void OnInterfered(object? sender, EventArgs args)
    {
        var emit = false; lock (_gate)
        { if (_state is MonitorState.WaitingForActivation or MonitorState.PttActive) { ResetState(); emit = true; } }
        if (emit) Raise(Interfered);
    }
    private void ArmInterference(bool armed) => _interference?.SetInterferenceArmed(armed);
    private static void Cancel(ref IDisposable? timer) { timer?.Dispose(); timer = null; }
    private void ResetState()
    {
        Cancel(ref _activationTimer); Cancel(ref _latchTimer); Cancel(ref _debounceTimer);
        _state = MonitorState.Idle; _firstTap = null; _enteredViaHold = false; _keyDown = false;
        ArmInterference(false); _shortcuts.ResetKeyboardState();
    }
    private void Raise(EventHandler? handlers)
    { if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }
    public void Reset() { lock (_gate) ResetState(); }
    public void ResetToIdle() { lock (_gate) ResetState(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Reset(); _shortcuts.ShortcutPressed -= OnPressed; _shortcuts.ShortcutReleased -= OnReleased;
        if (_interference is not null) _interference.Interfered -= OnInterfered;
        _shortcuts.Dispose(); Pressed = null; Released = null; Interfered = null;
    }
    private enum MonitorState { Idle, WaitingForActivation, PttActive, LatchActive, UnlatchPending }
}
