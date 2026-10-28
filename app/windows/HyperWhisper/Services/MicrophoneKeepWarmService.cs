// MICROPHONE KEEP-WARM SERVICE
// Keeps an idle capture stream open to reduce microphone startup latency.

using NAudio.Wave;

namespace HyperWhisper.Services;

public sealed class MicrophoneKeepWarmService : IDisposable
{
    private static readonly Lazy<MicrophoneKeepWarmService> _instance = new(() => new MicrophoneKeepWarmService());
    public static MicrophoneKeepWarmService Instance => _instance.Value;

    private readonly object _lock = new();
    private readonly Func<int, IWaveIn> _captureFactory;
    private IWaveIn? _waveIn;
    private int? _activeDeviceNumber;
    private bool _enabled;
    private bool _suspended;
    private bool _disposed;

    private MicrophoneKeepWarmService() : this(CreateWaveIn) { }

    /// <summary>
    /// Test seam (#2244): HyperWhisper.SmokeTests (InternalsVisibleTo, see
    /// HyperWhisper.csproj) passes a fake capture so it can drive the state
    /// machine below without a microphone. The app uses <see cref="Instance"/>.
    /// </summary>
    internal MicrophoneKeepWarmService(Func<int, IWaveIn> captureFactory)
    {
        _captureFactory = captureFactory;
    }

    private static IWaveIn CreateWaveIn(int deviceNumber) => new WaveInEvent
    {
        DeviceNumber = deviceNumber,
        WaveFormat = new WaveFormat(16000, 16, 1),
        BufferMilliseconds = 250
    };

    public void Configure(bool enabled, int? deviceNumber)
    {
        lock (_lock)
        {
            _enabled = enabled;
            ApplyLocked(deviceNumber);
        }
    }

    public void SuspendForRecording()
    {
        lock (_lock)
        {
            _suspended = true;
            StopLocked("recording-started");
        }
    }

    public void ResumeAfterRecording(int? deviceNumber)
    {
        // One lock for both steps (#2501): reading _enabled after the lock was
        // released let a concurrent Configure(false, ...) be overwritten by the
        // stale value, reopening the mic the user had just turned off.
        lock (_lock)
        {
            _suspended = false;
            ApplyLocked(deviceNumber);
        }
    }

    private void ApplyLocked(int? deviceNumber)
    {
        if (!_enabled || _suspended || !deviceNumber.HasValue)
        {
            StopLocked(_enabled ? "unavailable-or-suspended" : "disabled");
            return;
        }

        if (_waveIn != null && _activeDeviceNumber == deviceNumber.Value)
            return;

        StopLocked("device-changed");
        StartLocked(deviceNumber.Value);
    }

    private void StartLocked(int deviceNumber)
    {
        if (_disposed)
            return;

        try
        {
            _waveIn = _captureFactory(deviceNumber);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _activeDeviceNumber = deviceNumber;
            LoggingService.Info($"MicrophoneKeepWarmService: Started on device #{deviceNumber}");
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MicrophoneKeepWarmService: Failed to start on device #{deviceNumber}: {ex.Message}");
            CleanupWaveInLocked();
        }
    }

    private void StopLocked(string reason)
    {
        if (_waveIn == null)
            return;

        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"MicrophoneKeepWarmService: Stop failed ({reason}): {ex.Message}");
        }
        finally
        {
            CleanupWaveInLocked();
            LoggingService.Info($"MicrophoneKeepWarmService: Stopped ({reason})");
        }
    }

    private void CleanupWaveInLocked()
    {
        if (_waveIn == null)
            return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
        _activeDeviceNumber = null;
    }

    private static void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Intentionally discard all audio. The stream exists only to keep the
        // capture path initialized between real recordings.
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            LoggingService.Warn($"MicrophoneKeepWarmService: Recording stopped with error: {e.Exception.Message}");

            lock (_lock)
            {
                if (!ReferenceEquals(sender, _waveIn))
                    return;

                var deviceNumber = _activeDeviceNumber;
                CleanupWaveInLocked();

                if (_enabled && !_suspended && !_disposed && deviceNumber.HasValue)
                {
                    LoggingService.Info($"MicrophoneKeepWarmService: Restarting after unexpected stop on device #{deviceNumber.Value}");
                    StartLocked(deviceNumber.Value);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            StopLocked("dispose");
        }
    }
}
