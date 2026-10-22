// AUDIO ENVIRONMENT SERVICE
// Handles output audio changes while recording, matching macOS media-control behavior.
// "muteAudio" mutes the default output. "muffleAudio" low-passes every other app through
// Services/Muffle/WindowsAudioMuffler, and mutes instead where that cannot run.

using System.IO;
using HyperWhisper.Platform.Abstractions.Audio;
using HyperWhisper.Services.Muffle;
using NAudio.CoreAudioApi;

namespace HyperWhisper.Services;

public sealed class AudioEnvironmentService
{
    public const string MuteAudioMode = "muteAudio";
    public const string MuffleAudioMode = "muffleAudio";
    private const string MuffleJournalFileName = "muffle-restore.json";

    private static readonly Lazy<AudioEnvironmentService> _instance = new(() => new AudioEnvironmentService());
    public static AudioEnvironmentService Instance => _instance.Value;

    private AudioEnvironmentService() { }

    private readonly object _restoreLock = new();
    private int _restoreGeneration;
    private CancellationTokenSource? _pendingRestoreCts;
    private AudioEnvironmentState? _pendingRestoreState;

    // Every muffler start, engage, release and teardown runs one at a time behind this gate, on the
    // thread pool: a start takes a few hundred milliseconds and must not hold the UI thread.
    private readonly SemaphoreSlim _muffleGate = new(1, 1);
    private readonly Lazy<MuffleRestoreJournal> _muffleJournal = new(
        () => new MuffleRestoreJournal(Path.Combine(AppPaths.AppDataRoot, MuffleJournalFileName)));
    private WindowsAudioMuffler? _muffler;               // guarded by _muffleGate
    // Guarded by _restoreLock (not only _muffleGate): a following Mute Audio recording takes it over
    // synchronously, and set, take-over and restore must not interleave with the mute itself.
    private AudioEnvironmentState? _muffleFallbackMute;
    private volatile bool _shuttingDown;
    // Cancelled at exit: a muffler start that is still lowering apps stops and puts them back.
    private readonly CancellationTokenSource _shutdownCts = new();

    public sealed record AudioEnvironmentRestoreClaim(
        int Generation,
        AudioEnvironmentState? InheritedRestoreState);

    public sealed record AudioEnvironmentState(
        string DeviceId,
        string DeviceName,
        bool WasMuted,
        bool MutedByHyperWhisper,
        int Generation)
    {
        /// <summary>
        /// The recording asked for a muffle. The service owns the muffler (or the mute it fell back
        /// to), so the restore goes through the service rather than this state's device fields.
        /// </summary>
        public bool MuffledByHyperWhisper { get; init; }

        public bool NeedsRestore => MutedByHyperWhisper || MuffledByHyperWhisper;

        internal static AudioEnvironmentState ForMuffle(int generation) =>
            new(string.Empty, string.Empty, false, false, generation) { MuffledByHyperWhisper = true };
    }

    /// <summary>
    /// Claims ownership of any pending restore before recorder startup, without
    /// changing mute state. The returned claim must be passed to PrepareForRecording
    /// after recorder startup succeeds.
    /// </summary>
    public AudioEnvironmentRestoreClaim ClaimRestoreOwnershipForRecording()
    {
        CancellationTokenSource? pendingCts;
        AudioEnvironmentState? pendingState;
        int generation;

        lock (_restoreLock)
        {
            generation = ++_restoreGeneration;
            pendingCts = _pendingRestoreCts;
            pendingState = _pendingRestoreState;
            _pendingRestoreCts = null;
            _pendingRestoreState = null;
        }

        // A muffle that is still gliding open stops its teardown here and is taken over below.
        SafeCancel(pendingCts);

        var inheritedRestoreState = pendingState?.NeedsRestore == true
            ? pendingState with { Generation = generation }
            : null;

        if (inheritedRestoreState != null)
        {
            LoggingService.Debug("AudioEnvironmentService: Cancelled pending restore and transferred audio restore ownership to new recording");
        }

        return new AudioEnvironmentRestoreClaim(generation, inheritedRestoreState);
    }

    /// <summary>
    /// Applies the configured recording media-control behavior after recording starts.
    /// Returns state only when restoration may be needed.
    /// </summary>
    public AudioEnvironmentState? PrepareForRecording(AudioEnvironmentRestoreClaim restoreClaim)
    {
        var inheritedRestoreState = restoreClaim.InheritedRestoreState;
        var mode = SettingsService.Instance.MediaControlMode;

        if (mode.Equals(MuffleAudioMode, StringComparison.OrdinalIgnoreCase))
        {
            if (inheritedRestoreState?.MutedByHyperWhisper == true)
            {
                TryRestoreMuteState(inheritedRestoreState, "the media control mode is now Muffle audio");
            }

            var generation = restoreClaim.Generation;
            _ = RunMuffleOperationAsync(() => EngageMuffle(generation), "Muffle start");
            return AudioEnvironmentState.ForMuffle(generation);
        }

        var muteMode = mode.Equals(MuteAudioMode, StringComparison.OrdinalIgnoreCase);
        if (!muteMode)
        {
            if (inheritedRestoreState?.MuffledByHyperWhisper == true)
            {
                // The mode changed while the last muffle was still gliding open: end it now instead of
                // carrying it through a recording that did not ask for it.
                _ = RunMuffleOperationAsync(() => TearDownMuffle("the media control mode changed"), "Muffle teardown");
                inheritedRestoreState = null;
            }

            return inheritedRestoreState;
        }

        lock (_restoreLock)
        {
            // A muffle that fell back to muting still holds the output muted. This recording mutes
            // too, so it takes that mute over (and its WasMuted) instead of seeing an already muted
            // output, owning nothing, and having the muffle teardown below unmute it mid-recording.
            var fallbackMute = TakeMuffleFallbackMuteLocked(restoreClaim.Generation);
            if (inheritedRestoreState?.MuffledByHyperWhisper == true)
            {
                _ = RunMuffleOperationAsync(() => TearDownMuffle("the media control mode changed"), "Muffle teardown");
                inheritedRestoreState = null;
            }

            var owned = fallbackMute ?? inheritedRestoreState;
            return MuteOutput(restoreClaim.Generation, owned) ?? owned;
        }
    }

    // Caller holds _restoreLock.
    private AudioEnvironmentState? TakeMuffleFallbackMuteLocked(int generation)
    {
        var fallbackMute = _muffleFallbackMute;
        _muffleFallbackMute = null;
        if (fallbackMute == null)
        {
            return null;
        }

        LoggingService.Debug("AudioEnvironmentService: Mute Audio recording took over the muffle fallback mute");
        return fallbackMute with { Generation = generation };
    }

    /// <summary>
    /// Restores output audio after recording. If the user manually unmuted during
    /// recording, this leaves their current state alone. A muffle glides open first and
    /// is torn down <see cref="MuffleFilter.TeardownDelay"/> later, unless
    /// <paramref name="immediate"/> (cancel paths) skips the glide.
    /// </summary>
    public void ScheduleRestoreAfterRecording(AudioEnvironmentState? state, bool immediate = false)
    {
        if (state == null || !state.NeedsRestore)
        {
            return;
        }

        var restoreCts = RegisterPendingRestore(state);
        if (restoreCts == null)
        {
            LoggingService.Debug("AudioEnvironmentService: Skipping stale output restore");
            return;
        }

        if (state.MuffledByHyperWhisper)
        {
            _ = RunMuffleOperationAsync(() => ReleaseMuffle(state, restoreCts, immediate), "Muffle restore");
            return;
        }

        try
        {
            RestorePendingMuteState(state, restoreCts, 0);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Failed to restore output audio: {ex.Message}");
        }
        finally
        {
            ClearPendingRestore(state, restoreCts);
        }
    }

    /// <summary>
    /// Restores output audio immediately during shutdown. This also flushes a
    /// pending restore that was already handed off to the service.
    /// </summary>
    public Task RestoreAfterRecordingImmediatelyAsync(AudioEnvironmentState? state)
    {
        CancellationTokenSource? pendingCts;
        AudioEnvironmentState? restoreState;

        lock (_restoreLock)
        {
            _restoreGeneration++;
            pendingCts = _pendingRestoreCts;
            var pendingState = _pendingRestoreState;
            restoreState = state?.NeedsRestore == true
                ? state
                : pendingState;
            _pendingRestoreCts = null;
            _pendingRestoreState = null;

            if (restoreState?.MutedByHyperWhisper == true)
            {
                try
                {
                    RestoreMuteState(restoreState, 0);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"AudioEnvironmentService: Failed to immediately restore output audio: {ex.Message}");
                }
            }
        }

        SafeCancel(pendingCts);

        return restoreState?.MuffledByHyperWhisper == true
            ? RunMuffleOperationAsync(() => TearDownMuffle("restored immediately"), "Muffle teardown")
            : Task.CompletedTask;
    }

    /// <summary>
    /// Startup: puts back any app volume an earlier run left lowered (a crash or a kill mid-muffle),
    /// then keeps watching for the apps that were not running yet. Call off the UI thread.
    /// </summary>
    public void RecoverMuffleVolumesFromEarlierRun()
    {
        try
        {
            if (!File.Exists(Path.Combine(AppPaths.AppDataRoot, MuffleJournalFileName)))
            {
                return;
            }

            var journal = _muffleJournal.Value;
            journal.RecoverStale();
            if (journal.HasStaleEntries)
            {
                journal.WatchForStaleSessions();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Muffle volume recovery failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Exit safety net: stops a muffler start that is still lowering apps, tears down a muffle that
    /// is running (its volumes come back) and stops any later start. If that has not finished
    /// within two thirds of <paramref name="timeout"/> (a start stuck in a capture activation holds
    /// the muffle gate), the volumes are put back straight from the journal in the time left.
    /// Waits at most <paramref name="timeout"/> in all.
    /// </summary>
    public void ShutdownMuffle(TimeSpan timeout)
    {
        _shuttingDown = true;
        SafeCancel(_shutdownCts);
        var orderlyBudget = TimeSpan.FromTicks(timeout.Ticks * 2 / 3);
        var emergencyBudget = timeout - orderlyBudget;

        try
        {
            var teardown = RunMuffleOperationAsync(() =>
            {
                TearDownMuffle("app exit");
                if (_muffleJournal.IsValueCreated)
                {
                    _muffleJournal.Value.Dispose();
                }
            }, "Muffle shutdown");

            if (teardown.Wait(orderlyBudget))
            {
                return;
            }

            if (!_muffleJournal.IsValueCreated)
            {
                LoggingService.Warn("AudioEnvironmentService: Muffle shutdown timed out before the muffler ever ran");
                return;
            }

            LoggingService.Warn("AudioEnvironmentService: Muffle shutdown is taking too long; restoring the volumes from the journal");
            var journal = _muffleJournal.Value;
            if (!Task.Run(journal.RestoreLiveNow).Wait(emergencyBudget))
            {
                LoggingService.Warn("AudioEnvironmentService: Muffle exit restore timed out; the next start restores the volumes");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Muffle shutdown failed: {ex.Message}");
        }
    }

    private CancellationTokenSource? RegisterPendingRestore(AudioEnvironmentState state)
    {
        CancellationTokenSource? previousCts;
        CancellationTokenSource restoreCts;

        lock (_restoreLock)
        {
            if (state.Generation != _restoreGeneration)
            {
                return null;
            }

            previousCts = _pendingRestoreCts;
            restoreCts = new CancellationTokenSource();
            _pendingRestoreCts = restoreCts;
            _pendingRestoreState = state;
        }

        SafeCancel(previousCts);
        return restoreCts;
    }

    private void RestorePendingMuteState(
        AudioEnvironmentState state,
        CancellationTokenSource restoreCts,
        double delaySeconds)
    {
        lock (_restoreLock)
        {
            if (!IsPendingRestoreLocked(state, restoreCts))
            {
                LoggingService.Debug("AudioEnvironmentService: Skipping stale output restore");
                return;
            }

            RestoreMuteState(state, delaySeconds);
            _pendingRestoreCts = null;
            _pendingRestoreState = null;
        }
    }

    private bool IsPendingRestoreLocked(AudioEnvironmentState state, CancellationTokenSource restoreCts) =>
        ReferenceEquals(_pendingRestoreCts, restoreCts)
        && _pendingRestoreState?.Generation == state.Generation
        && state.Generation == _restoreGeneration;

    private void ClearPendingRestore(AudioEnvironmentState state, CancellationTokenSource restoreCts)
    {
        lock (_restoreLock)
        {
            if (ReferenceEquals(_pendingRestoreCts, restoreCts)
                && _pendingRestoreState?.Generation == state.Generation)
            {
                _pendingRestoreCts = null;
                _pendingRestoreState = null;
            }
        }

        restoreCts.Dispose();
    }

    // ---------------------------------------------------------------- mute

    private static AudioEnvironmentState? MuteOutput(int generation, AudioEnvironmentState? inheritedRestoreState)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var wasMuted = device.AudioEndpointVolume.Mute;
            var inheritedCurrentMute = inheritedRestoreState?.MutedByHyperWhisper == true;

            if (!wasMuted)
            {
                device.AudioEndpointVolume.Mute = true;
                LoggingService.Info($"AudioEnvironmentService: Muted output device for recording ({device.FriendlyName})");
            }
            else if (inheritedCurrentMute)
            {
                LoggingService.Debug($"AudioEnvironmentService: Output device already muted by pending HyperWhisper restore; taking ownership ({device.FriendlyName})");
            }
            else
            {
                LoggingService.Debug($"AudioEnvironmentService: Output device already muted ({device.FriendlyName})");
            }

            return new AudioEnvironmentState(
                device.ID,
                device.FriendlyName,
                inheritedRestoreState?.WasMuted ?? wasMuted,
                !wasMuted || inheritedCurrentMute,
                generation);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Failed to mute output audio: {ex.Message}");
            return null;
        }
    }

    private static void TryRestoreMuteState(AudioEnvironmentState state, string reason)
    {
        try
        {
            LoggingService.Debug($"AudioEnvironmentService: Restoring output mute state because {reason}");
            RestoreMuteState(state, 0);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Failed to restore output audio: {ex.Message}");
        }
    }

    private static void RestoreMuteState(AudioEnvironmentState state, double delaySeconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = GetDeviceOrDefault(enumerator, state.DeviceId);
        if (device.AudioEndpointVolume.Mute)
        {
            device.AudioEndpointVolume.Mute = state.WasMuted;
            LoggingService.Info($"AudioEnvironmentService: Restored output mute state ({device.FriendlyName}, delay={delaySeconds:F1}s)");
        }
        else
        {
            LoggingService.Info("AudioEnvironmentService: Skipping output restore because audio was already unmuted");
        }
    }

    private static MMDevice GetDeviceOrDefault(MMDeviceEnumerator enumerator, string deviceId)
    {
        try
        {
            return enumerator.GetDevice(deviceId);
        }
        catch
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
    }

    // ---------------------------------------------------------------- muffle

    private async Task RunMuffleOperationAsync(Func<Task> operation, string name)
    {
        await _muffleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(operation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: {name} failed: {ex.Message}");
        }
        finally
        {
            _muffleGate.Release();
        }
    }

    private Task RunMuffleOperationAsync(Action operation, string name) =>
        RunMuffleOperationAsync(() =>
        {
            operation();
            return Task.CompletedTask;
        }, name);

    // Runs behind _muffleGate.
    private void EngageMuffle(int generation)
    {
        if (_shuttingDown || !IsCurrentGeneration(generation))
        {
            LoggingService.Debug("AudioEnvironmentService: Skipping stale muffle start");
            return;
        }

        if (_muffler is { IsFaulted: false } running)
        {
            running.Engage();
            LoggingService.Debug("AudioEnvironmentService: New recording took over the running muffle");
            return;
        }

        if (_muffler != null)
        {
            TearDownMuffle("its output failed");
        }

        lock (_restoreLock)
        {
            if (_muffleFallbackMute != null)
            {
                LoggingService.Debug("AudioEnvironmentService: Output is still muted from the last fallback; keeping it");
                return;
            }
        }

        var started = WindowsAudioMuffler.Start(_muffleJournal.Value, cancellation: _shutdownCts.Token);
        if (started.IsSuccess)
        {
            _muffler = started.Value;
            _muffler!.Engage();
            return;
        }

        if (_shuttingDown)
        {
            // The start saw the exit and put back what it had lowered; nothing to fall back to.
            LoggingService.Debug($"AudioEnvironmentService: Muffle start stopped for app exit ({started.Error})");
            return;
        }

        LoggingService.Warn($"AudioEnvironmentService: Muffle audio is unavailable ({started.Error}); muting the output instead");
        lock (_restoreLock)
        {
            // A failed start can take seconds; if another recording claimed the output meanwhile
            // (or the app restored immediately), this mute would belong to no one.
            if (generation != _restoreGeneration)
            {
                LoggingService.Debug("AudioEnvironmentService: Skipping the fallback mute for a recording that is over");
                return;
            }

            var muted = MuteOutput(generation, null);
            _muffleFallbackMute = muted?.MutedByHyperWhisper == true ? muted : null;
        }
    }

    // Runs behind _muffleGate.
    private async Task ReleaseMuffle(AudioEnvironmentState state, CancellationTokenSource restoreCts, bool immediate)
    {
        try
        {
            lock (_restoreLock)
            {
                if (!IsPendingRestoreLocked(state, restoreCts))
                {
                    LoggingService.Debug("AudioEnvironmentService: Skipping stale muffle restore");
                    return;
                }
            }

            if (!immediate && _muffler is { IsFaulted: false } muffler)
            {
                muffler.Release();
                try
                {
                    await Task.Delay(MuffleFilter.TeardownDelay, restoreCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LoggingService.Debug("AudioEnvironmentService: A new recording took over the muffle while it glided open");
                    return;
                }
            }

            lock (_restoreLock)
            {
                if (!IsPendingRestoreLocked(state, restoreCts))
                {
                    LoggingService.Debug("AudioEnvironmentService: A new recording took over the muffle");
                    return;
                }

                _pendingRestoreCts = null;
                _pendingRestoreState = null;
            }

            TearDownMuffle(immediate ? "recording cancelled" : "recording ended");
        }
        finally
        {
            ClearPendingRestore(state, restoreCts);
        }
    }

    // Runs behind _muffleGate. Disposing the muffler stops its output first, then restores the volumes.
    private void TearDownMuffle(string reason)
    {
        var muffler = _muffler;
        _muffler = null;
        if (muffler != null)
        {
            LoggingService.Debug($"AudioEnvironmentService: Ending muffle ({reason})");
            try
            {
                muffler.Dispose();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"AudioEnvironmentService: Muffle teardown failed: {ex.Message}");
            }
        }

        // Under _restoreLock, so a Mute Audio recording either takes this mute over first or sees
        // the output already unmuted, never the moment in between.
        lock (_restoreLock)
        {
            var fallbackMute = _muffleFallbackMute;
            _muffleFallbackMute = null;
            if (fallbackMute != null)
            {
                TryRestoreMuteState(fallbackMute, reason);
            }
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_restoreLock)
        {
            return generation == _restoreGeneration;
        }
    }

    private static void SafeCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The restore already finished and disposed its token.
        }
    }
}
