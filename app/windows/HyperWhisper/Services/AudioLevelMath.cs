namespace HyperWhisper.Services;

/// <summary>
/// The RMS -> dB -> envelope maths AudioRecorderService.OnDataAvailable runs,
/// shared by the onboarding level preview and the Settings "Test" meter, so
/// every meter in the app moves the same way for the same speech.
/// </summary>
public static class AudioLevelMath
{
    /// <summary>
    /// The next meter level (0 to 1) for one buffer of 16-bit mono PCM, given
    /// the previous level: fast attack, slow decay.
    /// </summary>
    public static float NextLevel(byte[] buffer, int bytesRecorded, float previous)
    {
        double sumSquares = 0;
        var sampleCount = 0;
        for (var i = 0; i + 1 < bytesRecorded && i + 1 < buffer.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            var norm = sample / 32768.0;
            sumSquares += norm * norm;
            sampleCount++;
        }

        var normalized = 0f;
        if (sampleCount > 0)
        {
            var rms = Math.Sqrt(sumSquares / sampleCount);
            var db = 20.0 * Math.Log10(Math.Max(rms, 1e-6));
            normalized = (float)Math.Clamp((db + 60.0) / 54.0, 0.0, 1.0);
        }

        return normalized > previous
            ? normalized
            : Math.Max(normalized, previous * 0.85f);
    }
}
