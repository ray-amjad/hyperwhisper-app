namespace HyperWhisper.Platform.Abstractions.Audio;

/// <summary>
/// The real-time half of "Muffle Audio": a 4-pole low-pass (two cascaded RBJ biquads, transposed
/// direct form II, Q = 1/sqrt 2) whose cutoff glides in the log domain between fully open and
/// <see cref="MuffledCutoffHz"/>, so music keeps playing as if from the next room. The constants
/// match the macOS <c>MuffleDSP</c> in AudioMuffler.swift.
/// </summary>
/// <remarks>
/// <see cref="Process(Span{float})"/> runs on one audio thread and never allocates or locks. The
/// only value shared with other threads is the target cutoff, written through
/// <see cref="Volatile"/>. The glide steps every <see cref="ControlFrames"/> frames whatever the
/// caller's block size, so a buffer gives the same output split into blocks of any length.
/// </remarks>
public sealed class MuffleFilter
{
    /// <summary>Cutoff while recording: speech-band energy from the speakers mostly disappears, the beat stays.</summary>
    public const double MuffledCutoffHz = 400;
    /// <summary>Cutoff when open, clamped to 45% of the sample rate.</summary>
    public const double OpenCutoffHz = 20_000;
    /// <summary>Time constant of the cutoff glide, in seconds. About three of them to settle.</summary>
    public const double GlideTimeConstantSeconds = 0.06;
    /// <summary>How long a stop waits for the glide back up before tearing down.</summary>
    public static readonly TimeSpan TeardownDelay = TimeSpan.FromSeconds(0.25);
    public const int MaxChannels = 16;
    /// <summary>Frames per glide step. The coefficients are recomputed at most once per step.</summary>
    public const int ControlFrames = 32;
    private const int Stages = 2;
    private const double Q = 0.7071067811865476;
    private const double SettledLogDistance = 0.01;

    private readonly float[] _state;
    private readonly double _alpha;
    private double _targetCutoff;
    private double _currentCutoff;
    private double _coefficientCutoff = double.NaN;
    private int _phase;
    private float _b0, _b1, _b2, _a1, _a2;

    public MuffleFilter(int sampleRate, int channels)
    {
        if (sampleRate < 8_000 || sampleRate > 768_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > MaxChannels) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate; Channels = channels;
        MaxCutoffHz = Math.Min(OpenCutoffHz, sampleRate * 0.45);
        _currentCutoff = _targetCutoff = MaxCutoffHz;
        _state = new float[channels * Stages * 2];
        _alpha = 1 - Math.Exp(-ControlFrames / (GlideTimeConstantSeconds * sampleRate));
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public double MaxCutoffHz { get; }
    public double TargetCutoffHz => Volatile.Read(ref _targetCutoff);
    public double CurrentCutoffHz => Volatile.Read(ref _currentCutoff);
    /// <summary>True once a release has glided back to (within 1% of) the open cutoff.</summary>
    public bool IsOpen => TargetCutoffHz >= MaxCutoffHz && Math.Log(MaxCutoffHz / CurrentCutoffHz) < SettledLogDistance;
    /// <summary>True once the cutoff has reached (within 1% of) the target, whichever way it glided.</summary>
    public bool IsSettled => Math.Abs(Math.Log(TargetCutoffHz / CurrentCutoffHz)) < SettledLogDistance;

    /// <summary>Thread-safe. Clamped to [20 Hz, <see cref="MaxCutoffHz"/>].</summary>
    public void SetTargetCutoff(double hz) => Volatile.Write(ref _targetCutoff, double.IsNaN(hz) ? MaxCutoffHz : Math.Clamp(hz, 20, MaxCutoffHz));
    /// <summary>Glide down to <see cref="MuffledCutoffHz"/>.</summary>
    public void Engage() => SetTargetCutoff(MuffledCutoffHz);
    /// <summary>Glide back open. Poll <see cref="IsOpen"/>, or wait <see cref="TeardownDelay"/>.</summary>
    public void Release() => SetTargetCutoff(OpenCutoffHz);

    /// <summary>Audio thread only: forgets the filter memory and jumps the cutoff to the target.</summary>
    public void Reset()
    {
        Array.Clear(_state); _phase = 0; _coefficientCutoff = double.NaN;
        Volatile.Write(ref _currentCutoff, Volatile.Read(ref _targetCutoff));
    }

    /// <summary>Filters interleaved samples in place. The length must be whole frames.</summary>
    public void Process(Span<float> interleaved) => Process(interleaved, interleaved);

    /// <summary>Filters interleaved <paramref name="input"/> into <paramref name="output"/>; the two may be the same buffer.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var channels = Channels;
        if (input.Length != output.Length) throw new ArgumentException("Input and output must be the same length.", nameof(output));
        if (input.Length % channels != 0) throw new ArgumentException("The buffer must hold whole frames.", nameof(input));
        var frames = input.Length / channels;
        var offset = 0;
        while (offset < frames)
        {
            if (_phase == 0) Glide();
            var count = Math.Min(ControlFrames - _phase, frames - offset);
            var b0 = _b0; var b1 = _b1; var b2 = _b2; var a1 = _a1; var a2 = _a2;
            for (var c = 0; c < channels; c++)
            {
                var s = c * Stages * 2;
                float z1a = _state[s], z2a = _state[s + 1], z1b = _state[s + 2], z2b = _state[s + 3];
                var index = offset * channels + c;
                for (var f = 0; f < count; f++, index += channels)
                {
                    var x = input[index];
                    var y1 = b0 * x + z1a; z1a = b1 * x - a1 * y1 + z2a; z2a = b2 * x - a2 * y1;
                    var y2 = b0 * y1 + z1b; z1b = b1 * y1 - a1 * y2 + z2b; z2b = b2 * y1 - a2 * y2;
                    output[index] = y2;
                }
                // Flush denormals so a silent tail does not burn CPU.
                _state[s] = Math.Abs(z1a) < 1e-20f ? 0 : z1a; _state[s + 1] = Math.Abs(z2a) < 1e-20f ? 0 : z2a;
                _state[s + 2] = Math.Abs(z1b) < 1e-20f ? 0 : z1b; _state[s + 3] = Math.Abs(z2b) < 1e-20f ? 0 : z2b;
            }
            offset += count; _phase = (_phase + count) % ControlFrames;
        }
    }

    // One glide step, in the log domain: alpha = 1 - exp(-frames / (tau * sr)), as on macOS.
    private void Glide()
    {
        var target = Volatile.Read(ref _targetCutoff); var current = _currentCutoff;
        if (current != target)
        {
            current = Math.Exp(Math.Log(current) + (Math.Log(target) - Math.Log(current)) * _alpha);
            if (Math.Abs(Math.Log(target / current)) < 1e-6) current = target;
            Volatile.Write(ref _currentCutoff, current);
        }
        if (current == _coefficientCutoff) return;
        _coefficientCutoff = current;
        var w0 = 2 * Math.PI * current / SampleRate; var cosW0 = Math.Cos(w0); var a = Math.Sin(w0) / (2 * Q); var a0 = 1 + a;
        _b0 = (float)((1 - cosW0) / 2 / a0); _b1 = (float)((1 - cosW0) / a0); _b2 = _b0;
        _a1 = (float)(-2 * cosW0 / a0); _a2 = (float)((1 - a) / a0);
    }
}

/// <summary>Allocation-free conversions between 16-bit little-endian PCM and float samples in [-1, 1).</summary>
public static class PcmFloatConversion
{
    public static void S16ToFloat(ReadOnlySpan<short> source, Span<float> destination)
    {
        if (destination.Length < source.Length) throw new ArgumentException("The destination is too short.", nameof(destination));
        for (var i = 0; i < source.Length; i++) destination[i] = source[i] * (1f / 32768f);
    }

    /// <summary>Rounds to nearest and clips to the 16-bit range.</summary>
    public static void FloatToS16(ReadOnlySpan<float> source, Span<short> destination)
    {
        if (destination.Length < source.Length) throw new ArgumentException("The destination is too short.", nameof(destination));
        for (var i = 0; i < source.Length; i++)
        {
            var scaled = source[i] * 32768f;
            destination[i] = float.IsNaN(scaled) ? (short)0 : (short)Math.Clamp(MathF.Round(scaled), short.MinValue, short.MaxValue);
        }
    }
}
