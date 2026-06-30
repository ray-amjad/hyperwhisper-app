// AUDIO ENVIRONMENT SERVICE
// Handles output audio changes while recording, matching macOS media-control behavior.

using NAudio.CoreAudioApi;

namespace HyperWhisper.Services;

public sealed class AudioEnvironmentService
{
    private static readonly Lazy<AudioEnvironmentService> _instance = new(() => new AudioEnvironmentService());
    public static AudioEnvironmentService Instance => _instance.Value;

    private AudioEnvironmentService() { }

    private readonly object _restoreLock = new();
    private int _restoreGeneration;
    private CancellationTokenSource? _pendingRestoreCts;
    private AudioEnvironmentState? _pendingRestoreState;

    public sealed record AudioEnvironmentRestoreClaim(
        int Generation,
        AudioEnvironmentState? InheritedRestoreState);

    public sealed record AudioEnvironmentState(
        string DeviceId,
        string DeviceName,
        bool WasMuted,
        bool MutedByHyperWhisper,
        int Generation);

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

        pendingCts?.Cancel();

        var inheritedRestoreState = pendingState?.MutedByHyperWhisper == true
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

        if (!SettingsService.Instance.MediaControlMode.Equals("muteAudio", StringComparison.OrdinalIgnoreCase))
        {
            return inheritedRestoreState;
        }

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
                restoreClaim.Generation);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioEnvironmentService: Failed to mute output audio: {ex.Message}");
            return inheritedRestoreState;
        }
    }

    /// <summary>
    /// Restores output audio after recording. If the user manually unmuted during
    /// recording, this leaves their current state alone.
    /// </summary>
    public void ScheduleRestoreAfterRecording(AudioEnvironmentState? state)
    {
        if (state == null || !state.MutedByHyperWhisper)
        {
            return;
        }

        var restoreCts = RegisterPendingRestore(state);
        if (restoreCts == null)
        {
            LoggingService.Debug("AudioEnvironmentService: Skipping stale output restore");
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

        lock (_restoreLock)
        {
            _restoreGeneration++;
            pendingCts = _pendingRestoreCts;
            var pendingState = _pendingRestoreState;
            var restoreState = state?.MutedByHyperWhisper == true
                ? state
                : pendingState;
            _pendingRestoreCts = null;
            _pendingRestoreState = null;

            if (restoreState == null || !restoreState.MutedByHyperWhisper)
            {
                pendingCts?.Cancel();
                return Task.CompletedTask;
            }

            try
            {
                RestoreMuteState(restoreState, 0);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"AudioEnvironmentService: Failed to immediately restore output audio: {ex.Message}");
            }
        }

        pendingCts?.Cancel();
        return Task.CompletedTask;
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

        previousCts?.Cancel();
        return restoreCts;
    }

    private void RestorePendingMuteState(
        AudioEnvironmentState state,
        CancellationTokenSource restoreCts,
        double delaySeconds)
    {
        lock (_restoreLock)
        {
            if (!ReferenceEquals(_pendingRestoreCts, restoreCts)
                || _pendingRestoreState?.Generation != state.Generation
                || state.Generation != _restoreGeneration)
            {
                LoggingService.Debug("AudioEnvironmentService: Skipping stale output restore");
                return;
            }

            RestoreMuteState(state, delaySeconds);
            _pendingRestoreCts = null;
            _pendingRestoreState = null;
        }
    }

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
}
