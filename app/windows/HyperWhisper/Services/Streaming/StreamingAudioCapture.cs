using System;
using System.Diagnostics;
using NAudio.Wave;

namespace HyperWhisper.Services.Streaming;

/// <summary>
/// Captures microphone audio as 16 kHz mono PCM chunks for realtime transcription.
/// </summary>
public sealed class StreamingAudioCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = new();
    private WaveInEvent? _waveIn;
    private float _displayedLevel;
    private int _captureChannelCount = 1;
    private bool _disposed;

    public bool IsCapturing { get; private set; }
    public TimeSpan Duration => _stopwatch.Elapsed;

    public event Action<byte[]>? AudioChunkAvailable;
    public event Action<float>? AudioLevelChanged;
    public event Action<Exception?>? CaptureStopped;

    public void Start(int deviceNumber, int sampleRate = 16000)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsCapturing)
                return;

            _displayedLevel = 0f;
            _waveIn = CreateWaveIn(deviceNumber, sampleRate);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            IsCapturing = true;
            _stopwatch.Restart();
        }

        try
        {
            LoggingService.Info($"StreamingAudioCapture: Starting capture on device #{deviceNumber} ({_captureChannelCount} channel(s))");
            _waveIn?.StartRecording();
        }
        catch (Exception ex) when (_captureChannelCount > 1)
        {
            var attemptedChannelCount = _captureChannelCount;
            LoggingService.Warn($"StreamingAudioCapture: failed to start {attemptedChannelCount}-channel capture - {ex.Message}; falling back to mono");
            Cleanup();
            StartMonoFallback(deviceNumber, sampleRate);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public void Stop()
    {
        WaveInEvent? waveIn;

        lock (_gate)
        {
            if (!IsCapturing)
                return;

            IsCapturing = false;
            _stopwatch.Stop();
            waveIn = _waveIn;
        }

        try
        {
            LoggingService.Info($"StreamingAudioCapture: Stopping capture (duration={_stopwatch.Elapsed.TotalSeconds:F2}s)");
            waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StreamingAudioCapture: Stop failed - {ex.Message}");
            Cleanup();
            CaptureStopped?.Invoke(ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsCapturing || e.BytesRecorded <= 0)
            return;

        var chunk = _captureChannelCount > 2
            ? MixToMono(e.Buffer, e.BytesRecorded, _captureChannelCount)
            : CopyChunk(e.Buffer, e.BytesRecorded);

        AudioChunkAvailable?.Invoke(chunk);
        AudioLevelChanged?.Invoke(UpdateAudioLevel(chunk));
    }

    private WaveInEvent CreateWaveIn(int deviceNumber, int sampleRate)
    {
        var requestedChannels = GetDeviceChannelCount(deviceNumber);
        var channelCount = requestedChannels > 2 ? requestedChannels : 1;

        try
        {
            return CreateWaveIn(deviceNumber, sampleRate, channelCount);
        }
        catch (Exception ex) when (channelCount > 1)
        {
            LoggingService.Warn($"StreamingAudioCapture: multi-channel open failed ({channelCount} channels) - {ex.Message}; falling back to mono");
            return CreateWaveIn(deviceNumber, sampleRate, 1);
        }
    }

    private WaveInEvent CreateWaveIn(int deviceNumber, int sampleRate, int channelCount)
    {
        _captureChannelCount = channelCount;
        return new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(sampleRate, 16, channelCount),
            BufferMilliseconds = 100
        };
    }

    private static int GetDeviceChannelCount(int deviceNumber)
    {
        try
        {
            var capabilities = WaveIn.GetCapabilities(deviceNumber);
            return Math.Max(1, capabilities.Channels);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"StreamingAudioCapture: failed to read device channel count - {ex.Message}; using mono");
            return 1;
        }
    }

    private void StartMonoFallback(int deviceNumber, int sampleRate)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _waveIn = CreateWaveIn(deviceNumber, sampleRate, 1);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            IsCapturing = true;
            _stopwatch.Restart();
        }

        try
        {
            LoggingService.Info($"StreamingAudioCapture: Starting mono fallback capture on device #{deviceNumber}");
            _waveIn?.StartRecording();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private static byte[] CopyChunk(byte[] buffer, int bytesRecorded)
    {
        var chunk = new byte[bytesRecorded];
        Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRecorded);
        return chunk;
    }

    private static byte[] MixToMono(byte[] buffer, int bytesRecorded, int channelCount)
    {
        var frameSize = channelCount * sizeof(short);
        var frameCount = bytesRecorded / frameSize;
        var output = new byte[frameCount * sizeof(short)];
        // Match macOS' RMS-preserving multi-channel mix so one active mic-array
        // channel stays usable while the final clamp still prevents overflow.
        var scale = 1.0 / Math.Sqrt(channelCount);

        for (int frame = 0; frame < frameCount; frame++)
        {
            double mixed = 0;
            var frameOffset = frame * frameSize;

            for (int channel = 0; channel < channelCount; channel++)
            {
                var sampleOffset = frameOffset + channel * sizeof(short);
                mixed += BitConverter.ToInt16(buffer, sampleOffset) * scale;
            }

            var sample = (short)Math.Clamp(
                Math.Round(mixed),
                short.MinValue,
                short.MaxValue);
            var outputOffset = frame * sizeof(short);
            output[outputOffset] = (byte)(sample & 0xFF);
            output[outputOffset + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return output;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var exception = e.Exception;
        Cleanup();
        CaptureStopped?.Invoke(exception);
    }

    private float UpdateAudioLevel(byte[] buffer)
    {
        double sumSquares = 0;
        int sampleCount = 0;

        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            double normalizedSample = sample / 32768.0;
            sumSquares += normalizedSample * normalizedSample;
            sampleCount++;
        }

        var normalized = 0f;
        if (sampleCount > 0)
        {
            double rms = Math.Sqrt(sumSquares / sampleCount);
            double db = 20.0 * Math.Log10(Math.Max(rms, 1e-6));
            normalized = (float)Math.Clamp((db + 60.0) / 54.0, 0.0, 1.0);
        }

        _displayedLevel = normalized > _displayedLevel
            ? normalized
            : Math.Max(normalized, _displayedLevel * 0.85f);

        return _displayedLevel;
    }

    private void Cleanup()
    {
        WaveInEvent? waveIn;

        lock (_gate)
        {
            IsCapturing = false;
            _displayedLevel = 0f;
            _captureChannelCount = 1;
            _stopwatch.Stop();
            waveIn = _waveIn;
            _waveIn = null;
        }

        if (waveIn == null)
            return;

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;
        waveIn.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        Cleanup();
    }
}
