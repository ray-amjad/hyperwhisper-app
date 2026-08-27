using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.Linux.Platform.Input;

internal interface IPushToTalkScheduler
{
    /// <summary>
    /// A MONOTONIC millisecond reading. Never a wall clock: the state machine
    /// measures the post-lock bounce window and the double-press window from it,
    /// and an NTP step used to be able to make either interval negative.
    /// </summary>
    ulong NowMs { get; }
    IDisposable Schedule(TimeSpan delay, Action action);
}
internal sealed class PushToTalkScheduler : IPushToTalkScheduler
{
    public ulong NowMs => (ulong)Environment.TickCount64;
    public IDisposable Schedule(TimeSpan delay, Action action)
    { Timer? timer = null; timer = new Timer(_ => { timer?.Dispose(); action(); }, null, delay, Timeout.InfiniteTimeSpan); return timer; }
}

/// <summary>
/// The Linux push-to-talk head. Owns the evdev/portal event source, the timer
/// primitive and the clock; the five-state machine itself lives in the shared
/// Rust core (<see cref="PortablePushToTalkCore"/>, issue #287) and is shared
/// with the Windows and macOS heads.
/// </summary>
public sealed class LinuxPushToTalkMonitor : IPushToTalkMonitor
{
    private const string ActionName = "push-to-talk";

    // Linux keeps the values it shipped. The activation delay (250 ms) and the
    // double-press window (1500 ms) already matched the other platforms and come
    // from the core.
    private const ulong MinimumLockMs = 2000;
    private const ulong KeyUpDebounceMs = 100;

    private readonly object _gate = new();
    private readonly IGlobalShortcutService _shortcuts;
    private readonly IShortcutInterferenceSource? _interference;
    private readonly IPushToTalkScheduler _scheduler;
    private PushToTalkConfiguration _configuration = new(PushToTalkMode.Disabled);
    private PortablePttMachineState _machine = PortablePushToTalkCore.InitialState();
    private PortablePttConfig _pttConfig = PortablePushToTalkCore.Config(MinimumLockMs, KeyUpDebounceMs, false);
    /// <summary>One slot per <see cref="PortablePttTimer"/>, indexed by the enum.</summary>
    private readonly IDisposable?[] _timers = new IDisposable?[3];
    private bool _disposed;
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
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            _configuration = configuration;
            _pttConfig = PortablePushToTalkCore.Config(MinimumLockMs, KeyUpDebounceMs, configuration.DoublePressLock);
        }
        Dispatch(PortablePttEvent.Reset);
    }
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
        Dispatch(PortablePttEvent.KeyDown);
    }
    private void OnReleased(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (args.Name != ActionName) return;
        Dispatch(PortablePttEvent.KeyUp);
    }
    private void OnInterfered(object? sender, EventArgs args) => Dispatch(PortablePttEvent.Interference);

    /// <summary>
    /// Step the shared machine and apply what it asks for. Every mutation happens
    /// under <see cref="_gate"/>; the resulting event is raised outside it, so a
    /// subscriber can call back in without deadlocking.
    /// </summary>
    /// <param name="fired">
    /// The timer whose callback is driving this event, if any. Its slot is
    /// cleared before the step runs so a cancel in the same step cannot dispose a
    /// handle the step itself re-armed.
    /// </param>
    private void Dispatch(PortablePttEvent @event, PortablePttTimer? fired = null)
    {
        PortablePttSignal? signal;
        lock (_gate)
        {
            if (fired is { } timer) _timers[(int)timer] = null;

            var result = PortablePushToTalkCore.Step(_machine, @event, _scheduler.NowMs, _pttConfig);
            _machine = result.State;

            foreach (var command in result.Timers)
            {
                var slot = (int)command.Timer;
                _timers[slot]?.Dispose();
                _timers[slot] = command.Start
                    ? _scheduler.Schedule(TimeSpan.FromMilliseconds(command.DelayMs), () => Dispatch(TimeoutFor(command.Timer), command.Timer))
                    : null;
            }

            if (result.ArmInterference is { } armed) _interference?.SetInterferenceArmed(armed);
            if (result.ResetKeyboardState) _shortcuts.ResetKeyboardState();
            signal = result.Signal;
        }

        switch (signal)
        {
            case PortablePttSignal.StartRecording: Raise(Pressed); break;
            case PortablePttSignal.StopRecording: Raise(Released); break;
            case PortablePttSignal.Interfered: Raise(Interfered); break;
        }
    }

    private static PortablePttEvent TimeoutFor(PortablePttTimer timer) => timer switch
    {
        PortablePttTimer.Activation => PortablePttEvent.ActivationTimeout,
        PortablePttTimer.Latch => PortablePttEvent.LatchTimeout,
        _ => PortablePttEvent.KeyUpDebounceTimeout,
    };

    private void Raise(EventHandler? handlers)
    { if (handlers is null) return; foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch { } }
    public void Reset() => Dispatch(PortablePttEvent.Reset);
    public void ResetToIdle() => Dispatch(PortablePttEvent.ResetToIdle);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Reset(); _shortcuts.ShortcutPressed -= OnPressed; _shortcuts.ShortcutReleased -= OnReleased;
        if (_interference is not null) _interference.Interfered -= OnInterfered;
        _shortcuts.Dispose(); Pressed = null; Released = null; Interfered = null;
    }
}
