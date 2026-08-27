using System.Buffers.Binary;

namespace HyperWhisper.Platform.Abstractions.Audio;

/// <summary>Why a canonical 44-byte PCM WAV header could not be read.</summary>
/// <remarks>
/// The reader reports a status rather than an error code, because each caller owns a
/// different, stable error-code namespace (<c>audio_format_unsupported</c> on the Linux
/// audio stack, <c>audio_recovery.*</c> in crash recovery) and those codes are branched on.
/// </remarks>
public enum PcmWaveHeaderStatus
{
    /// <summary>The header parsed and carries at least one complete sample frame.</summary>
    Valid,

    /// <summary>The stream is shorter than the 44-byte canonical header, or is not seekable.</summary>
    TruncatedHeader,

    /// <summary>A magic marker or the format tag is not the canonical 44-byte PCM layout.</summary>
    NotCanonicalPcm,

    /// <summary>The channel count, sample rate or sample width is outside the supported range.</summary>
    UnsupportedFormat,

    /// <summary>The stream carries no complete sample frame after the header.</summary>
    NoCompleteSamples,

    /// <summary>The payload cannot be described by the 32-bit RIFF length fields.</summary>
    TooLarge,
}

/// <summary>
/// A canonical 44-byte PCM WAV header, as read from a stream. <see cref="DataLength"/> is the
/// declared length when that is usable and a value <em>recomputed</em> from the stream length
/// when it is not — see <see cref="PcmWaveHeader.TryRead"/>.
/// </summary>
public sealed record PcmWaveHeaderInfo(
    int SampleRate,
    short Channels,
    short BitsPerSample,
    long DataLength,
    uint DeclaredDataLength,
    uint DeclaredRiffSize)
{
    /// <summary>Bytes per sample frame across every channel.</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    /// <summary>Whether both declared length fields already match the recomputed payload.</summary>
    public bool DeclaredLengthsAgree
        => DeclaredDataLength == DataLength && DeclaredRiffSize == 36 + DataLength;
}

/// <summary>
/// The one in-process builder and reader for the canonical 44-byte PCM WAV header
/// (<c>RIFF</c> / <c>WAVE</c> / <c>fmt </c> of 16 bytes / <c>data</c>, no extra chunks).
/// </summary>
/// <remarks>
/// The read path does not take the declared <c>data</c> length on faith. A recorder writes a
/// placeholder header before it has any audio and patches the lengths only when it stops
/// cleanly, so a recording interrupted by a crash declares a length of zero while the file
/// holds every captured byte. When the declared length is zero, or reaches past the end of the
/// file, the payload is recomputed from the stream length — which makes every reader agree with
/// crash recovery instead of reporting an empty recording. A declared length that fits inside
/// the file is taken verbatim, so bytes that belong to a trailing chunk are never played or
/// measured as audio.
/// </remarks>
public static class PcmWaveHeader
{
    /// <summary>Size in bytes of the canonical header.</summary>
    public const int HeaderSize = 44;

    /// <summary>
    /// Writes the canonical header at the start of <paramref name="stream"/>, leaving the
    /// stream positioned immediately after it.
    /// </summary>
    /// <exception cref="OverflowException">
    /// A field does not fit its RIFF slot. Callers that finalize a recording rely on this.
    /// </exception>
    public static void Write(Stream stream, int sampleRate, int channels, int bitsPerSample, long dataLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var blockAlign = channels * bitsPerSample / 8;
        var bytesPerSecond = (long)sampleRate * blockAlign;
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + dataLength)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)bytesPerSecond));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], checked((ushort)bitsPerSample));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataLength));
        stream.Position = 0;
        stream.Write(header);
    }

    /// <summary>
    /// Reads the canonical header from the start of <paramref name="stream"/> and resolves the
    /// payload length: the declared <c>data</c> length when it is non-zero and fits inside
    /// <c>stream.Length - 44</c>, otherwise <c>stream.Length - 44</c> itself. Either way it is
    /// aligned <em>down</em> to a whole sample frame. The declared fields are returned unchanged
    /// so a caller that repairs files can tell whether the header needs rewriting.
    /// </summary>
    /// <returns><see cref="PcmWaveHeaderStatus.Valid"/> when <paramref name="info"/> is set.</returns>
    public static PcmWaveHeaderStatus TryRead(Stream stream, out PcmWaveHeaderInfo? info)
    {
        ArgumentNullException.ThrowIfNull(stream);
        info = null;
        if (!stream.CanSeek || stream.Length < HeaderSize)
        {
            return PcmWaveHeaderStatus.TruncatedHeader;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8)
            || !header[8..12].SequenceEqual("WAVE"u8)
            || !header[12..16].SequenceEqual("fmt "u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) != 1
            || !header[36..40].SequenceEqual("data"u8))
        {
            return PcmWaveHeaderStatus.NotCanonicalPcm;
        }

        // Range-check every field before narrowing. A checked cast here would surface a hostile
        // file as an OverflowException on paths that only expect I/O failures.
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
        if (channels is 0 or > (ushort)short.MaxValue || sampleRate is 0 or > int.MaxValue || bitsPerSample != 16)
        {
            return PcmWaveHeaderStatus.UnsupportedFormat;
        }

        var blockAlign = channels * (bitsPerSample / 8);
        var available = stream.Length - HeaderSize;
        var declaredDataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);

        // Trust the declared length when it is usable, and recompute only when it is not.
        //
        // Recomputing is the point of this reader: a recorder writes a placeholder header
        // with a zero length before it has any audio and patches it only on a clean stop,
        // so a crashed recording declares 0 while the file holds every captured byte.
        // Recomputing unconditionally, though, over-reads in the opposite direction — a
        // valid file whose `data` chunk is followed by a `LIST`, `JUNK` or any other RIFF
        // chunk would report that trailing chunk as audio, and a playback path would count
        // it in the duration and emit it as noise. So:
        //   * declared 0 (or absent)      -> recompute; this is the crash case.
        //   * declared beyond the file    -> clamp to what is there; truncated file.
        //   * declared within the file    -> take it verbatim; anything after it is
        //                                    another chunk, not audio.
        var usable = declaredDataLength > 0 && declaredDataLength <= available
            ? declaredDataLength
            : available;
        // Align DOWN either way: a declared length that is not a whole number of frames is
        // as unusable as a recomputed one.
        var actual = usable - usable % blockAlign;
        if (actual <= 0)
        {
            return PcmWaveHeaderStatus.NoCompleteSamples;
        }

        if (actual > uint.MaxValue - 36)
        {
            return PcmWaveHeaderStatus.TooLarge;
        }

        info = new PcmWaveHeaderInfo(
            (int)sampleRate,
            (short)channels,
            (short)bitsPerSample,
            actual,
            declaredDataLength,
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
        return PcmWaveHeaderStatus.Valid;
    }
}
