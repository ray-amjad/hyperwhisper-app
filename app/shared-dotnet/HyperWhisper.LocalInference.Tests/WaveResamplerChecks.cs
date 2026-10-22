using System.Text;
using HyperWhisper.LocalInference;
using Whisper.net.Wave;

/// <summary>
/// Issue #1007: Whisper.net reads only a 16 kHz WAV, so the service resamples
/// a PCM WAV at any other rate itself. These checks need no model.
/// </summary>
static class WaveResamplerChecks
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("a 22,050 Hz WAV becomes 16 kHz mono with its tone and level (issue #1007)", () =>
        {
            var samples = WaveResampler.TryReadAs16kMono(Wav(22_050, 1, 16, Tone(22_050, 440, 0.5, 1.0)));
            Check(samples is not null, "a 22,050 Hz WAV was left to Whisper.net, which refuses it");
            Check(Math.Abs(samples!.Length - 16_000) <= 1, $"1 s became {samples.Length} samples, expected 16,000");
            CheckTone(samples, 440, 0.5);
        });
        yield return ("8, 24 and 32-bit, stereo and 8 kHz WAVs are resampled too", () =>
        {
            foreach (var (rate, channels, bits) in new[] { (8_000, 1, 16), (44_100, 2, 24), (48_000, 1, 32), (22_050, 2, 8) })
            {
                var samples = WaveResampler.TryReadAs16kMono(Wav(rate, channels, bits, Tone(rate, 440, 0.5, 1.0)));
                Check(samples is not null, $"{rate} Hz {bits}-bit x{channels} was not resampled");
                CheckTone(samples!, 440, 0.5, $"{rate} Hz {bits}-bit x{channels}: ");
            }
        });
        yield return ("a WAVE_FORMAT_EXTENSIBLE PCM WAV at 22,050 Hz is resampled", () =>
        {
            var samples = WaveResampler.TryReadAs16kMono(Wav(22_050, 1, 16, Tone(22_050, 440, 0.5, 0.5), extensible: true));
            Check(samples is not null && Math.Abs(samples.Length - 8_000) <= 1, "the extensible WAV was not resampled");
            CheckTone(samples!, 440, 0.5);
        });
        yield return ("going down filters what would alias into the speech band", () =>
        {
            // 12 kHz at 48 kHz has no place at 16 kHz; unfiltered it folds to 4 kHz at full level.
            var samples = WaveResampler.TryReadAs16kMono(Wav(48_000, 1, 16, Tone(48_000, 12_000, 0.5, 1.0)))!;
            var rms = Rms(samples.AsSpan(800, samples.Length - 1_600));
            Check(rms < 0.01, $"a 12 kHz tone came through at RMS {rms:F4}");
        });
        yield return ("Whisper.net still reads a 16 kHz WAV, and anything it refuses at every rate", () =>
        {
            Check(WaveResampler.TryReadAs16kMono(Wav(16_000, 1, 16, Tone(16_000, 440, 0.5, 0.1))) is null,
                "a 16 kHz WAV no longer goes to Whisper.net as before");
            Check(WaveResampler.TryReadAs16kMono(Wav(22_050, 1, 32, Tone(22_050, 440, 0.5, 0.1), format: 3)) is null,
                "a float WAV was decoded here; Whisper.net refuses it at 16 kHz too");
            Check(WaveResampler.TryReadAs16kMono(new MemoryStream(Encoding.ASCII.GetBytes("ID3 not a wave file at all"))) is null,
                "a non-WAV file was decoded");
            Check(WaveResampler.TryReadAs16kMono(new MemoryStream(Encoding.ASCII.GetBytes("RIFF"))) is null,
                "a cut-off header was decoded");
        });
        yield return ("a WAV that holds less than its header says ends where its audio ends", () =>
        {
            var full = Wav(22_050, 1, 16, Tone(22_050, 440, 0.5, 1.0)).ToArray();
            var samples = WaveResampler.TryReadAs16kMono(new MemoryStream(full[..(56 + 22_050)]))!;
            Check(Math.Abs(samples.Length - 8_000) <= 1, $"half a second became {samples.Length} samples");
            CheckTone(samples, 440, 0.5);
        });
        yield return ("reading in blocks gives the same samples as one pass", () =>
        {
            var random = new Random(1007);
            var source = Enumerable.Range(0, 70_001).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
            var whole = WaveResampler.Resample(source, 44_100);
            var blocks = WaveResampler.TryReadAs16kMono(Wav(44_100, 1, 32, source))!;
            Check(whole.Length == blocks.Length, $"{whole.Length} samples in one pass, {blocks.Length} in blocks");
            var worst = whole.Zip(blocks, (a, b) => Math.Abs(a - b)).Max();
            Check(worst < 1e-6, $"block boundaries moved a sample by {worst}");
        });
    }

    /// <summary>
    /// Writes a 16-bit WAV at <paramref name="rate"/> from a PCM WAV, by linear
    /// interpolation: the harness's real-model check of a 22,050 Hz upload.
    /// </summary>
    public static string WriteUpsampled(string wav, int rate)
    {
        using var input = File.OpenRead(wav);
        var at16k = WaveResampler.TryReadAs16kMono(input);
        if (at16k is null)
        {
            input.Position = 0;
            at16k = new WaveParser(input).GetAvgSamples();
        }
        var length = (int)(at16k.Length * (double)rate / 16_000);
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * 16_000.0 / rate;
            var index = (int)position;
            var next = Math.Min(index + 1, at16k.Length - 1);
            output[i] = (float)(at16k[index] + (at16k[next] - at16k[index]) * (position - index));
        }
        var path = Path.Combine(Path.GetTempPath(), $"hw-1007-{rate}-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, Wav(rate, 1, 16, output).ToArray());
        return path;
    }

    private static float[] Tone(int rate, double hz, double amplitude, double seconds) =>
        Enumerable.Range(0, (int)(rate * seconds))
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))
            .ToArray();

    private static void CheckTone(float[] samples, double hz, double amplitude, string prefix = "")
    {
        // Skip the edges, then fit the tone: its level and its pitch must survive.
        var middle = samples.AsSpan(800, samples.Length - 1_600);
        var rms = Rms(middle);
        Check(Math.Abs(rms - amplitude / Math.Sqrt(2)) < 0.02, $"{prefix}the tone's RMS is {rms:F4}, expected {amplitude / Math.Sqrt(2):F4}");
        var crossings = 0;
        for (var i = 1; i < middle.Length; i++)
            if (middle[i - 1] < 0 && middle[i] >= 0) crossings++;
        var measured = crossings * 16_000.0 / middle.Length;
        Check(Math.Abs(measured - hz) < hz * 0.02, $"{prefix}the tone is {measured:F1} Hz, expected {hz}");
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    private static MemoryStream Wav(int rate, int channels, int bits, float[] mono, bool extensible = false, int format = 1)
    {
        var data = new MemoryStream();
        foreach (var sample in mono)
            for (var channel = 0; channel < channels; channel++)
            {
                if (format == 3) { data.Write(BitConverter.GetBytes(sample)); continue; }
                var value = Math.Clamp((double)sample, -1, 1);
                switch (bits)
                {
                    case 8: data.WriteByte((byte)Math.Round(value * 127 + 128)); break;
                    case 16: data.Write(BitConverter.GetBytes((short)Math.Round(value * 32767))); break;
                    case 24:
                        var v24 = (int)Math.Round(value * 8_388_607);
                        data.Write([(byte)v24, (byte)(v24 >> 8), (byte)(v24 >> 16)]);
                        break;
                    default: data.Write(BitConverter.GetBytes((int)Math.Round(value * 2_147_483_647))); break;
                }
            }
        var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true))
        {
            var fmtSize = extensible ? 40 : 16;
            writer.Write("RIFF"u8);
            writer.Write((uint)(4 + 8 + fmtSize + 8 + 8 + 4 + data.Length));
            writer.Write("WAVE"u8);
            // A chunk before fmt, with an odd size, to check the reader skips and pads it.
            writer.Write("LIST"u8);
            writer.Write(3u);
            writer.Write([1, 2, 3, 0]);
            writer.Write("fmt "u8);
            writer.Write((uint)fmtSize);
            writer.Write((ushort)(extensible ? 0xFFFE : format));
            writer.Write((ushort)channels);
            writer.Write((uint)rate);
            writer.Write((uint)(rate * channels * bits / 8));
            writer.Write((ushort)(channels * bits / 8));
            writer.Write((ushort)bits);
            if (extensible)
            {
                writer.Write((ushort)22);
                writer.Write((ushort)bits);
                writer.Write(4u);
                writer.Write([0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71]);
            }
            writer.Write("data"u8);
            writer.Write((uint)data.Length);
            writer.Write(data.ToArray());
        }
        wav.Position = 0;
        return wav;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
