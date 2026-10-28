using System.Text;
using HyperWhisper.LocalInference;
using Whisper.net.Wave;

/// <summary>
/// The short-clip encoder window and the kept processor (issue #1006). The
/// window rule runs everywhere; the processor checks need a real model.
/// </summary>
static class AudioContextChecks
{
    private const int Rate = 16_000;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("audio_ctx covers a short clip plus its margin, and a long or unknown one keeps the full window (issue #1006)", () =>
        {
            Check(LocalWhisperService.AudioContextFor((long?)null) == 0, "an unknown length must keep the full window");
            Check(LocalWhisperService.AudioContextFor(0) == 0, "an empty clip must keep the full window");
            Check(LocalWhisperService.AudioContextFor(-5) == 0, "a negative length must keep the full window");
            // 50 frames a second: 2.4 s is 120 frames, + 256 = 376, rounded up to 384.
            Check(LocalWhisperService.AudioContextFor(Samples(2.4)) == 384, $"2.4 s got {LocalWhisperService.AudioContextFor(Samples(2.4))}");
            Check(LocalWhisperService.AudioContextFor(Samples(10.7)) == 896, $"10.7 s got {LocalWhisperService.AudioContextFor(Samples(10.7))}");
            Check(LocalWhisperService.AudioContextFor(1) == 384, "one sample must still get the margin");
            // The last bucket under 1500 is 1408: 1152 frames (23.04 s) + 256.
            Check(LocalWhisperService.AudioContextFor(Samples(23.04)) == 1408, "23.04 s must get the last short bucket");
            Check(LocalWhisperService.AudioContextFor(Samples(23.04) + 1) == 0, "past 23.04 s the window must be the full one");
            foreach (var seconds in new[] { 29.9, 30.0, 30.1, 60.0, 3600.0 })
                Check(LocalWhisperService.AudioContextFor(Samples(seconds)) == 0, $"{seconds} s must keep the full window");
            var previous = 0;
            for (long samples = 1; samples <= Samples(23.04); samples += 97)
            {
                var context = LocalWhisperService.AudioContextFor(samples);
                var frames = (samples + 319) / 320;
                Check(context >= frames + LocalWhisperService.AudioContextMargin,
                    $"{samples} samples ({frames} frames) got {context}, less than the margin past the clip");
                Check(context < LocalWhisperService.FullAudioContext, $"{samples} samples got {context}, not under the full window");
                Check(context % LocalWhisperService.AudioContextBucket == 0, $"{samples} samples got {context}, not a bucket");
                Check(context >= previous, $"{samples} samples got a smaller window than a shorter clip");
                previous = context;
            }
        });
        yield return ("digital silence keeps the full window, and a quiet clip with signal is shortened (issue #1006)", () =>
        {
            // A muted mic: a shortened window made tiny.en hear "you" in zeros.
            Check(LocalWhisperService.AudioContextFor(new float[Samples(3)]) == 0, "3 s of zeros got a shortened window");
            Check(LocalWhisperService.AudioContextFor(new float[1]) == 0, "one zero sample got a shortened window");
            Check(LocalWhisperService.AudioContextFor((float[]?)null) == 0, "unread audio got a shortened window");
            Check(LocalWhisperService.AudioContextFor(Array.Empty<float>()) == 0, "an empty clip got a shortened window");
            // 3 LSB of 16-bit dither, both signs, is still silence.
            var dither = new float[Samples(3)];
            for (var i = 0; i < dither.Length; i++) dither[i] = (i % 7 - 3) / 32768f;
            Check(LocalWhisperService.IsDigitalSilence(dither) && LocalWhisperService.AudioContextFor(dither) == 0,
                "3 LSB of dither got a shortened window");
            // The quietest noise fixture peaks at -64 dBFS; one such sample is signal.
            var quiet = new float[Samples(2.4)];
            quiet[^1] = -6.1e-4f;
            Check(LocalWhisperService.AudioContextFor(quiet) == LocalWhisperService.AudioContextFor((long)quiet.Length),
                "a -64 dBFS clip kept the full window");
            Check(!LocalWhisperService.IsDigitalSilence([LocalWhisperService.DigitalSilencePeak]), "a sample at the threshold read as silence");
            Check(LocalWhisperService.IsDigitalSilence([LocalWhisperService.DigitalSilencePeak * 0.99f]), "a sample under the threshold read as signal");
            // The threshold sits between 3 LSB of dither and the -64 dBFS noise fixture.
            Check(LocalWhisperService.IsDigitalSilence([3 / 32768f, -3 / 32768f]), "3 LSB read as signal");
            Check(!LocalWhisperService.IsDigitalSilence([2e-4f]), "-74 dBFS read as silence");
        });
        yield return ("a shortened window's repetition loop is caught, and speech-rate text is not (issue #1006)", () =>
        {
            const string speech = "The quick brown fox jumps over the lazy dog.";
            Check(!LocalWhisperService.LooksRunaway(speech.Length, Samples(2.4)), "a sentence in 2.4 s read as a loop");
            Check(!LocalWhisperService.LooksRunaway("This is a sample review.".Length, Samples(1.0)), "a 1 s phrase read as a loop");
            // 24 characters a second was the full window's densest fixture.
            Check(!LocalWhisperService.LooksRunaway(24 * 25, Samples(25)), "24 characters a second read as a loop");
            var loop = string.Concat(Enumerable.Repeat("The ", 70));
            Check(LocalWhisperService.LooksRunaway(loop.Length, Samples(2.4)), "a 70-word loop in 2.4 s was not caught");
            Check(LocalWhisperService.LooksRunaway(160 * 6, Samples(5.7)), "160 characters a second was not caught");
        });
    }

    /// <summary>
    /// With a real model: one processor serves calls with the same options and
    /// window, a new one replaces it otherwise, and a reload, a failed file, a
    /// cancel or the ARM64 path never reuses the old one.
    /// </summary>
    public static async Task KeepsOneProcessorAsync(string model, string wav)
    {
        var samples = Read(wav);
        var shortClip = Write(samples[..Math.Min(samples.Length, Samples(2.4))]);
        var longClip = Write([.. Enumerable.Repeat(samples, (int)Math.Ceiling(Samples(35) / (double)samples.Length)).SelectMany(s => s)]);
        var broken = Path.Combine(Path.GetTempPath(), $"hw-1006-broken-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(broken, "this is not a WAV file");
        try
        {
            await using var service = new LocalWhisperService();
            var options = new LocalWhisperLoadOptions(model, LocalWhisperBackend.Cpu);
            Check((await service.LoadAsync(options)).IsSuccess, "model load failed");
            var shortContext = LocalWhisperService.AudioContextFor(Math.Min(samples.Length, Samples(2.4)));
            Check(shortContext is > 0 and < LocalWhisperService.FullAudioContext, $"the short clip got window {shortContext}");

            string? first = null;
            for (var call = 0; call < 3; call++)
            {
                var result = await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en"));
                Check(result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text), result.Failure?.Message ?? $"call {call} was empty");
                first ??= result.Text;
                Check(result.Text == first, $"call {call} heard '{result.Text}', the first heard '{first}'");
            }
            Check(service.ProcessorsBuilt == 1, $"3 same calls built {service.ProcessorsBuilt} processors, expected 1");
            Check(service.KeptAudioContext == shortContext, $"kept window {service.KeptAudioContext}, expected {shortContext}");

            var full = await service.TranscribeAsync(new LocalWhisperRequest(longClip, "en"));
            Check(full.IsSuccess && !string.IsNullOrWhiteSpace(full.Text), full.Failure?.Message ?? "the 35 s clip was empty");
            Check(service.KeptAudioContext == 0, $"a 35 s clip kept window {service.KeptAudioContext}, expected the full one");
            Check(service.ProcessorsBuilt == 2, $"a new window built {service.ProcessorsBuilt} processors in all, expected 2");

            // Word timestamps are fixed at Build, so they get their own processor,
            // and a shortened window must not stamp words past the clip.
            var timed = await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en", IncludeWordTimestamps: true));
            Check(timed.IsSuccess && timed.Timestamps is { Words.Count: > 0 }, timed.Failure?.Message ?? "no word timestamps");
            Check(service.ProcessorsBuilt == 3, $"word timestamps built {service.ProcessorsBuilt} processors in all, expected 3");
            var clipEnd = Math.Min(samples.Length, Samples(2.4)) / (double)Rate;
            // Whisper's own [_BEG_] and [_TT_n] tokens pass the word filter on main
            // too, stamped at the 30 s window's end, so only spoken words count here.
            Check(timed.Timestamps!.Words.Where(word => !word.Word.StartsWith("[_", StringComparison.Ordinal))
                    .All(word => word.End <= clipEnd + 0.5),
                $"a word ends past the {clipEnd:F1} s clip: {string.Join(", ", timed.Timestamps.Words.Select(w => $"{w.Word}@{w.End:F2}"))}");

            var failed = await service.TranscribeAsync(new LocalWhisperRequest(broken, "en"));
            Check(failed.Failure?.Code == LocalWhisperErrorCode.TranscriptionFailed, $"a broken file gave {failed.Failure?.Code.ToString() ?? "success"}");
            Check(service.KeptAudioContext is null, "a failed file kept its processor");
            var beforeRetry = service.ProcessorsBuilt;
            Check((await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en"))).IsSuccess, "the call after a failure failed");
            Check(service.ProcessorsBuilt == beforeRetry + 1, "the call after a failure reused a processor");

            using (var cancel = new CancellationTokenSource())
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
                var cancelled = await service.TranscribeAsync(new LocalWhisperRequest(longClip, "en"), cancel.Token);
                // A 35 s clip cannot finish in 150 ms on any CPU this runs on.
                Check(cancelled.Failure?.Code == LocalWhisperErrorCode.Cancelled, $"the cancel gave {cancelled.Failure?.Code.ToString() ?? "success"}");
                Check(service.KeptAudioContext is null, "a cancelled call kept its processor");
            }
            Check((await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en"))).Text == first, "the call after a cancel heard otherwise");

            Check((await service.LoadAsync(options)).IsSuccess, "reload failed");
            Check(service.KeptAudioContext is null, "a reload kept the old model's processor");

            // The ARM64 rule: the processor is made on the factory thread and kept there too.
            var built = service.ProcessorsBuilt;
            for (var chunk = 0; chunk < 3; chunk++)
            {
                var result = await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en", CreateFactoryOnRequestThread: true));
                Check(result.Text == first, $"ARM64 chunk {chunk} heard '{result.Text}', expected '{first}'");
            }
            Check(service.ProcessorsBuilt == built + 1, $"3 ARM64 chunks built {service.ProcessorsBuilt - built} processors, expected 1");
            Check(service.RequestFactoriesBuilt == 1 && service.FactoriesHeld == 1,
                $"ARM64: built {service.RequestFactoriesBuilt} factories, holding {service.FactoriesHeld}, expected 1 and 1");
            var afterFailure = await service.TranscribeAsync(new LocalWhisperRequest(broken, "en", CreateFactoryOnRequestThread: true));
            Check(afterFailure.Failure?.Code == LocalWhisperErrorCode.TranscriptionFailed && service.KeptAudioContext is null,
                "an ARM64 failure kept its processor");
            Check((await service.TranscribeAsync(new LocalWhisperRequest(shortClip, "en", CreateFactoryOnRequestThread: true))).Text == first,
                "the ARM64 call after a failure heard otherwise");
            Check(service.RequestFactoriesBuilt == 2, $"an ARM64 failure did not reset the factory ({service.RequestFactoriesBuilt} built)");
            Check(service.FullWindowRetries == 0, $"{service.FullWindowRetries} shortened runs were redone on the full window");
        }
        finally
        {
            File.Delete(shortClip);
            File.Delete(longClip);
            File.Delete(broken);
        }
    }

    private static int Samples(double seconds) => (int)Math.Round(seconds * Rate);

    private static float[] Read(string wav)
    {
        using var input = File.OpenRead(wav);
        return WaveResampler.TryReadAs16kMono(input) ?? Reread(input);

        static float[] Reread(Stream input)
        {
            input.Position = 0;
            return new WaveParser(input).GetAvgSamples();
        }
    }

    /// <summary>A 16 kHz mono 16-bit WAV of <paramref name="samples"/> in the temp folder.</summary>
    private static string Write(float[] samples)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hw-1006-{Guid.NewGuid():N}.wav");
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + samples.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)Rate);
        writer.Write((uint)(Rate * 2));
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)(samples.Length * 2));
        foreach (var sample in samples) writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767));
        return path;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
