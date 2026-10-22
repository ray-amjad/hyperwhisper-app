using System.Buffers.Binary;

namespace HyperWhisper.WhisperWorker;

/// <summary>
/// Writes the 16 kHz mono 16-bit PCM WAV the app hands to the worker.
///
/// The samples the app prepares are already quantized to 16 bits: each is
/// <c>k / 32768f</c> for a short <c>k</c> (TranscriptionService.ToWhisperInput).
/// Whisper.net's WaveParser reads a 16-bit sample back as <c>k / 32768f</c>, so
/// writing <c>k</c> here gives the worker the exact floats the in-process
/// decoder used to get.
/// </summary>
public static class WhisperWav
{
    public const int SampleRate = 16000;
    private const int HeaderBytes = 44;

    /// <summary>The 16-bit value Whisper.net turns back into <paramref name="sample"/>.</summary>
    public static short ToPcm16(float sample)
    {
        var scaled = MathF.Round(Math.Clamp(sample, -1f, 1f) * 32768f);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    public static void Write(string path, ReadOnlySpan<float> samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        Write(stream, samples);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples)
    {
        long dataBytes = (long)samples.Length * 2;
        if (dataBytes > uint.MaxValue - HeaderBytes)
        {
            throw new ArgumentException("The audio is too long for one WAV file.", nameof(samples));
        }

        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);           // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);            // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);            // mono
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], SampleRate * 2); // byte rate
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);            // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);           // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        stream.Write(header);

        var buffer = new byte[Math.Min(samples.Length, 8192) * 2];
        int index = 0;
        while (index < samples.Length)
        {
            int count = Math.Min(samples.Length - index, buffer.Length / 2);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), ToPcm16(samples[index + i]));
            }
            stream.Write(buffer, 0, count * 2);
            index += count;
        }
    }
}
