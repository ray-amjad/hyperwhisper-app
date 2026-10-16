// PROCESS LOOPBACK SOURCE
// One process-loopback capture client (a process and its children) plus the jitter buffer that
// feeds the muffler's mix. Open/Start/Dispose run on a control thread; Drain and MixInto run on the
// muffler's audio thread only and never allocate or lock.

using System.Runtime.InteropServices;
using HyperWhisper.Models;

namespace HyperWhisper.Services.Muffle;

internal sealed unsafe class ProcessLoopbackSource : IDisposable
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(3);
    private const long CaptureBufferHns = 2_000_000; // 200 ms
    private const double RingSeconds = 0.25;
    private const double PrimeSeconds = 0.010;
    private const double MaxBufferedSeconds = 0.060;
    // The capture applies its OWN session's volume to what it hears. With GUID_NULL the clients
    // joined HyperWhisper's default session, which the chimes leave at 0.5 (WaveOutEvent.Volume)
    // and the Volume Mixer slider for HyperWhisper moves, so everything muffled played 6 dB low.
    // A private session starts at 1.0 and no one else writes to it. (Process loopback refuses
    // AUDCLNT_STREAMFLAGS_NOPERSIST with AUDCLNT_E_INVALID_STREAM_FLAG, so Open forces it to 1.0.)
    private static readonly Guid CaptureSessionGuid = new("3f6b9e27-5c41-4d8a-b0e3-7a2d61c94f58");

    private readonly int _channels;
    private readonly float _gain;
    private readonly float _tripThreshold;
    private readonly float[] _ring;
    private readonly int _primeSamples;
    private readonly int _maxSamples;
    private IAudioClientRaw? _client;
    private IAudioCaptureClientRaw? _capture;
    private EventWaitHandle? _packetEvent;
    private int _ringRead;
    private int _ringCount;
    private bool _primed;
    private bool _disposed;

    private ProcessLoopbackSource(uint processId, int sampleRate, int channels, float gain, float tripThreshold)
    {
        ProcessId = processId;
        _channels = channels;
        _gain = gain;
        _tripThreshold = tripThreshold;
        _ring = new float[(int)(sampleRate * RingSeconds) * channels];
        _primeSamples = (int)(sampleRate * PrimeSeconds) * channels;
        _maxSamples = (int)(sampleRate * MaxBufferedSeconds) * channels;
    }

    public uint ProcessId { get; }

    // Audio-thread counters, read by the control thread only after the audio thread has stopped.
    public int Packets { get; private set; }
    public int SilentPackets { get; private set; }
    /// <summary>Loudest captured sample before the reciprocal gain.</summary>
    public float PeakInput { get; private set; }
    public int TrippedPackets { get; private set; }
    public int Underruns { get; private set; }
    public int DroppedSamples { get; private set; }
    public int FailureHr { get; private set; }
    public bool Failed { get; private set; }
    /// <summary>True once a scaled, non-silent packet has arrived: the muffle glide starts then.</summary>
    public bool HasSignal { get; private set; }

    /// <summary>
    /// Activates and initializes a capture of <paramref name="processId"/>'s process tree in the
    /// float format at <paramref name="format"/> (the muffler's render format). Not started yet.
    /// </summary>
    public static Result<ProcessLoopbackSource> Open(
        uint processId,
        IntPtr format,
        int sampleRate,
        int channels,
        float gain,
        float tripThreshold)
    {
        var activation = ProcessLoopbackNative.ActivateProcessLoopback(processId, ActivationTimeout);
        if (activation.IsFailure)
        {
            return Result<ProcessLoopbackSource>.Failure(activation.Error ?? "process loopback activation failed");
        }

        var source = new ProcessLoopbackSource(processId, sampleRate, channels, gain, tripThreshold)
        {
            _client = activation.Value,
        };

        var flags = ProcessLoopbackNative.AUDCLNT_STREAMFLAGS_LOOPBACK
            | ProcessLoopbackNative.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
            | ProcessLoopbackNative.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            | ProcessLoopbackNative.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        var sessionGuid = CaptureSessionGuid;
        var hr = source._client!.Initialize(ProcessLoopbackNative.AUDCLNT_SHAREMODE_SHARED, flags, CaptureBufferHns, 0, format, (IntPtr)(&sessionGuid));
        if (hr < 0)
        {
            source.Dispose();
            return Result<ProcessLoopbackSource>.Failure($"process loopback Initialize failed (hr=0x{hr:X8})");
        }

        // Event mode is what the platform sample uses; the audio thread polls the packets on its
        // own render cadence, so nobody waits on this event, but Start refuses without one.
        source._packetEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        hr = source._client.SetEventHandle(source._packetEvent.SafeWaitHandle.DangerousGetHandle());
        if (hr < 0)
        {
            source.Dispose();
            return Result<ProcessLoopbackSource>.Failure($"process loopback SetEventHandle failed (hr=0x{hr:X8})");
        }

        var iid = ProcessLoopbackNative.IID_IAudioCaptureClient;
        hr = source._client.GetService(ref iid, out var service);
        if (hr < 0 || service is not IAudioCaptureClientRaw capture)
        {
            source.Dispose();
            return Result<ProcessLoopbackSource>.Failure($"process loopback GetService(IAudioCaptureClient) failed (hr=0x{hr:X8})");
        }

        source._capture = capture;
        source.SessionVolume = EnsureUnitySessionVolume(source._client);
        return Result<ProcessLoopbackSource>.Success(source);
    }

    /// <summary>The capture session's volume as found at open (it is then forced to 1); NaN if unreadable.</summary>
    public float SessionVolume { get; private set; } = float.NaN;

    // The private session should start at 1.0; if anything left it lower, the captured audio would
    // be scaled by it, so it is put to 1.0. Returns the volume that was found.
    private static float EnsureUnitySessionVolume(IAudioClientRaw client)
    {
        var iid = ProcessLoopbackNative.IID_ISimpleAudioVolume;
        object? service = null;
        try
        {
            if (client.GetService(ref iid, out service) < 0 || service is not ISimpleAudioVolumeRaw volume)
            {
                return float.NaN;
            }

            if (volume.GetMasterVolume(out var level) < 0)
            {
                return float.NaN;
            }

            if (Math.Abs(level - 1f) > 1e-6f)
            {
                var context = Guid.Empty;
                volume.SetMasterVolume(1f, ref context);
            }

            return level;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ProcessLoopbackSource: Could not read the capture session volume: {ex.Message}");
            return float.NaN;
        }
        finally
        {
            if (service != null && Marshal.IsComObject(service))
            {
                Marshal.ReleaseComObject(service);
            }
        }
    }

    public Result Start()
    {
        if (_client == null)
        {
            return Result.Failure("process loopback client is closed");
        }

        var hr = _client.Start();
        return hr < 0
            ? Result.Failure($"process loopback Start failed (hr=0x{hr:X8})")
            : Result.Success();
    }

    /// <summary>Audio thread: moves every captured packet into the jitter buffer.</summary>
    public void Drain()
    {
        var capture = _capture;
        if (Failed || capture == null)
        {
            return;
        }

        while (true)
        {
            var hr = capture.GetNextPacketSize(out var nextFrames);
            if (hr < 0)
            {
                Fail(hr);
                return;
            }

            if (nextFrames == 0)
            {
                return;
            }

            hr = capture.GetBuffer(out var data, out var frames, out var bufferFlags, out _, out _);
            if (hr == ProcessLoopbackNative.AUDCLNT_S_BUFFER_EMPTY)
            {
                return;
            }

            if (hr < 0)
            {
                Fail(hr);
                return;
            }

            var samples = (int)frames * _channels;
            var silent = (bufferFlags & ProcessLoopbackNative.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero;
            if (silent) SilentPackets++;
            Write(silent ? ReadOnlySpan<float>.Empty : new ReadOnlySpan<float>((void*)data, samples), samples);
            capture.ReleaseBuffer(frames);
            Packets++;
        }
    }

    /// <summary>
    /// Audio thread: adds up to <paramref name="output"/>.Length samples, times the reciprocal
    /// gain, into <paramref name="output"/>.
    /// </summary>
    public void MixInto(Span<float> output)
    {
        if (!_primed)
        {
            if (_ringCount < _primeSamples)
            {
                return;
            }

            _primed = true;
        }

        var count = Math.Min(output.Length, _ringCount);
        var gain = _gain;
        var ring = _ring;
        var read = _ringRead;
        for (var i = 0; i < count; i++)
        {
            output[i] += ring[read] * gain;
            if (++read == ring.Length) read = 0;
        }

        _ringRead = read;
        _ringCount -= count;

        if (count < output.Length)
        {
            Underruns++;
            _primed = false;
        }

        // The capture and the render run on the same engine clock, so the buffer only grows after
        // a hiccup; drop back to the priming depth so latency stays bounded.
        if (_ringCount > _maxSamples)
        {
            Skip(_ringCount - _primeSamples);
        }
    }

    private void Write(ReadOnlySpan<float> packet, int samples)
    {
        // Anything louder than full scale times the volume factor did not go through the scaled
        // session (a packet from before the volume change, or a child process playing on another
        // endpoint). Times the reciprocal gain it would be a +80 dB burst, so it becomes silence.
        var peak = 0f;
        for (var i = 0; i < packet.Length; i++)
        {
            var magnitude = Math.Abs(packet[i]);
            if (magnitude > peak) peak = magnitude;
        }

        if (peak > PeakInput) PeakInput = peak;
        var tripped = peak > _tripThreshold;

        if (tripped)
        {
            TrippedPackets++;
            packet = ReadOnlySpan<float>.Empty;
        }
        else if (peak > 0)
        {
            HasSignal = true;
        }

        var ring = _ring;
        if (samples > ring.Length)
        {
            if (!packet.IsEmpty) packet = packet[(samples - ring.Length)..];
            DroppedSamples += samples - ring.Length;
            samples = ring.Length;
        }

        var overflow = _ringCount + samples - ring.Length;
        if (overflow > 0)
        {
            Skip(overflow);
        }

        var write = _ringRead + _ringCount;
        if (write >= ring.Length) write -= ring.Length;
        for (var i = 0; i < samples; i++)
        {
            ring[write] = packet.IsEmpty ? 0f : packet[i];
            if (++write == ring.Length) write = 0;
        }

        _ringCount += samples;
    }

    private void Skip(int samples)
    {
        samples -= samples % _channels;
        if (samples <= 0) return;
        _ringRead = (_ringRead + samples) % _ring.Length;
        _ringCount -= samples;
        DroppedSamples += samples;
    }

    private void Fail(int hr)
    {
        Failed = true;
        FailureHr = hr;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _client?.Stop();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ProcessLoopbackSource: Stop failed for pid {ProcessId}: {ex.Message}");
        }

        ReleaseCom(ref _capture);
        ReleaseCom(ref _client);
        SafeDispose(ref _packetEvent);
    }

    private void ReleaseCom<T>(ref T? comObject) where T : class
    {
        var temp = comObject;
        comObject = null;

        try
        {
            if (temp != null && Marshal.IsComObject(temp))
            {
                Marshal.ReleaseComObject(temp);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ProcessLoopbackSource: Release failed for {typeof(T).Name} (pid {ProcessId}): {ex.Message}");
        }
    }

    private void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ProcessLoopbackSource: Dispose failed for {typeof(T).Name} (pid {ProcessId}): {ex.Message}");
        }
    }
}
