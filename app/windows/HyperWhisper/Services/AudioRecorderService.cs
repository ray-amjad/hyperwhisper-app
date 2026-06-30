using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Service for recording audio from a microphone.
/// Records in 16kHz mono WAV format as required by Whisper.
/// </summary>
public class AudioRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private readonly object _writerLock = new();
    private string? _tempFilePath;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();

    // Mic volume boost fields
    private const float MicVolumeBoostThreshold = 0.5f;
    private const float MicVolumeBoostTarget = 0.9f;
    private const float MicVolumeTolerance = 0.02f;
    private const float MicVolumeRestoreTolerance = 0.08f;
    private float? _originalMicVolume;
    private MMDevice? _captureDevice;

    // Envelope follower state for audio level visualization
    private float _displayedLevel;

    /// <summary>
    /// Whether recording is currently in progress.
    /// </summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Current recording duration.
    /// </summary>
    public TimeSpan Duration => _stopwatch.Elapsed;

    /// <summary>
    /// Event raised when audio level changes (0.0 to 1.0).
    /// </summary>
    public event Action<float>? AudioLevelChanged;

    /// <summary>
    /// Starts recording audio from the specified device.
    /// </summary>
    /// <param name="deviceNumber">The device number to record from (from AudioDeviceService).</param>
    public void StartRecording(int deviceNumber)
    {
        if (IsRecording) return;

        _displayedLevel = 0f;

        try
        {
            LoggingService.Info($"AudioRecorderService: Starting recording on device #{deviceNumber}");
            // Create temp file for recording
            _tempFilePath = Path.Combine(Path.GetTempPath(), $"hyperwhisper_{Guid.NewGuid()}.wav");

            // Configure recording: 16kHz mono 16-bit PCM (required by Whisper)
            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            _stopwatch.Restart();
            IsRecording = true;
        }
        catch
        {
            var failedTempPath = _tempFilePath;
            _stopwatch.Reset();
            CleanupRecording();

            if (!string.IsNullOrEmpty(failedTempPath) && File.Exists(failedTempPath))
            {
                try
                {
                    File.Delete(failedTempPath);
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"AudioRecorderService: Failed to delete temp file after start failure - {ex.Message}");
                }
            }

            _tempFilePath = null;
            throw;
        }
    }

    /// <summary>
    /// Stops recording and returns the path to the recorded WAV file.
    ///
    /// CRITICAL: The WaveFileWriter is disposed synchronously BEFORE returning.
    /// This ensures the file is fully written and the lock is released,
    /// so the caller can immediately move/copy the file.
    ///
    /// Previously, this was async via OnRecordingStopped callback, causing
    /// a race condition where HistoryService.SaveAudioFile() would try to
    /// move the file while it was still locked by the writer.
    ///
    /// RESULT PATTERN: Returns Result<string> to force callers to handle both
    /// success (audio file path) and failure (not recording) cases explicitly.
    /// This prevents silent failures where null paths are not checked.
    /// </summary>
    public Result<string> StopRecording()
    {
        // GUARD CLAUSE: Check if recording is in progress
        if (!IsRecording)
        {
            LoggingService.Debug("AudioRecorderService: StopRecording called but not recording");
            return Result<string>.Failure(Loc.S("audio.error.notRecording"));
        }

        // GUARD CLAUSE: Verify temp file path exists
        if (string.IsNullOrEmpty(_tempFilePath))
        {
            LoggingService.Error("AudioRecorderService: StopRecording - temp file path is null/empty");
            IsRecording = false;
            return Result<string>.Failure(Loc.S("audio.error.recordingPathNotSet"));
        }

        _stopwatch.Stop();
        IsRecording = false;
        LoggingService.Info($"AudioRecorderService: StopRecording (duration={_stopwatch.Elapsed.TotalSeconds:F2}s, path={_tempFilePath})");

        try
        {
            // CRITICAL: Dispose writer synchronously BEFORE returning
            // This ensures the file is fully written and released
            // The previous async approach caused race conditions
            //
            // THREAD SAFETY: _writerLock makes dispose mutually exclusive with
            // OnDataAvailable's Write on the NAudio capture thread, which can
            // still fire until RecordingStopped. Without it, a late buffer can
            // hit the disposed writer (ObjectDisposedException) or interleave
            // with the WAV header finalization and corrupt the file.
            lock (_writerLock)
            {
                _writer?.Dispose();
                _writer = null;
            }

            // Stop the recording device - this will still fire OnRecordingStopped
            // asynchronously, but the callback will just clean up _waveIn
            _waveIn?.StopRecording();

            // SUCCESS: Return the path to the completed WAV file
            // The file is now fully written and the lock is released
            return Result<string>.Success(_tempFilePath);
        }
        catch (Exception ex)
        {
            // FAILURE HANDLING: Log exception and return failure result
            // Cleanup will still happen via OnRecordingStopped callback
            LoggingService.Error("AudioRecorderService: Error stopping recording", ex);
            return Result<string>.Failure(Loc.S("audio.error.stopRecordingFailed"), ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Write audio data to file
        // THREAD SAFETY: runs on the NAudio capture thread; _writerLock prevents
        // racing StopRecording()/CleanupRecording() disposing the writer
        lock (_writerLock)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // Compute RMS over the buffer (log-scaled perceptual level)
        double sumSquares = 0;
        int sampleCount = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            double norm = sample / 32768.0;
            sumSquares += norm * norm;
            sampleCount++;
        }

        float normalized = 0f;
        if (sampleCount > 0)
        {
            double rms = Math.Sqrt(sumSquares / sampleCount);
            // Convert to dB, floor at -60 dB for silence
            double db = 20.0 * Math.Log10(Math.Max(rms, 1e-6));
            // Map -60 dB → 0.0, -6 dB → 1.0 (soft clip above)
            normalized = (float)Math.Clamp((db + 60.0) / 54.0, 0.0, 1.0);
        }

        // Fast-attack / slow-decay envelope follower
        // Attack: immediate jump up. Decay: exponential fall (~200ms at 16kHz/50ms buffers = ~0.78 per buffer)
        if (normalized > _displayedLevel)
            _displayedLevel = normalized;                      // fast attack
        else
            _displayedLevel = Math.Max(normalized, _displayedLevel * 0.85f);  // slow decay

        AudioLevelChanged?.Invoke(_displayedLevel);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        CleanupRecording();
    }

    private void CleanupRecording()
    {
        IsRecording = false;
        _displayedLevel = 0f;

        // Writer is now disposed in StopRecording() synchronously,
        // but check anyway for safety
        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
        }

        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    /// <summary>
    /// Resolves the Core Audio (MMDevice) capture endpoint that corresponds to a
    /// NAudio WaveIn index, so volume boost/restore acts on the device actually
    /// being recorded rather than the system default comms mic.
    ///
    /// NAudio's <c>WaveIn.GetCapabilities(n).ProductName</c> is truncated to 31
    /// characters (the MME WAVEINCAPS limit), so we match it as a prefix of the
    /// full Core Audio <c>FriendlyName</c> instead of by equality. A
    /// <paramref name="deviceNumber"/> of -1 is NAudio's "default device"
    /// sentinel; when out of range, unmatched, or ambiguous we fall back to the
    /// default comms endpoint so the feature degrades gracefully instead of throwing.
    /// </summary>
    private static MMDevice ResolveCaptureDevice(MMDeviceEnumerator enumerator, int deviceNumber)
    {
        if (deviceNumber < 0 || deviceNumber >= WaveIn.DeviceCount)
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

        string targetName;
        try
        {
            targetName = WaveIn.GetCapabilities(deviceNumber).ProductName;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"AudioRecorderService: Failed to read capabilities for device #{deviceNumber}, using default endpoint - {ex.Message}");
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        MMDevice? matchedDevice = null;

        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var keepDevice = false;
                try
                {
                    if (string.IsNullOrEmpty(targetName) ||
                        !device.FriendlyName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (matchedDevice != null)
                    {
                        LoggingService.Debug($"AudioRecorderService: Multiple capture endpoints match truncated device name '{targetName}', using default endpoint");
                        DisposeCaptureDevice(matchedDevice, "previous matched capture endpoint");
                        matchedDevice = null;
                        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    }

                    matchedDevice = device;
                    keepDevice = true;
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"AudioRecorderService: Skipping capture endpoint while resolving device #{deviceNumber} - {ex.Message}");
                }
                finally
                {
                    if (!keepDevice)
                        DisposeCaptureDevice(device, "capture endpoint");
                }
            }
        }
        catch (Exception ex)
        {
            DisposeCaptureDevice(matchedDevice, "matched capture endpoint");
            LoggingService.Debug($"AudioRecorderService: Failed to enumerate capture endpoints for device #{deviceNumber}, using default endpoint - {ex.Message}");
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        if (matchedDevice != null)
            return matchedDevice;

        // No reliable name match (e.g. two mics share a truncated name); fall back
        // to the default comms endpoint rather than guessing.
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    private static void DisposeCaptureDevice(MMDevice? device, string context)
    {
        if (device == null)
            return;

        try
        {
            device.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"AudioRecorderService: Failed to dispose {context} - {ex.Message}");
        }
    }

    /// <summary>
    /// Boosts the selected capture device volume to 90% when the current level is low.
    /// Saves the original level so it can be restored later if HyperWhisper changed it.
    /// Non-fatal: failure is logged but does not prevent recording.
    /// </summary>
    /// <param name="deviceNumber">The NAudio WaveIn index of the device being recorded (-1 = system default).</param>
    public void BoostMicVolume(int deviceNumber)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _captureDevice = ResolveCaptureDevice(enumerator, deviceNumber);
            var currentVolume = _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar;

            if (currentVolume >= MicVolumeBoostThreshold)
            {
                LoggingService.Debug($"AudioRecorderService: Mic volume already healthy ({currentVolume:P0}); skipping boost to avoid Windows volume OSD (device: {_captureDevice.FriendlyName})");
                _captureDevice.Dispose();
                _captureDevice = null;
                _originalMicVolume = null;
                return;
            }

            if (Math.Abs(currentVolume - MicVolumeBoostTarget) <= MicVolumeTolerance)
            {
                LoggingService.Debug($"AudioRecorderService: Mic volume already near target ({currentVolume:P0}); skipping boost (device: {_captureDevice.FriendlyName})");
                _captureDevice.Dispose();
                _captureDevice = null;
                _originalMicVolume = null;
                return;
            }

            _originalMicVolume = currentVolume;
            LoggingService.Info($"AudioRecorderService: Boosting low mic volume from {_originalMicVolume:P0} to {MicVolumeBoostTarget:P0} (device: {_captureDevice.FriendlyName})");
            _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = MicVolumeBoostTarget;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioRecorderService: Failed to boost mic volume - {ex.Message}");
            _originalMicVolume = null;
            _captureDevice?.Dispose();
            _captureDevice = null;
        }
    }

    /// <summary>
    /// Restores the mic volume to the level saved by BoostMicVolume() if it still
    /// looks like HyperWhisper owns the temporary boost.
    /// Safe to call even if BoostMicVolume() was never called or failed.
    /// </summary>
    public void RestoreMicVolume()
    {
        try
        {
            if (_originalMicVolume.HasValue && _captureDevice != null)
            {
                var currentVolume = _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                if (Math.Abs(currentVolume - MicVolumeBoostTarget) > MicVolumeRestoreTolerance)
                {
                    LoggingService.Info($"AudioRecorderService: Skipping mic volume restore because the current level changed to {currentVolume:P0} during recording");
                    return;
                }

                LoggingService.Info($"AudioRecorderService: Restoring mic volume to {_originalMicVolume:P0}");
                _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = _originalMicVolume.Value;
            }
        }
        catch (Exception ex)
        {
            // Device may have been disconnected during recording
            LoggingService.Warn($"AudioRecorderService: Failed to restore mic volume - {ex.Message}");
        }
        finally
        {
            _captureDevice?.Dispose();
            _captureDevice = null;
            _originalMicVolume = null;
        }
    }

    /// <summary>
    /// Reads the current volume and name of the selected capture device.
    /// Returns null if the device cannot be accessed.
    /// </summary>
    /// <param name="deviceNumber">The NAudio WaveIn index of the selected device (-1 = system default).</param>
    public (float volume, string deviceName)? ReadMicVolume(int deviceNumber)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = ResolveCaptureDevice(enumerator, deviceNumber);
            return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.FriendlyName);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"AudioRecorderService: Failed to read mic volume - {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        CleanupRecording();
        _captureDevice?.Dispose();
        _captureDevice = null;
    }
}
