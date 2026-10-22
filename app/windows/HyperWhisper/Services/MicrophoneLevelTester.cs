using NAudio.Wave;

namespace HyperWhisper.Services;

/// <summary>
/// The capture half of Settings -> Sound -> Microphone -> "Test": a short-lived
/// WaveInEvent on one device, fed through <see cref="AudioLevelMath"/>. Opened
/// only on demand, never by visiting the page.
///
/// Same lifecycle as the onboarding level preview
/// (LiveOnboardingAudioGateway.StartPreviewLocked / StopPreviewLocked): keep-warm
/// is suspended while the meter holds the endpoint, and resumed on the device
/// the APP records from (not necessarily the tested one) when it lets go.
///
/// <see cref="LevelChanged"/> is raised on NAudio's capture thread; the caller
/// marshals. Timing (the ~10 s auto-stop) belongs to the caller too.
/// </summary>
public sealed class MicrophoneLevelTester : IDisposable
{
    private readonly MicrophoneKeepWarmService _keepWarm;
    private readonly Func<int?> _appDeviceNumber;
    private readonly object _lock = new();

    private WaveInEvent? _capture;
    private float _level;
    private bool _disposed;

    public MicrophoneLevelTester(MicrophoneKeepWarmService keepWarm, Func<int?> appDeviceNumber)
    {
        _keepWarm = keepWarm;
        _appDeviceNumber = appDeviceNumber;
    }

    /// <summary>Raised with each new level, 0 to 1, on the capture thread.</summary>
    public event EventHandler<float>? LevelChanged;

    public bool IsRunning
    {
        get { lock (_lock) return _capture != null; }
    }

    /// <summary>
    /// Opens <paramref name="deviceNumber"/> (WaveIn index; -1 = WAVE_MAPPER).
    /// </summary>
    /// <returns>True only when a capture stream is actually running afterwards.</returns>
    public bool Start(int deviceNumber)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_capture != null) return true;

            try
            {
                // The keep-warm stream holds the same endpoint open.
                _keepWarm.SuspendForRecording();

                var capture = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 50
                };
                capture.DataAvailable += OnData;
                capture.StartRecording();
                _capture = capture;
                _level = 0f;
                LoggingService.Info($"MicrophoneLevelTester: test started on device #{deviceNumber}");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"MicrophoneLevelTester: could not open device #{deviceNumber}: {ex.Message}");
                _capture = null;
                _keepWarm.ResumeAfterRecording(_appDeviceNumber());
                return false;
            }
        }
    }

    /// <summary>Releases the device. Safe to call when idle.</summary>
    /// <param name="resumeKeepWarm">
    /// False when a recording is about to open the microphone: it suspends
    /// keep-warm itself and resumes it afterwards, so reopening the warm stream
    /// here would only cost the dictation start a WASAPI activation.
    /// </param>
    public void Stop(bool resumeKeepWarm = true)
    {
        WaveInEvent? capture;
        lock (_lock)
        {
            capture = _capture;
            if (capture == null) return;
            _capture = null;
            capture.DataAvailable -= OnData;
        }

        // Two try blocks, as in the onboarding preview: StopRecording throws when
        // the endpoint was unplugged under the stream, and Dispose is what
        // actually releases the device, so it must run either way.
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"MicrophoneLevelTester: stop failed: {ex.Message}");
        }

        try
        {
            capture.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"MicrophoneLevelTester: dispose failed: {ex.Message}");
        }

        if (resumeKeepWarm)
            _keepWarm.ResumeAfterRecording(_appDeviceNumber());
        _level = 0f;
        LoggingService.Info("MicrophoneLevelTester: test stopped");
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var next = AudioLevelMath.NextLevel(e.Buffer, e.BytesRecorded, _level);
        _level = next;

        try
        {
            LevelChanged?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"MicrophoneLevelTester: a level handler failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
