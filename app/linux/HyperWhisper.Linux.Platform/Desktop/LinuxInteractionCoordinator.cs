using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public sealed record LinuxInteractionConfiguration(
    GlobalShortcut ToggleShortcut,
    PushToTalkConfiguration PushToTalk,
    TimeSpan ClipboardRestoreDelay,
    GlobalShortcut? CancelShortcut = null,
    GlobalShortcut? ChangeModeShortcut = null)
{
    public GlobalShortcut? SessionCancelShortcut { get; init; } = new(
        ShortcutModifiers.None, new ShortcutKeyCode("Escape"));

    public static LinuxInteractionConfiguration Default { get; } = new(
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new ShortcutKeyCode("Space")),
        new PushToTalkConfiguration(PushToTalkMode.Disabled),
        TimeSpan.FromMilliseconds(750),
        // XGrabKey would steal a persistent bare Escape from every application.
        // Bare Escape is instead supplied by SessionCancelShortcut only while active.
        null,
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new ShortcutKeyCode("Period")));
}

public sealed record InteractionStopOutcome(PlatformResult Result, bool RestoreClipboard = true)
{
    public static InteractionStopOutcome FromInjection(
        PlatformResult result,
        TextInjectionOutcome? injectionOutcome,
        bool restoreClipboardPreference) => new(
            result,
            restoreClipboardPreference && injectionOutcome != TextInjectionOutcome.SecureFieldSkipped);
}

/// <summary>
/// Small application seam used by the Linux input adapters. Implementations own
/// the actual batch or streaming recording operation; the coordinator owns only
/// input event serialization and the safe text-injection session boundaries.
/// </summary>
public interface IInteractionRecordingSession
{
    bool IsActive { get; }
    bool IsStreaming => false;
    ValueTask<PlatformResult> StartAsync(CancellationToken cancellationToken = default);
    ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default);
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

internal interface IInteractionDurationScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class InteractionDurationScheduler : IInteractionDurationScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var cancellation = new CancellationTokenSource();
        _ = RunAsync(delay, callback, cancellation.Token);
        return cancellation;
    }

    private static async Task RunAsync(TimeSpan delay, Action callback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            callback();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

/// <summary>
/// Connects the Linux global-shortcut and push-to-talk adapters to one recording
/// session. Native input callbacks are serialized, repeated key-down frames are
/// ignored, and exceptions never escape an event handler onto the desktop loop.
/// </summary>
public sealed class LinuxInteractionCoordinator : IDisposable
{
    public const string ToggleActionName = "toggle-transcription";
    public const string CancelActionName = "cancel-transcription";
    public const string ChangeModeActionName = "change-mode";
    public const string SessionCancelActionName = "session-cancel-transcription";

    private readonly IGlobalShortcutService _shortcuts;
    private readonly IPushToTalkMonitor _pushToTalk;
    private readonly ITextInjectionService _textInjection;
    private readonly IInteractionRecordingSession _recording;
    private readonly IUiDispatcher _dispatcher;
    private readonly IInteractionDurationScheduler _durationScheduler;
    private readonly TimeSpan _maximumRecordingDuration;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _durationGate = new();
    private readonly object _sessionCancelGate = new();
    private IDisposable? _durationLimit;
    private long _durationGeneration;
    private LinuxInteractionConfiguration _configuration = LinuxInteractionConfiguration.Default;
    private readonly HashSet<string> _heldActions = new(StringComparer.Ordinal);
    private bool _started;
    private bool _sessionCancelArmed;
    private long _sessionCancelGeneration;
    private bool _disposed;

    public LinuxInteractionCoordinator(
        IGlobalShortcutService shortcuts,
        IPushToTalkMonitor pushToTalk,
        ITextInjectionService textInjection,
        IInteractionRecordingSession recording,
        IUiDispatcher dispatcher)
        : this(shortcuts, pushToTalk, textInjection, recording, dispatcher,
            new InteractionDurationScheduler(), TimeSpan.FromMinutes(20))
    {
    }

    internal LinuxInteractionCoordinator(
        IGlobalShortcutService shortcuts,
        IPushToTalkMonitor pushToTalk,
        ITextInjectionService textInjection,
        IInteractionRecordingSession recording,
        IUiDispatcher dispatcher,
        IInteractionDurationScheduler durationScheduler,
        TimeSpan maximumRecordingDuration)
    {
        _shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        _pushToTalk = pushToTalk ?? throw new ArgumentNullException(nameof(pushToTalk));
        _textInjection = textInjection ?? throw new ArgumentNullException(nameof(textInjection));
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _durationScheduler = durationScheduler ?? throw new ArgumentNullException(nameof(durationScheduler));
        if (maximumRecordingDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordingDuration));
        _maximumRecordingDuration = maximumRecordingDuration;
        _shortcuts.ShortcutPressed += OnShortcutPressed;
        _shortcuts.ShortcutReleased += OnShortcutReleased;
        _pushToTalk.Pressed += OnPushToTalkPressed;
        _pushToTalk.Released += OnPushToTalkReleased;
        _pushToTalk.Interfered += OnPushToTalkInterfered;
    }

    public event EventHandler<PlatformError>? OperationFailed;
    /// <summary>
    /// Content-free request to select the next configured mode. Native key names
    /// and key events are deliberately not exposed beyond the shortcut module.
    /// </summary>
    public event EventHandler? ChangeModeRequested;

    public PlatformResult ConfigureAndStart(LinuxInteractionConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        var validated = Validate(configuration);
        if (validated.IsFailure) return validated;

        var wasStarted = _started;
        var previousSessionCancelArmed = _sessionCancelArmed;
        var previous = _configuration;
        _started = false;
        _heldActions.Clear();
        var armSessionCancel = _recording.IsActive && configuration.SessionCancelShortcut is not null;
        var desiredBindings = Bindings(configuration, armSessionCancel);
        var registered = _shortcuts.RegisterShortcuts(desiredBindings);
        var registrationFailure = FirstRegistrationFailure(desiredBindings, registered);
        if (registrationFailure is not null)
        {
            RestoreConfiguration(previous, wasStarted, previousSessionCancelArmed);
            return registrationFailure;
        }
        if (!wasStarted)
        {
            var shortcutStart = _shortcuts.Start();
            if (shortcutStart.IsFailure)
            {
                RestoreConfiguration(previous, wasStarted, previousSessionCancelArmed);
                return shortcutStart;
            }
        }

        _pushToTalk.Configure(configuration.PushToTalk);
        var pushToTalkStart = _pushToTalk.Start();
        if (pushToTalkStart.IsFailure)
        {
            RestoreConfiguration(previous, wasStarted, previousSessionCancelArmed);
            return pushToTalkStart;
        }
        _configuration = configuration;
        _sessionCancelArmed = armSessionCancel;
        _started = true;
        return PlatformResult.Success();
    }

    private static PlatformResult Validate(LinuxInteractionConfiguration configuration)
    {
        if (configuration.ClipboardRestoreDelay < TimeSpan.Zero)
            return PlatformResult.Failure("interaction.restore_delay_invalid", "Clipboard restore delay cannot be negative.");
        if (configuration.CancelShortcut is { Modifiers: ShortcutModifiers.None })
            return PlatformResult.Failure(
                "interaction.cancel_shortcut_unsafe",
                "A persistent cancel shortcut must include a modifier; bare Escape requires session-scoped registration.");
        if (configuration.SessionCancelShortcut is { } sessionCancel
            && (sessionCancel.IsEmpty || sessionCancel.IsModifierOnly))
            return PlatformResult.Failure(
                "interaction.session_cancel_shortcut_invalid",
                "The session cancel shortcut must include a non-modifier key.");
        var bindings = Bindings(configuration, configuration.SessionCancelShortcut is not null);
        foreach (var binding in bindings)
        {
            if (binding.Shortcut.IsEmpty || binding.Shortcut.IsModifierOnly)
                return PlatformResult.Failure($"interaction.{binding.Name}_invalid", "Every configured interaction shortcut must include a non-modifier key.");
        }
        for (var left = 0; left < bindings.Count; left++)
        for (var right = left + 1; right < bindings.Count; right++)
            if (SameShortcut(bindings[left].Shortcut, bindings[right].Shortcut))
                return PlatformResult.Failure("interaction.shortcut_conflict", "Interaction shortcuts must be different.");
        if (configuration.PushToTalk.Mode == PushToTalkMode.CustomShortcut
            && configuration.PushToTalk.CustomShortcut is { } pushToTalk
            && bindings.Any(binding => SameShortcut(binding.Shortcut, pushToTalk)))
            return PlatformResult.Failure("interaction.shortcut_conflict", "Interaction and push-to-talk shortcuts must be different.");
        return PlatformResult.Success();
    }

    private static IReadOnlyList<NamedShortcut> Bindings(
        LinuxInteractionConfiguration configuration,
        bool includeSessionCancel = false)
    {
        var values = new List<NamedShortcut> { new(ToggleActionName, configuration.ToggleShortcut) };
        if (configuration.CancelShortcut is { } cancel) values.Add(new(CancelActionName, cancel));
        if (configuration.ChangeModeShortcut is { } changeMode) values.Add(new(ChangeModeActionName, changeMode));
        if (includeSessionCancel && configuration.SessionCancelShortcut is { } sessionCancel)
            values.Add(new(SessionCancelActionName, sessionCancel));
        return values;
    }

    private static bool SameShortcut(GlobalShortcut left, GlobalShortcut right) =>
        left.Modifiers == right.Modifiers
        && string.Equals(left.Key.Value, right.Key.Value, StringComparison.OrdinalIgnoreCase);

    private static PlatformResult? FirstRegistrationFailure(
        IReadOnlyList<NamedShortcut> desired,
        IReadOnlyDictionary<string, PlatformResult> registered)
    {
        foreach (var binding in desired)
            if (!registered.TryGetValue(binding.Name, out var result) || result.IsFailure)
                return result ?? PlatformResult.Failure(
                    "interaction.shortcut_registration_failed", "An interaction shortcut could not be registered.");
        return null;
    }

    private void RestoreConfiguration(
        LinuxInteractionConfiguration previous,
        bool wasStarted,
        bool sessionCancelArmed)
    {
        _heldActions.Clear();
        if (!wasStarted)
        {
            _shortcuts.Clear();
            _pushToTalk.Reset();
            return;
        }
        _ = _shortcuts.RegisterShortcuts(Bindings(previous, sessionCancelArmed));
        _pushToTalk.Configure(previous.PushToTalk);
        _ = _pushToTalk.Start();
        _configuration = previous;
        _sessionCancelArmed = sessionCancelArmed;
        _started = true;
    }

    private void OnShortcutPressed(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (!_started || !IsInteractionAction(args.Name) || !_heldActions.Add(args.Name)) return;
        if (args.Name == SessionCancelActionName && !IsSessionCancelArmed())
        {
            _heldActions.Remove(args.Name);
            return;
        }
        switch (args.Name)
        {
            case ToggleActionName:
                Dispatch(_recording.IsActive ? StopCoreAsync : StartCoreAsync);
                break;
            case CancelActionName when _recording.IsActive:
            case SessionCancelActionName when _recording.IsActive:
                Dispatch(CancelCoreAsync);
                break;
            case ChangeModeActionName:
                Dispatch(ChangeModeCoreAsync);
                break;
        }
    }

    private void OnShortcutReleased(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (IsInteractionAction(args.Name)) _heldActions.Remove(args.Name);
    }

    private static bool IsInteractionAction(string name) => name is
        ToggleActionName or CancelActionName or ChangeModeActionName or SessionCancelActionName;

    private void OnPushToTalkPressed(object? sender, EventArgs args)
    {
        if (_started && !_heldActions.Contains(ToggleActionName) && !_recording.IsActive) Dispatch(StartCoreAsync);
        else if (_recording.IsActive) _pushToTalk.ResetToIdle();
    }

    private void OnPushToTalkReleased(object? sender, EventArgs args)
    {
        if (_started && !_heldActions.Contains(ToggleActionName) && _recording.IsActive) Dispatch(StopCoreAsync);
    }

    private void OnPushToTalkInterfered(object? sender, EventArgs args)
    {
        if (_started && !_heldActions.Contains(ToggleActionName) && _recording.IsActive) Dispatch(CancelCoreAsync);
    }

    private void Dispatch(Func<CancellationToken, ValueTask> operation)
    {
        _dispatcher.Post(() => _ = RunGuardedAsync(operation));
    }

    private async Task RunGuardedAsync(Func<CancellationToken, ValueTask> action)
    {
        await _operation.WaitAsync();
        try
        {
            if (!_disposed) await action(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RaiseFailure(new PlatformError("interaction.operation_failed", "The transcription interaction could not be completed."));
            _pushToTalk.ResetToIdle();
        }
        finally
        {
            _operation.Release();
        }
    }

    public Task StartRecordingAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(token => StartCoreAsync(token), cancellationToken);

    public Task StopRecordingAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(token => StopCoreAsync(token), cancellationToken);

    public Task CancelRecordingAsync(CancellationToken cancellationToken = default) =>
        RunGuardedAsync(token => CancelCoreAsync(token), cancellationToken);

    private async Task RunGuardedAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken);
        try
        {
            if (!_disposed) await action(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RaiseFailure(new PlatformError("interaction.operation_failed", "The transcription interaction could not be completed."));
            _pushToTalk.ResetToIdle();
        }
        finally { _operation.Release(); }
    }

    private async ValueTask StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_recording.IsActive) return;
        _textInjection.CaptureTarget();
        _textInjection.StartSession();
        try
        {
            var started = await _recording.StartAsync(cancellationToken);
            if (started.IsSuccess)
            {
                var sessionCancel = ArmSessionCancel();
                if (sessionCancel.IsFailure) RaiseFailure(sessionCancel.Error!);
                ArmDurationLimit();
                return;
            }
            DisarmSessionCancel();
            DisarmDurationLimit();
            RaiseFailure(started.Error!);
        }
        finally
        {
            if (!_recording.IsActive)
            {
                _textInjection.EndSession();
                _ = await _textInjection.RestoreClipboardImmediatelyAsync(CancellationToken.None);
                _pushToTalk.ResetToIdle();
            }
        }
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        DisarmSessionCancel();
        DisarmDurationLimit();
        if (!_recording.IsActive) return;
        InteractionStopOutcome outcome;
        try
        {
            outcome = await _recording.StopAsync(cancellationToken);
        }
        catch
        {
            _ = await _textInjection.RestoreClipboardImmediatelyAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _textInjection.EndSession();
            _pushToTalk.ResetToIdle();
        }
        if (outcome.RestoreClipboard) _textInjection.ScheduleClipboardRestore(_configuration.ClipboardRestoreDelay);
        if (outcome.Result.IsFailure) RaiseFailure(outcome.Result.Error!);
    }

    private async ValueTask CancelCoreAsync(CancellationToken cancellationToken)
    {
        DisarmSessionCancel();
        DisarmDurationLimit();
        try { await _recording.CancelAsync(cancellationToken); }
        finally
        {
            _textInjection.EndSession();
            _ = await _textInjection.RestoreClipboardImmediatelyAsync(CancellationToken.None);
            _pushToTalk.ResetToIdle();
        }
    }

    private ValueTask ChangeModeCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handlers = ChangeModeRequested;
        if (handlers is null) return ValueTask.CompletedTask;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { }
        return ValueTask.CompletedTask;
    }

    private void RaiseFailure(PlatformError error)
    {
        var handlers = OperationFailed;
        if (handlers is null) return;
        foreach (EventHandler<PlatformError> handler in handlers.GetInvocationList())
            try { handler(this, error); } catch { }
    }

    private PlatformResult ArmSessionCancel()
    {
        long generation;
        lock (_sessionCancelGate)
        {
            if (_disposed || _sessionCancelArmed || _configuration.SessionCancelShortcut is null)
                return PlatformResult.Success();
            generation = ++_sessionCancelGeneration;
        }
        var desired = Bindings(_configuration, includeSessionCancel: true);
        var registered = _shortcuts.RegisterShortcuts(desired);
        var failure = FirstRegistrationFailure(desired, registered);
        if (failure is null)
        {
            lock (_sessionCancelGate)
            {
                if (!_disposed && generation == _sessionCancelGeneration)
                {
                    _sessionCancelArmed = true;
                    return PlatformResult.Success();
                }
            }
        }

        _heldActions.Remove(SessionCancelActionName);
        bool disposed;
        lock (_sessionCancelGate) disposed = _disposed;
        if (disposed) _shortcuts.Clear();
        else _ = _shortcuts.RegisterShortcuts(Bindings(_configuration));
        if (failure is null) return PlatformResult.Success();
        return PlatformResult.Failure(
            "interaction.session_cancel_registration_failed",
            "Escape cancellation is unavailable for this recording session.");
    }

    private void DisarmSessionCancel()
    {
        lock (_sessionCancelGate)
        {
            ++_sessionCancelGeneration;
            if (!_sessionCancelArmed) return;
            _sessionCancelArmed = false;
        }
        _heldActions.Remove(SessionCancelActionName);
        var idle = Bindings(_configuration);
        var registered = _shortcuts.RegisterShortcuts(idle);
        if (FirstRegistrationFailure(idle, registered) is null) return;

        // Fail closed: never leave bare Escape grabbed after the session ends.
        _shortcuts.Clear();
        registered = _shortcuts.RegisterShortcuts(idle);
        if (FirstRegistrationFailure(idle, registered) is not null)
            RaiseFailure(new PlatformError(
                "interaction.shortcut_restore_failed",
                "Desktop shortcuts could not be restored after recording."));
    }

    private bool IsSessionCancelArmed()
    {
        lock (_sessionCancelGate) return _sessionCancelArmed;
    }

    private void ArmDurationLimit()
    {
        IDisposable? previous;
        long generation;
        lock (_durationGate)
        {
            previous = _durationLimit;
            _durationLimit = null;
            generation = ++_durationGeneration;
        }
        previous?.Dispose();
        var scheduled = _durationScheduler.Schedule(
            _maximumRecordingDuration, () => OnDurationLimit(generation));
        var retain = false;
        lock (_durationGate)
        {
            if (!_disposed && generation == _durationGeneration)
            {
                _durationLimit = scheduled;
                retain = true;
            }
        }
        if (!retain) scheduled.Dispose();
    }

    private void OnDurationLimit(long generation)
    {
        lock (_durationGate)
        {
            if (_disposed || generation != _durationGeneration) return;
        }
        try { _dispatcher.Post(() => _ = RunDurationLimitAsync(generation)); }
        catch { /* A failed UI dispatch must not stop or leak a background callback. */ }
    }

    private async Task RunDurationLimitAsync(long generation)
    {
        await _operation.WaitAsync();
        try
        {
            lock (_durationGate)
            {
                if (_disposed || generation != _durationGeneration) return;
            }
            if (!_recording.IsActive)
            {
                DisarmDurationLimit();
                return;
            }
            var limitError = _recording.IsStreaming
                ? new PlatformError(
                    "interaction.streaming_duration_limit_reached",
                    "Streaming reached the 20-minute safety limit.")
                : new PlatformError(
                    "interaction.recording_duration_limit_reached",
                    "Recording stopped after reaching the 20-minute safety limit.");
            RaiseFailure(limitError);
            await StopCoreAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RaiseFailure(new PlatformError(
                "interaction.duration_limit_stop_failed",
                "The recording safety limit could not stop transcription cleanly."));
            _pushToTalk.ResetToIdle();
        }
        finally { _operation.Release(); }
    }

    private void DisarmDurationLimit()
    {
        IDisposable? durationLimit;
        lock (_durationGate)
        {
            ++_durationGeneration;
            durationLimit = _durationLimit;
            _durationLimit = null;
        }
        durationLimit?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisarmSessionCancel();
        DisarmDurationLimit();
        _started = false;
        _shortcuts.ShortcutPressed -= OnShortcutPressed;
        _shortcuts.ShortcutReleased -= OnShortcutReleased;
        _pushToTalk.Pressed -= OnPushToTalkPressed;
        _pushToTalk.Released -= OnPushToTalkReleased;
        _pushToTalk.Interfered -= OnPushToTalkInterfered;
        _shortcuts.Clear();
        _pushToTalk.Reset();
        _textInjection.EndSession();
        // An event callback may still be awaiting the recording operation. Do
        // not dispose the semaphore while that callback can legally release it.
        OperationFailed = null;
        ChangeModeRequested = null;
    }
}
