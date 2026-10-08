using System.IO;
using NAudio.Wave;

namespace HyperWhisper.Services;

/// <summary>
/// Opens an audio file for decoding the way NAudio's <see cref="AudioFileReader"/> does,
/// except that a WAVE_FORMAT_EXTENSIBLE WAV whose SubFormat is PCM or IEEE float is read
/// directly instead of through ACM.
/// </summary>
/// <remarks>
/// <para>
/// NAudio 2.2.1's <see cref="AudioFileReader"/> sends every .wav whose format tag is not
/// exactly Pcm or IeeeFloat through <c>WaveFormatConversionStream.CreatePcmStream</c>, which
/// asks ACM for a codec. WAVE_FORMAT_EXTENSIBLE (0xFFFE) is neither tag, and no ACM driver
/// takes it, so a perfectly ordinary file fails with "NoDriver calling acmFormatSuggest"
/// (#1450). ffmpeg writes every WAV with more than two channels that way, and so do most
/// multichannel recorders and DAWs; some write mono and stereo files that way too.
/// </para>
/// <para>
/// The samples of such a file are plain interleaved PCM or float: the SubFormat GUID says
/// which. This reader relabels the stream with the matching plain tag, so NAudio's own
/// converters decode it with no codec at all. A WAV that is really compressed (ADPCM,
/// mu-law, an Extensible file with any other SubFormat) keeps the old ACM path untouched,
/// and so does every other file type.
/// </para>
/// <para>
/// The samples keep the source channel count. Use <see cref="ToMono"/> to fold them:
/// NAudio's <c>ToMono()</c> handles exactly two channels and throws on more.
/// </para>
/// </remarks>
internal static class AudioFileDecoder
{
    // KSDATAFORMAT_SUBTYPE_PCM and KSDATAFORMAT_SUBTYPE_IEEE_FLOAT (ksmedia.h).
    internal static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// cbSize of a WAVEFORMATEXTENSIBLE: wValidBitsPerSample (2), dwChannelMask (4) and the
    /// SubFormat GUID (16).
    /// </summary>
    private const int ExtensibleExtraSize = 22;
    private const int SubFormatOffset = 6;

    /// <summary>
    /// Opens <paramref name="path"/> for decoding. Throws whatever
    /// <see cref="AudioFileReader"/> throws for a file neither reader can open.
    /// </summary>
    internal static DecodedAudioFile Open(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            WaveFileReader? wave = new WaveFileReader(path);
            try
            {
                var plain = TryGetPlainFormat(wave.WaveFormat);
                if (plain != null)
                {
                    var stream = new RelabelledWaveStream(wave, plain);
                    wave = null;
                    return new DecodedAudioFile(stream, stream.ToSampleProvider());
                }
            }
            finally
            {
                wave?.Dispose();
            }
        }

        var reader = new AudioFileReader(path);
        return new DecodedAudioFile(reader, reader);
    }

    /// <summary>
    /// Folds <paramref name="source"/> to mono by averaging every channel, or returns it
    /// unchanged when it is already mono.
    /// </summary>
    internal static ISampleProvider ToMono(ISampleProvider source) =>
        source.WaveFormat.Channels > 1 ? new MonoFoldSampleProvider(source) : source;

    /// <summary>
    /// The plain Pcm or IeeeFloat format that describes the same bytes as
    /// <paramref name="format"/>, or null when <paramref name="format"/> is not an
    /// Extensible PCM or float format NAudio's converters can read as is.
    /// </summary>
    internal static WaveFormat? TryGetPlainFormat(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
        {
            return null;
        }

        var subFormat = ReadSubFormat(format);
        if (subFormat == null)
        {
            return null;
        }

        WaveFormatEncoding encoding;
        if (subFormat.Value == PcmSubFormat)
        {
            encoding = WaveFormatEncoding.Pcm;
        }
        else if (subFormat.Value == IeeeFloatSubFormat)
        {
            encoding = WaveFormatEncoding.IeeeFloat;
        }
        else
        {
            return null;
        }

        var bits = format.BitsPerSample;
        var supported = encoding == WaveFormatEncoding.Pcm
            ? bits is 8 or 16 or 24 or 32
            : bits is 32 or 64;
        if (!supported || format.Channels < 1 || format.SampleRate < 1)
        {
            return null;
        }

        // wBitsPerSample is the container size, so a frame is channels * bits / 8 bytes.
        // A header that disagrees cannot be read sample by sample, so leave it alone.
        var blockAlign = format.Channels * (bits / 8);
        if (format.BlockAlign != blockAlign)
        {
            return null;
        }

        return WaveFormat.CreateCustomFormat(
            encoding,
            format.SampleRate,
            format.Channels,
            format.SampleRate * blockAlign,
            blockAlign,
            bits);
    }

    private static Guid? ReadSubFormat(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            return extensible.SubFormat;
        }

        // WaveFileReader parses the fmt chunk into a WaveFormatExtraData, whose extra
        // bytes are the WAVEFORMATEXTENSIBLE tail.
        if (format is WaveFormatExtraData extra &&
            format.ExtraSize >= ExtensibleExtraSize &&
            extra.ExtraData.Length >= ExtensibleExtraSize)
        {
            return new Guid(extra.ExtraData.AsSpan(SubFormatOffset, 16));
        }

        return null;
    }

    /// <summary>
    /// The same bytes as the source stream, described by a different (equivalent) format.
    /// Position, length and reads all go straight to the source, which keeps its own lock.
    /// </summary>
    private sealed class RelabelledWaveStream(WaveStream source, WaveFormat format) : WaveStream
    {
        public override WaveFormat WaveFormat => format;

        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            source.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// An opened audio file: <see cref="Stream"/> for length and positioning, and
/// <see cref="Samples"/> for float samples at the source rate and channel count. Both read
/// the same underlying data, so a seek on <see cref="Stream"/> moves <see cref="Samples"/>.
/// </summary>
internal sealed class DecodedAudioFile : IDisposable
{
    internal DecodedAudioFile(WaveStream stream, ISampleProvider samples)
    {
        Stream = stream;
        Samples = samples;
    }

    public WaveStream Stream { get; }

    public ISampleProvider Samples { get; }

    public TimeSpan TotalTime => Stream.TotalTime;

    /// <summary>Source channel count and sample rate, as float samples.</summary>
    public WaveFormat WaveFormat => Samples.WaveFormat;

    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// Folds any channel count down to mono by averaging across channels, for any channel
/// count and without throwing.
/// </summary>
/// <remarks>
/// NAudio 2.2.1's <c>ToMono()</c> and <see cref="NAudio.Wave.SampleProviders.StereoToMonoSampleProvider"/>
/// handle exactly two channels and throw <see cref="NotImplementedException"/> on anything
/// else. A plain average is the same 0.5/0.5 mix the 2-channel provider defaults to,
/// generalized to N channels. First written for the no-speech diagnostic (#291), then shared
/// with every path that folds a user's file to mono (#1450).
/// </remarks>
// internal (not private): test seam for HyperWhisper.SmokeTests via
// InternalsVisibleTo, which drives it with a source that returns awkward
// read counts - see the short-read test.
internal sealed class MonoFoldSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _sourceBuffer = [];

    /// <summary>
    /// Samples of an incomplete frame held over from the previous <see cref="Read"/>, at the
    /// start of <see cref="_sourceBuffer"/>. A source is free to return any count it likes,
    /// including one that ends mid-frame; dropping the remainder instead of carrying it
    /// would rotate every later frame across the channels by that many samples.
    /// </summary>
    private int _pending;

    internal MonoFoldSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var required = count * _channels;
        if (_sourceBuffer.Length < required)
        {
            // Resize, not reallocate: the carried partial frame lives at the start of it.
            Array.Resize(ref _sourceBuffer, required);
        }

        // Returning fewer than `count` frames is legal; returning 0 while the source still
        // has audio is not — both the analysis loop and WdlResamplingSampleProvider read a 0
        // as end-of-stream, so a source that returned 0 < read < _channels once would
        // silently truncate the measurement and still report AnalysisSucceeded: true. Keep
        // pulling until a whole frame exists or the source is genuinely exhausted.
        var available = _pending;
        while (available < _channels)
        {
            var read = _source.Read(_sourceBuffer, available, required - available);
            if (read <= 0)
            {
                break;
            }

            available += read;
        }

        var frames = available / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var start = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _sourceBuffer[start + channel];
            }

            buffer[offset + frame] = (float)(sum / _channels);
        }

        // Carry whatever did not make up a whole frame to the next call.
        _pending = available - (frames * _channels);
        if (_pending > 0)
        {
            Array.Copy(_sourceBuffer, frames * _channels, _sourceBuffer, 0, _pending);
        }

        return frames;
    }
}
