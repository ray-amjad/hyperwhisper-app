using System.Buffers.Binary;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Audio;

internal static class WaveFile
{
    public const int HeaderSize = 44;

    public static void WriteHeader(Stream stream, WaveFormat format, long dataLength)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + dataLength)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], checked((ushort)format.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], checked((uint)format.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], checked((uint)format.BytesPerSecond));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], checked((ushort)format.BlockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], checked((ushort)format.BitsPerSample));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)dataLength));
        stream.Position = 0;
        stream.Write(header);
    }

    public static PlatformResult<(WaveFormat Format, long DataOffset, long DataLength)> ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.Read(header) != HeaderSize
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header[8..12].SequenceEqual("WAVE"u8)
            || !header[12..16].SequenceEqual("fmt "u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(header[20..]) != 1
            || !header[36..40].SequenceEqual("data"u8))
        {
            return PlatformResult<(WaveFormat, long, long)>.Failure("audio_format_unsupported", "Only canonical PCM WAV files are supported.");
        }

        var format = new WaveFormat(
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..])),
            checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[34..])),
            checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[22..])));
        if (format.BitsPerSample != 16 || format.Channels <= 0 || format.SampleRate <= 0)
        {
            return PlatformResult<(WaveFormat, long, long)>.Failure("audio_format_unsupported", "Only 16-bit PCM WAV files are supported.");
        }

        return PlatformResult<(WaveFormat, long, long)>.Success((format, HeaderSize, BinaryPrimitives.ReadUInt32LittleEndian(header[40..])));
    }
}
