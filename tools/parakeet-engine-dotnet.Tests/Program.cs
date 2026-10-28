// This harness deliberately does NOT use top-level statements. The daemon's own
// entry point is a global `Program` type, and a top-level-statement file emits a
// second global `Program` that shadows it — `Program.IsNoSpaceLanguage` then
// fails to resolve (CS0436 + CS0117). A named entry point keeps the daemon's
// type reachable.
using NAudio.Wave;

internal static class DaemonTests
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("three stable passes commit while retaining a tail", StableAgreement),
            ("unstable hypotheses remain volatile", UnstableAgreement),
            ("finish commits the unconfirmed tail without overlap", FinishDeduplicates),
            ("no-space languages preserve their join policy", NoSpaceJoin),
            ("the join policy comes from the shared core", NoSpaceLanguageTable),
            ("the shared table widens the daemon's old four codes", NoSpaceLanguageWidening),
            ("an auto language joins VAD segments from the text", AutoLanguageSegmentJoin),
            ("an auto-language join reads both sides of the boundary", AutoLanguageSegmentJoinIsSymmetric),
            ("an auto-language join does not depend on where the VAD split", AutoLanguageJoinIsBoundaryIndependent),
            ("--segment-join parses to the host's separator", SegmentJoinArgument),
            ("a host-decided separator wins over the language", SegmentJoinOverridesLanguage),
            ("a 48 kHz 6-channel Extensible PCM WAV decodes and folds to 16 kHz mono", ExtensibleSixChannelWav),
            ("an Extensible float WAV decodes like a plain one", ExtensibleFloatWav),
            ("plain mono and stereo WAVs decode as before", PlainMonoAndStereoWav),
            ("the fold carries a partial frame across reads", MonoFoldCarriesPartialFrame),
            ("a WAV whose data ends mid-frame decodes its whole frames", WavEndingMidFrame),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static void StableAgreement()
    {
        var engine = new BoundedWordAgreement(" ");
        var value = "one two three four five six seven eight nine ten";
        Equal("", engine.Observe(value).Committed);
        Equal("", engine.Observe(value).Committed);
        var third = engine.Observe(value);
        Equal("one two three four five six seven", third.Committed);
        Equal(value, third.Preview);
    }

    private static void UnstableAgreement()
    {
        var engine = new BoundedWordAgreement(" ");
        _ = engine.Observe("one two three four five six seven eight");
        _ = engine.Observe("one two changed four five six seven eight");
        var third = engine.Observe("one two three four five six seven eight");
        Equal("", third.Committed);
        Equal("one two three four five six seven eight", third.Preview);
    }

    private static void FinishDeduplicates()
    {
        var engine = new BoundedWordAgreement(" ");
        var value = "one two three four five six seven eight nine ten";
        _ = engine.Observe(value);
        _ = engine.Observe(value);
        _ = engine.Observe(value);
        var final = engine.Finish("six seven eight nine ten eleven");
        Equal("eight nine ten eleven", final.Committed);
        Equal("one two three four five six seven eight nine ten eleven", final.Preview);
    }

    private static void NoSpaceJoin()
    {
        var engine = new BoundedWordAgreement("");
        var final = engine.Finish("alpha beta gamma");
        Equal("alphabetagamma", final.Preview);
    }

    // Program.IsNoSpaceLanguage now delegates to hw-text through the UniFFI core
    // (issue #286). This asserts against the real native library, so it also
    // proves the daemon can load libhyperwhisper_core.so — the P/Invoke this
    // change introduces into a process that previously had none.
    private static void NoSpaceLanguageTable()
    {
        foreach (var code in new[] { "ja", "zh", "ko", "yue" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "en", "de", "fr", "es", "ru", "auto", "" })
            True(!Program.IsNoSpaceLanguage(code), $"'{code}' should be spaced");
    }

    // The four codes the daemon used to hardcode were a strict subset. Moving
    // onto the shared table adds Thai, the explicit Chinese script tags, case
    // insensitivity and the two-character prefix fallback.
    private static void NoSpaceLanguageWidening()
    {
        foreach (var code in new[] { "th", "zh-TW", "zh-Hans", "zh-Hant" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "JA", "YUE", "ZH-HANT" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        foreach (var code in new[] { "zh-CN", "ja-JP", "ko-KR" })
            True(Program.IsNoSpaceLanguage(code), $"{code} should be no-space");
        True(!Program.IsNoSpaceLanguage("en-US"), "en-US should be spaced");
    }

    // `--language auto` is what the hosts send for a mode with no language
    // (`ParakeetDaemonLiveTranscriber.NormalizeLanguage`), and it is not a
    // no-space code — so `DecodeOfflineWithVad` used to wedge a space between
    // every Japanese VAD segment. It now resolves the separator per boundary
    // from the segment text, mirroring `append_trailing_space`.
    private static void AutoLanguageSegmentJoin()
    {
        Equal("こんにちは世界です", JoinLikeDaemon("auto", ["こんにちは", "世界", "です"]));
        Equal("hello there world", JoinLikeDaemon("auto", ["hello", "there", "world"]));
        // A declared language still wins over the text.
        Equal("こんにちは 世界", JoinLikeDaemon("en", ["こんにちは", "世界"]));
        Equal("alphabeta", JoinLikeDaemon("ja", ["alpha", "beta"]));
    }

    // The four ways a segment can carry no script evidence of its own. Each one
    // used to break a Japanese VAD join, because the separator was decided from
    // the segment AFTER the boundary alone.
    private static void AutoLanguageSegmentJoinIsSymmetric()
    {
        // A VAD segment that decodes to nothing.
        Equal("こんにちは世界", JoinLikeDaemon("auto", ["こんにちは", "", "世界"]));
        Equal("こんにちは世界", JoinLikeDaemon("auto", ["こんにちは", "   ", "世界"]));
        // Punctuation-only and digit-heavy segments.
        Equal("日本語。", JoinLikeDaemon("auto", ["日本語", "。"]));
        Equal("これは2024年です", JoinLikeDaemon("auto", ["これは", "2024年", "です"]));
        // Symmetric around an embedded Latin run — a space on one side only was
        // the visible symptom.
        Equal("これはOKです", JoinLikeDaemon("auto", ["これは", "OK", "です"]));
        // Thai: the one no-space language that is not CJK. `th` and `auto` must
        // reach the same answer.
        Equal("สวัสดีครับ", JoinLikeDaemon("auto", ["สวัสดี", "ครับ"]));
        Equal("สวัสดีครับ", JoinLikeDaemon("th", ["สวัสดี", "ครับ"]));
    }

    // Where the VAD put its boundaries must not change the transcript. This is
    // what makes the daemon and the host agree even when their segmentation
    // differs, which is the divergence #286 exists to close.
    private static void AutoLanguageJoinIsBoundaryIndependent()
    {
        Equal("これはtestです", JoinLikeDaemon("auto", ["これはtestです"]));
        Equal("これはtestです", JoinLikeDaemon("auto", ["これは", "test", "です"]));
        Equal("これはtestです", JoinLikeDaemon("auto", ["これ", "はtest", "です"]));
    }

    // The Windows host decides the join from the core and passes it (#902), so
    // the daemon's own language lookup is only the fallback.
    private static void SegmentJoinArgument()
    {
        string[] Args(params string[] extra) => new[] { "--model", "m" }.Concat(extra).ToArray();
        Equal<string?>("", DaemonOptions.Parse(Args("--segment-join", "none"))!.SegmentJoin);
        Equal<string?>(" ", DaemonOptions.Parse(Args("--segment-join", "space"))!.SegmentJoin);
        Equal<string?>(null, DaemonOptions.Parse(Args("--language", "ja"))!.SegmentJoin);
        True(DaemonOptions.Parse(Args("--segment-join", "tab")) == null, "an unknown --segment-join is rejected");
    }

    private static void SegmentJoinOverridesLanguage()
    {
        // "en" alone joins with a space and "ja" alone without; the host's answer wins either way.
        Equal("hello world", EngineSession.JoinSegments("ja", " ", new[] { "hello", "world" }));
        Equal("これはテスト", EngineSession.JoinSegments("en", "", new[] { "これは", "テスト" }));
        Equal("これはテスト", EngineSession.JoinSegments("auto", "", new[] { "これは", " ", "テスト" }));
        Equal("hello world", EngineSession.JoinSegments("en", null, new[] { "hello", "world" }));
    }

    /// <summary>
    /// The daemon's own join, not a copy of it — <c>EngineSession.JoinSegments</c>
    /// is what <c>DecodeOfflineWithVad</c> calls.
    /// </summary>
    private static string JoinLikeDaemon(string language, string[] parts)
        => EngineSession.JoinSegments(language, parts);

    // #1541: ffmpeg writes every WAV with more than two channels as WAVE_FORMAT_EXTENSIBLE.
    // On main this file failed twice: ACM ("NoDriver calling acmFormatSuggest", or no msacm32
    // at all off Windows) and then StereoToMonoSampleProvider ("Source must be stereo").
    private static void ExtensibleSixChannelWav()
    {
        // Channel c carries a DC level of (c + 1) / 8, so the mono average is 3.5 / 8.
        var path = WriteWav(48000, 6, 16, extensible: true, float32: false, frames: 48000,
            (frame, channel) => (channel + 1) / 8.0);
        try
        {
            var audio = Program.ReadAudio(path);
            Equal(16000, audio.SampleRate);
            True(Math.Abs(audio.Samples.Length - 16000) <= 64, $"expected ~16000 samples, got {audio.Samples.Length}");
            var middle = audio.Samples[8000];
            True(Math.Abs(middle - 3.5f / 8) < 0.01f, $"expected the 6-channel average 0.4375, got {middle}");
        }
        finally { File.Delete(path); }
    }

    private static void ExtensibleFloatWav()
    {
        var path = WriteWav(16000, 3, 32, extensible: true, float32: true, frames: 1600,
            (frame, channel) => channel == 0 ? 0.3 : 0.0);
        try
        {
            var audio = Program.ReadAudio(path);
            Equal(1600, audio.Samples.Length);
            True(Math.Abs(audio.Samples[800] - 0.1f) < 0.001f, $"expected 0.1, got {audio.Samples[800]}");
        }
        finally { File.Delete(path); }
    }

    private static void PlainMonoAndStereoWav()
    {
        var mono = WriteWav(16000, 1, 16, extensible: false, float32: false, frames: 1600, (_, _) => 0.25);
        var stereo = WriteWav(16000, 2, 16, extensible: false, float32: false, frames: 1600,
            (_, channel) => channel == 0 ? 0.5 : 0.0);
        try
        {
            var m = Program.ReadAudio(mono);
            Equal(1600, m.Samples.Length);
            True(Math.Abs(m.Samples[800] - 0.25f) < 0.001f, $"mono: expected 0.25, got {m.Samples[800]}");
            var s = Program.ReadAudio(stereo);
            Equal(1600, s.Samples.Length);
            // The old StereoToMonoSampleProvider 0.5/0.5 mix.
            True(Math.Abs(s.Samples[800] - 0.25f) < 0.001f, $"stereo: expected 0.25, got {s.Samples[800]}");
        }
        finally { File.Delete(mono); File.Delete(stereo); }
    }

    private static void MonoFoldCarriesPartialFrame()
    {
        // 3 channels, frames (1,2,3) (4,5,6) (7,8,9), handed out 2 samples at a time.
        var fold = new MonoFoldSampleProvider(new TrickleProvider([1, 2, 3, 4, 5, 6, 7, 8, 9], 3, 2));
        var output = new List<float>();
        var buffer = new float[4];
        int read;
        while ((read = fold.Read(buffer, 0, buffer.Length)) > 0) output.AddRange(buffer.Take(read));
        Equal("2,5,8", string.Join(",", output));
    }

    private static void WavEndingMidFrame()
    {
        // A recorder killed mid-write leaves a data chunk that is not a whole number of
        // frames. NAudio's WaveFileReader refuses a read that is not whole blocks, so the
        // fold must never ask for one after it carries the partial frame.
        foreach (var (channels, extensible) in new[] { (2, false), (6, true) })
        {
            var path = WriteWav(16000, channels, 16, extensible, float32: false, frames: 1600, (_, _) => 0.25);
            try
            {
                AppendPartialFrame(path);
                var audio = Program.ReadAudio(path);
                Equal(1600, audio.Samples.Length);
                True(Math.Abs(audio.Samples[1599] - 0.25f) < 0.001f, $"{channels} ch: expected 0.25, got {audio.Samples[1599]}");
            }
            finally { File.Delete(path); }
        }
    }

    /// <summary>Appends one 16-bit sample (half a stereo frame) and grows both sizes to match.</summary>
    private static void AppendPartialFrame(string path)
    {
        var bytes = File.ReadAllBytes(path).ToList();
        bytes.AddRange(new byte[] { 0x00, 0x20 });
        var file = bytes.ToArray();
        BitConverter.GetBytes(file.Length - 8).CopyTo(file, 4);
        var data = file.Length - 4;
        for (var i = 12; i < file.Length - 8; i++)
            if (file[i] == 'd' && file[i + 1] == 'a' && file[i + 2] == 't' && file[i + 3] == 'a') { data = i; break; }
        BitConverter.GetBytes(file.Length - data - 8).CopyTo(file, data + 4);
        File.WriteAllBytes(path, file);
    }

    private sealed class TrickleProvider(float[] samples, int channels, int perRead) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, perRead), samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    /// <summary>Writes a WAV by hand, so the header (plain or Extensible) is exactly what ffmpeg writes.</summary>
    private static string WriteWav(int rate, int channels, int bits, bool extensible, bool float32, int frames,
        Func<int, int, double> sample)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pe-test-{Guid.NewGuid():N}.wav");
        var blockAlign = channels * bits / 8;
        var dataSize = frames * blockAlign;
        var fmtSize = extensible ? 40 : 16;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(4 + 8 + fmtSize + 8 + dataSize); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(fmtSize);
        w.Write((ushort)(extensible ? 0xFFFE : float32 ? 3 : 1));
        w.Write((ushort)channels); w.Write(rate); w.Write(rate * blockAlign);
        w.Write((ushort)blockAlign); w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22); w.Write((ushort)bits); w.Write(channels == 6 ? 0x3F : 0);
            w.Write(new Guid(float32 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        }
        w.Write("data"u8); w.Write(dataSize);
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
            {
                var value = sample(frame, channel);
                if (float32) w.Write((float)value);
                else w.Write((short)Math.Round(value * 32767));
            }
        return path;
    }

    private static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}
