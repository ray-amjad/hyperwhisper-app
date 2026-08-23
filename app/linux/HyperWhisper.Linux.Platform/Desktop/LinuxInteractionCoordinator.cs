using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Desktop;

public sealed record LinuxInteractionConfiguration(
    GlobalShortcut ToggleShortcut,
    PushToTalkConfiguration PushToTalk,
    TimeSpan ClipboardRestoreDelay)
{
    public static LinuxInteractionConfiguration Default { get; } = new(
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Shift, new ShortcutKeyCode("Space")),
        new PushToTalkConfiguration(PushToTalkMode.Disabled),
        TimeSpan.FromMilliseconds(750));
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
    ValueTask<PlatformResult> StartAsync(CancellationToken cancellationToken = default);
    ValueTask<InteractionStopOutcome> StopAsync(CancellationToken cancellationToken = default);
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects the Linux global-shortcut and push-to-talk adapters to one recording
/// session. Native input callbacks are serialized, repeated key-down frames are
/// ignored, and exceptions never escape an event handler onto the desktop loop.
/// </summary>
public sealed class LinuxInteractionCoordinator : IDisposable
{
    public const string ToggleActionName = "toggle-transcription";

    private readonly IGlobalShortcutService _shortcuts;
    private readonly IPushToTalkMonitor _pushToTalk;
    private readonly ITextInjectionService _textInjection;
    private readonly IInteractionRecordingSession _recording;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private LinuxInteractionConfiguration _configuration = LinuxInteractionConfiguration.Default;
    private bool _toggleHeld;
    private bool _started;
    private bool _disposed;

    public LinuxInteractionCoordinator(
        IGlobalShortcutService shortcuts,
        IPushToTalkMonitor pushToTalk,
        ITextInjectionService textInjection,
        IInteractionRecordingSession recording,
        IUiDispatcher dispatcher)
    {
        _shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        _pushToTalk = pushToTalk ?? throw new ArgumentNullException(nameof(pushToTalk));
        _textInjection = textInjection ?? throw new ArgumentNullException(nameof(textInjection));
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shortcuts.ShortcutPressed += OnShortcutPressed;
        _shortcuts.ShortcutReleased += OnShortcutReleased;
        _pushToTalk.Pressed += OnPushToTalkPressed;
        _pushToTalk.Released += OnPushToTalkReleased;
        _pushToTalk.Interfered += OnPushToTalkInterfered;
    }

    public event EventHandler<PlatformError>? OperationFailed;

    public PlatformResult ConfigureAndStart(LinuxInteractionConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ClipboardRestoreDelay < TimeSpan.Zero)
            return PlatformResult.Failure("interaction.restore_delay_invalid", "Clipboard restore delay cannot be negative.");
        if (configuration.ToggleShortcut.IsEmpty || configuration.ToggleShortcut.IsModifierOnly)
            return PlatformResult.Failure("interaction.toggle_invalid", "The toggle shortcut must include a non-modifier key.");
        if (Conflicts(configuration.ToggleShortcut, configuration.PushToTalk))
            return PlatformResult.Failure("interaction.shortcut_conflict", "Toggle and push-to-talk shortcuts must be different.");

        var wasStarted = _started;
        _started = false;
        _configuration = configuration;
        _toggleHeld = false;
        _shortcuts.Clear();
        var registered = _shortcuts.RegisterShortcuts(
            [new NamedShortcut(ToggleActionName, configuration.ToggleShortcut)]);
        if (!registered.TryGetValue(ToggleActionName, out var registration) || registration.IsFailure)
            return registration ?? PlatformResult.Failure("interaction.toggle_registration_failed", "The transcription shortcut could not be registered.");
        if (!wasStarted)
        {
            var shortcutStart = _shortcuts.Start();
            if (shortcutStart.IsFailure) return shortcutStart;
        }

        _pushToTalk.Configure(configuration.PushToTalk);
        var pushToTalkStart = _pushToTalk.Start();
        if (pushToTalkStart.IsFailure)
        {
            _shortcuts.Clear();
            return pushToTalkStart;
        }
        _started = true;
        return PlatformResult.Success();
    }

    private static bool Conflicts(GlobalShortcut toggle, PushToTalkConfiguration pushToTalk)
    {
        if (pushToTalk.Mode != PushToTalkMode.CustomShortcut || pushToTalk.CustomShortcut is null) return false;
        return toggle.Modifiers == pushToTalk.CustomShortcut.Modifiers
            && string.Equals(toggle.Key.Value, pushToTalk.CustomShortcut.Key.Value, StringComparison.OrdinalIgnoreCase);
    }

    private void OnShortcutPressed(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (!_started || args.Name != ToggleActionName || _toggleHeld) return;
        _toggleHeld = true;
        Dispatch(_recording.IsActive ? StopCoreAsync : StartCoreAsync);
    }

    private void OnShortcutReleased(object? sender, ShortcutTriggeredEventArgs args)
    {
        if (args.Name == ToggleActionName) _toggleHeld = false;
    }

    private void OnPushToTalkPressed(object? sender, EventArgs args)
    {
        if (_started && !_toggleHeld && !_recording.IsActive) Dispatch(StartCoreAsync);
        else if (_recording.IsActive) _pushToTalk.ResetToIdle();
    }

    private void OnPushToTalkReleased(object? sender, EventArgs args)
    {
        if (_started && !_toggleHeld && _recording.IsActive) Dispatch(StopCoreAsync);
    }

    private void OnPushToTalkInterfered(object? sender, EventArgs args)
    {
        if (_started && !_toggleHeld && _recording.IsActive) Dispatch(CancelCoreAsync);
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
            if (started.IsSuccess) return;
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
        try { await _recording.CancelAsync(cancellationToken); }
        finally
        {
            _textInjection.EndSession();
            _ = await _textInjection.RestoreClipboardImmediatelyAsync(CancellationToken.None);
            _pushToTalk.ResetToIdle();
        }
    }

    private void RaiseFailure(PlatformError error)
    {
        var handlers = OperationFailed;
        if (handlers is null) return;
        foreach (EventHandler<PlatformError> handler in handlers.GetInvocationList())
            try { handler(this, error); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
    }
}
