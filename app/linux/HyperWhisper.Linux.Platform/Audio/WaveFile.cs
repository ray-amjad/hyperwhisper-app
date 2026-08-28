using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Platform.Abstractions.Audio;

namespace HyperWhisper.Linux.Platform.Audio;

/// <summary>
/// Adapts the shared canonical PCM WAV header (<see cref="PcmWaveHeader"/>) to the Linux audio
/// stack's own <see cref="WaveFormat"/> record and <c>audio_format_unsupported</c> error code.
/// </summary>
internal static class WaveFile
{
    public const int HeaderSize = PcmWaveHeader.HeaderSize;

    public static void WriteHeader(Stream stream, WaveFormat format, long dataLength)
        => PcmWaveHeader.Write(stream, format.SampleRate, format.Channels, format.BitsPerSample, dataLength);

    /// <summary>
    /// Reads the header. The returned data length is recomputed from the stream, so a recording
    /// whose header was never patched (a crash between start and stop) reports the bytes it
    /// actually holds instead of the zero it declares.
    /// </summary>
    public static PlatformResult<(WaveFormat Format, long DataOffset, long DataLength)> ReadHeader(Stream stream)
    {
        var status = PcmWaveHeader.TryRead(stream, out var header);
        switch (status)
        {
            case PcmWaveHeaderStatus.Valid:
                break;
            case PcmWaveHeaderStatus.UnsupportedFormat:
                return Failure("Only 16-bit PCM WAV files are supported.");
            case PcmWaveHeaderStatus.NoCompleteSamples:
                return Failure("The WAV file contains no complete audio samples.");
            case PcmWaveHeaderStatus.TooLarge:
                return Failure("The WAV file is too large to play back.");
            default:
                return Failure("Only canonical PCM WAV files are supported.");
        }

        var format = new WaveFormat(header!.SampleRate, header.BitsPerSample, header.Channels);
        return PlatformResult<(WaveFormat, long, long)>.Success((format, HeaderSize, header.DataLength));

        static PlatformResult<(WaveFormat, long, long)> Failure(string message)
            => PlatformResult<(WaveFormat, long, long)>.Failure("audio_format_unsupported", message);
    }
}
