// Real-signal tests for the shared "Muffle Audio" low-pass (MuffleFilter), the DSP behind the
// Linux and Windows modes and a port of the macOS MuffleDSP. Every check measures tone levels
// in a mix with a single-bin DFT over a whole number of cycles, so the numbers are exact
// amplitudes rather than eyeballed spectra.

using HyperWhisper.Platform.Abstractions.Audio;

var failures = 0; var passed = 0;
void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}

Run("open filter passes the mix unchanged", () =>
{
    foreach (var rate in new[] { 44_100, 48_000 })
    {
        var filter = new MuffleFilter(rate, 2);
        var signal = Mix(rate, 2, seconds: 1, (100, 0.3), (1_000, 0.3), (3_000, 0.3));
        var output = signal.ToArray(); filter.Process(output);
        foreach (var hz in new[] { 100, 1_000, 3_000 })
            for (var c = 0; c < 2; c++)
            {
                var gain = GainDb(signal, output, rate, 2, c, hz, fromSeconds: 0.5);
                Check(Math.Abs(gain) < 0.1, $"{rate} Hz open: {hz} Hz moved {gain:0.000} dB on channel {c}");
            }
        Check(filter.IsOpen && filter.IsSettled, "a fresh filter is not open");
    }
});

Run("engaged at 48 kHz: 3 kHz down 40+ dB, 100 Hz within 1 dB", () => Engaged(48_000));
Run("engaged at 44.1 kHz: 3 kHz down 40+ dB, 100 Hz within 1 dB", () => Engaged(44_100));

Run("release glides back to open", () =>
{
    const int rate = 48_000;
    var filter = new MuffleFilter(rate, 2);
    filter.Engage(); filter.Process(Mix(rate, 2, seconds: 1, (100, 0.3), (3_000, 0.3)));
    Check(!filter.IsOpen && filter.IsSettled, "the filter did not settle muffled");
    filter.Release();
    Check(!filter.IsOpen, "release reported open before any audio ran");
    var glide = Mix(rate, 2, seconds: MuffleFilter.TeardownDelay.TotalSeconds, (3_000, 0.3)); filter.Process(glide);
    Check(filter.CurrentCutoffHz > 15_000, $"after the teardown delay the cutoff is only {filter.CurrentCutoffHz:0} Hz");
    var signal = Mix(rate, 2, seconds: 1, (100, 0.3), (1_000, 0.3), (3_000, 0.3)); var output = signal.ToArray(); filter.Process(output);
    Check(filter.IsOpen, $"the filter is not open after 1.25 s (cutoff {filter.CurrentCutoffHz:0} Hz)");
    foreach (var hz in new[] { 100, 1_000, 3_000 })
    {
        var gain = GainDb(signal, output, rate, 2, 0, hz, fromSeconds: 0.5);
        Check(Math.Abs(gain) < 0.1, $"released: {hz} Hz is {gain:0.000} dB");
    }
});

Run("the glide settles in about three time constants, both ways", () =>
{
    const int rate = 48_000;
    var filter = new MuffleFilter(rate, 1);
    filter.Engage(); var frames = 0; var chunk = new float[480];
    while (!filter.IsSettled && frames < rate) { filter.Process(chunk); frames += chunk.Length; }
    var down = frames / (double)rate;
    Check(down is > 0.15 and < 0.5, $"the glide down took {down:0.000} s");
    filter.Release(); frames = 0;
    while (!filter.IsOpen && frames < rate) { filter.Process(chunk); frames += chunk.Length; }
    var up = frames / (double)rate;
    Check(up is > 0.15 and < 0.5, $"the glide up took {up:0.000} s");
});

Run("block sizes 1..4096 give bit-identical output", () =>
{
    const int rate = 48_000;
    var signal = Mix(rate, 2, seconds: 0.6, (100, 0.3), (1_000, 0.2), (3_000, 0.3));
    float[] Render(Func<int, int> blockSize)
    {
        var filter = new MuffleFilter(rate, 2); filter.Engage();
        var output = new float[signal.Length]; var frame = 0; var frames = signal.Length / 2; var call = 0;
        while (frame < frames)
        {
            var count = Math.Min(blockSize(call++), frames - frame);
            filter.Process(signal.AsSpan(frame * 2, count * 2), output.AsSpan(frame * 2, count * 2));
            frame += count;
        }
        return output;
    }
    var reference = Render(_ => 4096);
    var random = new Random(1895);
    foreach (var (name, size) in new (string, Func<int, int>)[] { ("1", _ => 1), ("7", _ => 7), ("32", _ => 32), ("441", _ => 441),
        ("512", _ => 512), ("1024", _ => 1024), ("random 1..4096", _ => random.Next(1, 4097)) })
    {
        var output = Render(size);
        for (var i = 0; i < output.Length; i++)
            if (output[i] != reference[i]) throw new Exception($"block size {name} differs at sample {i}: {output[i]} vs {reference[i]}");
    }
});

Run("in place equals in to out", () =>
{
    const int rate = 48_000;
    var signal = Mix(rate, 2, seconds: 0.3, (100, 0.3), (3_000, 0.3));
    var a = new MuffleFilter(rate, 2); var b = new MuffleFilter(rate, 2); a.Engage(); b.Engage();
    var inPlace = signal.ToArray(); a.Process(inPlace);
    var output = new float[signal.Length]; b.Process(signal, output);
    Check(inPlace.AsSpan().SequenceEqual(output), "in-place and in-to-out disagree");
});

Run("channels are filtered independently", () =>
{
    const int rate = 48_000;
    var signal = new float[rate * 2];
    for (var f = 0; f < rate; f++) { signal[f * 2] = (float)(0.3 * Math.Sin(2 * Math.PI * 100 * f / rate)); signal[f * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 3_000 * f / rate)); }
    var filter = new MuffleFilter(rate, 2); filter.Engage(); var output = signal.ToArray(); filter.Process(output);
    var left = GainDb(signal, output, rate, 2, 0, 100, 0.5); var right = GainDb(signal, output, rate, 2, 1, 3_000, 0.5);
    Check(Math.Abs(left) < 1, $"left 100 Hz moved {left:0.00} dB"); Check(right < -40, $"right 3 kHz only {right:0.0} dB");
    Check(Level(output, rate, 2, 0, 3_000, 0.5) < 1e-6, "right-channel 3 kHz leaked into the left channel");
});

Run("six channels and mono work", () =>
{
    foreach (var channels in new[] { 1, 6 })
    {
        const int rate = 48_000;
        var signal = Mix(rate, channels, seconds: 1, (100, 0.3), (3_000, 0.3));
        var filter = new MuffleFilter(rate, channels); filter.Engage(); var output = signal.ToArray(); filter.Process(output);
        for (var c = 0; c < channels; c++)
            Check(GainDb(signal, output, rate, channels, c, 3_000, 0.5) < -40, $"{channels}ch: channel {c} 3 kHz not muffled");
    }
});

Run("silence after audio decays to exact zero (denormal flush)", () =>
{
    const int rate = 48_000;
    var filter = new MuffleFilter(rate, 2); filter.Engage(); filter.Process(Mix(rate, 2, 0.5, (100, 0.9)));
    var silence = new float[rate * 2 * 2]; filter.Process(silence);
    var tail = silence.AsSpan(silence.Length - 2_000);
    foreach (var sample in tail) Check(sample == 0, $"tail sample {sample:E} is not zero");
});

Run("processing allocates nothing", () =>
{
    const int rate = 48_000;
    var filter = new MuffleFilter(rate, 2); var buffer = Mix(rate, 2, 0.1, (100, 0.3), (3_000, 0.3)); var output = new float[buffer.Length];
    filter.Process(buffer); filter.Engage();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 200; i++) { filter.Process(buffer.AsSpan(0, 960)); filter.Process(buffer, output); _ = filter.IsOpen; _ = filter.IsSettled; }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0, $"Process allocated {allocated} bytes");
});

Run("target writes from another thread are safe", () =>
{
    const int rate = 48_000;
    var filter = new MuffleFilter(rate, 2); var buffer = Mix(rate, 2, 0.02, (3_000, 0.3)); using var stop = new CancellationTokenSource();
    var writer = Task.Run(() => { var i = 0; while (!stop.IsCancellationRequested) if (i++ % 2 == 0) filter.Engage(); else filter.Release(); });
    for (var i = 0; i < 2_000; i++) { filter.Process(buffer); foreach (var s in buffer) if (!float.IsFinite(s)) throw new Exception("non-finite output"); }
    stop.Cancel(); writer.Wait();
    filter.Engage(); filter.Process(Mix(rate, 2, 1, (3_000, 0.3)));
    Check(filter.IsSettled && Math.Abs(filter.CurrentCutoffHz - MuffleFilter.MuffledCutoffHz) < 5, $"cutoff ended at {filter.CurrentCutoffHz:0.0} Hz");
});

Run("constants match macOS and inputs are validated", () =>
{
    Check(MuffleFilter.MuffledCutoffHz == 400 && MuffleFilter.OpenCutoffHz == 20_000 && MuffleFilter.GlideTimeConstantSeconds == 0.06
        && MuffleFilter.TeardownDelay == TimeSpan.FromMilliseconds(250), "a constant drifted from AudioMuffler.swift");
    Check(new MuffleFilter(44_100, 2).MaxCutoffHz == 19_845 && new MuffleFilter(48_000, 2).MaxCutoffHz == 20_000, "open cutoff is not min(20 kHz, 0.45 sr)");
    var filter = new MuffleFilter(48_000, 2); filter.SetTargetCutoff(1); Check(filter.TargetCutoffHz == 20, "target is not clamped low");
    filter.SetTargetCutoff(1e9); Check(filter.TargetCutoffHz == 20_000, "target is not clamped high");
    Throws<ArgumentOutOfRangeException>(() => new MuffleFilter(48_000, 0)); Throws<ArgumentOutOfRangeException>(() => new MuffleFilter(48_000, 17));
    Throws<ArgumentOutOfRangeException>(() => new MuffleFilter(100, 2));
    Throws<ArgumentException>(() => filter.Process(new float[3])); Throws<ArgumentException>(() => filter.Process(new float[4], new float[6]));
});

Run("s16le and float convert both ways", () =>
{
    short[] pcm = [short.MinValue, -16384, 0, 16384, short.MaxValue];
    var floats = new float[pcm.Length]; PcmFloatConversion.S16ToFloat(pcm, floats);
    Check(floats[0] == -1f && floats[1] == -0.5f && floats[2] == 0 && floats[3] == 0.5f && floats[4] < 1f, "s16 to float is wrong");
    var back = new short[pcm.Length]; PcmFloatConversion.FloatToS16(floats, back);
    Check(back.AsSpan().SequenceEqual(pcm), "the round trip is lossy");
    PcmFloatConversion.FloatToS16([2f, -2f, float.NaN], back);
    Check(back[0] == short.MaxValue && back[1] == short.MinValue && back[2] == 0, "out-of-range floats are not clipped");
    Throws<ArgumentException>(() => PcmFloatConversion.S16ToFloat(pcm, new float[2]));
});

Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

static void Engaged(int rate)
{
    var filter = new MuffleFilter(rate, 2); filter.Engage();
    var signal = Mix(rate, 2, seconds: 1.5, (100, 0.3), (1_000, 0.3), (3_000, 0.3));
    var output = signal.ToArray(); filter.Process(output);
    Check(filter.IsSettled && !filter.IsOpen, "the glide did not settle within 1.5 s");
    for (var c = 0; c < 2; c++)
    {
        var low = GainDb(signal, output, rate, 2, c, 100, fromSeconds: 1); var mid = GainDb(signal, output, rate, 2, c, 1_000, fromSeconds: 1);
        var high = GainDb(signal, output, rate, 2, c, 3_000, fromSeconds: 1);
        Console.WriteLine($"  {rate} Hz ch{c}: 100 Hz {low:0.00} dB, 1 kHz {mid:0.0} dB, 3 kHz {high:0.0} dB");
        Check(Math.Abs(low) < 1, $"100 Hz moved {low:0.00} dB"); Check(mid < -20, $"1 kHz only {mid:0.0} dB"); Check(high < -40, $"3 kHz only {high:0.0} dB");
    }
}

// Interleaved sine mix; each channel gets every tone.
static float[] Mix(int rate, int channels, double seconds, params (double Hz, double Amplitude)[] tones)
{
    var frames = (int)Math.Round(seconds * rate); var samples = new float[frames * channels];
    for (var f = 0; f < frames; f++)
    {
        var value = 0.0; foreach (var (hz, amplitude) in tones) value += amplitude * Math.Sin(2 * Math.PI * hz * f / rate);
        for (var c = 0; c < channels; c++) samples[f * channels + c] = (float)value;
    }
    return samples;
}

// Amplitude of one tone on one channel, from `fromSeconds` to the last whole cycle (single-bin DFT).
static double Level(float[] samples, int rate, int channels, int channel, double hz, double fromSeconds)
{
    var start = (int)(fromSeconds * rate); var frames = samples.Length / channels - start;
    var cycles = Math.Floor(frames * hz / rate); var count = (int)Math.Round(cycles * rate / hz);
    double re = 0, im = 0;
    for (var i = 0; i < count; i++)
    {
        var x = samples[(start + i) * channels + channel]; var phase = 2 * Math.PI * hz * (start + i) / rate;
        re += x * Math.Cos(phase); im += x * Math.Sin(phase);
    }
    return 2 * Math.Sqrt(re * re + im * im) / count;
}

static double GainDb(float[] input, float[] output, int rate, int channels, int channel, double hz, double fromSeconds) =>
    20 * Math.Log10(Math.Max(Level(output, rate, channels, channel, hz, fromSeconds), 1e-12) / Level(input, rate, channels, channel, hz, fromSeconds));

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"expected {typeof(T).Name}");
}
