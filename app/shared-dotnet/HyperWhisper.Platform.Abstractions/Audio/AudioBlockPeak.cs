namespace HyperWhisper.Platform.Abstractions.Audio;

/// <summary>
/// The PEAK level of one capture block in dBFS: <c>20·log10(max |sample| / full scale)</c>. A block
/// of exact zeros, or no samples at all, is <see cref="double.NegativeInfinity"/>.
/// </summary>
/// <remarks>
/// Read from the raw samples where a recorder reads its buffer, never from the meter level: the
/// meters are smoothed and perceptually scaled (Windows) or a mean (Linux), and neither is dBFS.
/// The live "mic hears nothing" warning (#2411) compares this value with
/// <c>MicSilenceWatcher.SilenceThresholdDbfs</c>.
/// </remarks>
public static class AudioBlockPeak
{
    /// <summary>The peak of little-endian signed 16-bit PCM, any channel count. A trailing odd byte is ignored.</summary>
    public static double Pcm16Dbfs(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            // Widen to int: Math.Abs(short.MinValue) throws.
            var sample = Math.Abs((int)(short)(pcm[index] | pcm[index + 1] << 8));
            if (sample > peak) peak = sample;
        }
        return ToDbfs(peak / 32768.0);
    }

    /// <summary>The peak of 32-bit float samples in [-1, 1]. Non-finite samples are skipped.</summary>
    public static double FloatDbfs(ReadOnlySpan<float> samples)
    {
        var peak = 0.0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) continue;
            var magnitude = Math.Abs((double)sample);
            if (magnitude > peak) peak = magnitude;
        }
        return ToDbfs(peak);
    }

    /// <summary>A linear amplitude relative to full scale, in dBFS.</summary>
    public static double ToDbfs(double linearPeak) =>
        linearPeak > 0 ? 20.0 * Math.Log10(linearPeak) : double.NegativeInfinity;
}

/// <summary>
/// A capture that reports each block's peak (<see cref="AudioBlockPeak"/>) as it reads it. Kept apart
/// from <see cref="IAudioRecorder"/> and <see cref="IStreamingAudioCapture"/> so a fake recorder that
/// has no samples does not have to implement it: a consumer tests for it with <c>is</c>.
/// </summary>
public interface IAudioBlockPeakSource
{
    /// <summary>Raised once per captured block, on the capture thread, with the block's peak in dBFS.</summary>
    event EventHandler<double>? BlockPeakDbfs;
}
