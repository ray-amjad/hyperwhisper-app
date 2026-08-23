using System.Diagnostics;
using HyperWhisper.AudioNormalization;

var root = Path.Combine(Path.GetTempPath(), "HyperWhisper.AudioNormalization.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    var sourceWave = Path.Combine(root, "source.wav");
    WriteToneWave(sourceWave, seconds: 1.2);
    var fixtures = new Dictionary<string, string> { [".wav"] = sourceWave };
    foreach (var format in new[] { ".mp3", ".m4a", ".flac", ".ogg", ".webm" })
    {
        var fixture = Path.Combine(root, $"fixture{format}");
        await RunAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", sourceWave, fixture);
        fixtures[format] = fixture;
    }

    foreach (var (format, source) in fixtures)
    {
        var destination = Path.Combine(root, $"output-{format[1..]}");
        var reports = new List<AudioNormalizationProgress>();
        var result = await new FfmpegAudioNormalizationService().NormalizeAsync(source, destination, new InlineProgress<AudioNormalizationProgress>(reports.Add));
        Assert(result.IsSuccess, $"{format} normalization failed: {result.Error?.Code}");
        Assert(File.Exists(result.Value), $"{format} output is missing");
        Assert(ReadWaveFormat(result.Value!) == (1, 16_000, 16), $"{format} output is not canonical PCM");
        Assert(reports.Count > 0 && reports[^1].Phase == "complete" && reports[^1].Fraction == 1, $"{format} progress did not complete");
        if (!OperatingSystem.IsWindows())
            Assert((File.GetUnixFileMode(result.Value!) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, $"{format} output is not private");
        passed++;
    }

    var injectionMarker = Path.Combine(root, "SHELL_INJECTION_OCCURRED");
    var hostileSource = Path.Combine(root, "audio;touch SHELL_INJECTION_OCCURRED.wav");
    File.Copy(sourceWave, hostileSource);
    var hostileResult = await new FfmpegAudioNormalizationService().NormalizeAsync(hostileSource, Path.Combine(root, "hostile-output"));
    Assert(hostileResult.IsSuccess && !File.Exists(injectionMarker), "a hostile filename escaped into a shell");
    passed++;

    var unsupported = Path.Combine(root, "audio.txt");
    File.Copy(sourceWave, unsupported);
    var unsupportedResult = await new FfmpegAudioNormalizationService().NormalizeAsync(unsupported, Path.Combine(root, "unsupported-output"));
    Assert(unsupportedResult.Error?.Code == "audio_normalization.unsupported_format", "unsupported input was accepted");
    passed++;

    var oversized = new FfmpegAudioNormalizationService(new FfmpegAudioNormalizationOptions { MaximumInputBytes = 64, MaximumOutputBytes = 1024 });
    var oversizedResult = await oversized.NormalizeAsync(sourceWave, Path.Combine(root, "oversized-output"));
    Assert(oversizedResult.Error?.Code == "audio_normalization.invalid_size", "oversized input was accepted");
    Assert(!Directory.EnumerateFiles(Path.Combine(root, "oversized-output")).Any(), "oversized input left partial files");
    passed++;

    var outputBounded = new FfmpegAudioNormalizationService(new FfmpegAudioNormalizationOptions
    {
        MaximumInputBytes = 2 * 1024 * 1024,
        MaximumOutputBytes = 1024
    });
    var outputBoundedDestination = Path.Combine(root, "output-bounded");
    var outputBoundedResult = await outputBounded.NormalizeAsync(sourceWave, outputBoundedDestination);
    Assert(outputBoundedResult.Error?.Code == "audio_normalization.invalid_output", "oversized output was accepted");
    Assert(!Directory.EnumerateFiles(outputBoundedDestination).Any(), "oversized output left partial files");
    passed++;

    var cancellationSource = Path.Combine(root, "cancel.wav");
    using (var stream = new FileStream(cancellationSource, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.SetLength(16 * 1024 * 1024);
    using var cancellation = new CancellationTokenSource();
    var cancellationDestination = Path.Combine(root, "cancel-output");
    var cancellationProgress = new InlineProgress<AudioNormalizationProgress>(report =>
    {
        if (report.Phase == "staging") cancellation.Cancel();
    });
    await AssertThrowsAsync<OperationCanceledException>(() =>
        new FfmpegAudioNormalizationService().NormalizeAsync(cancellationSource, cancellationDestination, cancellationProgress, cancellation.Token));
    Assert(!Directory.EnumerateFiles(cancellationDestination).Any(), "cancellation left partial files");
    passed++;

    var invalid = Path.Combine(root, "invalid.wav");
    await File.WriteAllTextAsync(invalid, "not audio");
    var invalidDestination = Path.Combine(root, "invalid-output");
    var invalidResult = await new FfmpegAudioNormalizationService().NormalizeAsync(invalid, invalidDestination);
    Assert(invalidResult.Error?.Code == "audio_normalization.ffmpeg_failed", "invalid audio did not return a structured decode failure");
    Assert(!Directory.EnumerateFiles(invalidDestination).Any(), "decode failure left partial files");
    passed++;

    var missingResult = await new FfmpegAudioNormalizationService(new FfmpegAudioNormalizationOptions
    {
        FfmpegExecutable = Path.Combine(root, "missing-ffmpeg"),
        FfprobeExecutable = Path.Combine(root, "missing-ffprobe")
    }).NormalizeAsync(sourceWave, Path.Combine(root, "missing-output"));
    Assert(missingResult.Error?.Code == "audio_normalization.ffmpeg_unavailable", "missing FFmpeg did not return a structured dependency failure");
    Assert(!Directory.EnumerateFiles(Path.Combine(root, "missing-output")).Any(), "missing FFmpeg left partial files");
    passed++;

    Console.WriteLine($"Audio normalization verification passed ({passed}/13 scenarios).\n"
        + "Formats: WAV, MP3, M4A, FLAC, OGG, WebM; canonical 16 kHz mono PCM; progress; privacy; injection; limits; cancellation; cleanup.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

static void WriteToneWave(string path, double seconds)
{
    const int sampleRate = 44_100;
    const short channels = 2;
    const short bits = 16;
    var sampleCount = checked((int)(sampleRate * seconds));
    var dataLength = checked(sampleCount * channels * (bits / 8));
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(36 + dataLength);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write(channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * (bits / 8));
    writer.Write((short)(channels * (bits / 8)));
    writer.Write(bits);
    writer.Write("data"u8);
    writer.Write(dataLength);
    for (var sample = 0; sample < sampleCount; sample++)
    {
        var value = (short)(Math.Sin(2 * Math.PI * 440 * sample / sampleRate) * short.MaxValue * 0.25);
        writer.Write(value);
        writer.Write(value);
    }
}

static (ushort Channels, uint SampleRate, ushort Bits) ReadWaveFormat(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    Assert(new string(reader.ReadChars(4)) == "RIFF", "missing RIFF header");
    _ = reader.ReadUInt32();
    Assert(new string(reader.ReadChars(4)) == "WAVE", "missing WAVE header");
    while (stream.Position + 8 <= stream.Length)
    {
        var name = new string(reader.ReadChars(4));
        var length = reader.ReadUInt32();
        if (name == "fmt ")
        {
            Assert(reader.ReadUInt16() == 1, "output is not PCM");
            return (reader.ReadUInt16(), reader.ReadUInt32(), ReadRemainingFormat(reader));
        }
        stream.Seek(length + (length & 1), SeekOrigin.Current);
    }
    throw new InvalidOperationException("missing fmt chunk");
}

static ushort ReadRemainingFormat(BinaryReader reader)
{
    _ = reader.ReadUInt32();
    _ = reader.ReadUInt16();
    return reader.ReadUInt16();
}

static async Task RunAsync(string executable, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert(process.ExitCode == 0, $"fixture generation failed: {error}");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
