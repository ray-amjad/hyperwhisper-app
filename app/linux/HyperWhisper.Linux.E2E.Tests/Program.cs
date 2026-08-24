using System.Diagnostics;
using System.Text;
using HyperWhisper.Data.Entities;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using Microsoft.EntityFrameworkCore;

const string ExpectedText = "Ray is verifying the Hyper Whisper Linux speech transcription build.";

var live = args.Contains("--live", StringComparer.Ordinal);
if (live && !OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("The live virtual-microphone E2E requires Linux.");
using var workspace = new TestWorkspace();
var paths = new TestPaths(workspace.Root);
var database = new ApplicationDb(paths);
await using (var context = database.CreateContext())
    await context.Database.EnsureCreatedAsync();
var history = new HistoryRepository(database, paths);
using var injection = new SafeInjectionTarget();

IAudioRecorder recorder;
IRecordedAudioTranscriber transcriber;
string deviceId;
Func<Task>? play = null;

if (live)
{
    var audioPath = RequireEnvironmentFile("HW_E2E_AUDIO_PATH");
    var modelPath = RequireEnvironmentFile("HW_E2E_WHISPER_MODEL");
    Environment.SetEnvironmentVariable("HYPERWHISPER_MODEL_PATH", modelPath);
    deviceId = RequireEnvironment("HW_E2E_SOURCE");
    var sink = RequireEnvironment("HW_E2E_SINK");
    var player = Environment.GetEnvironmentVariable("HW_E2E_PLAYER") ?? "paplay";
    recorder = new PulseAudioRecorder(paths);
    transcriber = new AudioSanityCheckingTranscriber(
        new LinuxLocalWhisperTranscriber(paths.ModelsDirectory),
        audioPath);
    play = () => PlayAsync(player, sink, audioPath);
}
else
{
    deviceId = "deterministic-safe-source";
    recorder = new DeterministicRecorder(paths, ExpectedText);
    transcriber = new DeterministicTranscriber(ExpectedText);
}

using (recorder)
using (var devices = new FixedInputDeviceService(deviceId))
using (var disposableTranscriber = transcriber as IDisposable)
using (var workflow = new TranscriptionWorkflow(
    recorder,
    devices,
    transcriber,
    history,
    textInjection: injection))
{
    injection.CaptureTarget();
    injection.StartSession();
    workflow.RefreshDevices();
    Assert(workflow.Snapshot.SelectedAudioDeviceId == deviceId, "The isolated virtual source was not selected.");

    var started = await workflow.StartRecordingAsync();
    Assert(started.IsSuccess, started.Failure?.Message ?? "Recording did not start.");
    if (play is not null)
    {
        await Task.Delay(250);
        await play();
        // pipewire-pulse may acknowledge the playback client while up to one
        // server buffer remains queued. Keep the monitor open until the graph
        // has emitted that tail; WaveSanity below proves it was not truncated.
        await Task.Delay(2000);
    }

    var result = await workflow.StopAndTranscribeAsync(new TranscriptionWorkflowRequest(Language: "en"));
    Assert(result.IsSuccess, result.Failure?.Message ?? "Transcription failed.");
    Assert(Normalize(result.Text) == Normalize(ExpectedText),
        $"Transcript mismatch. Expected '{ExpectedText}', received '{result.Text}'.");
    Assert(result.InjectionOutcome == TextInjectionOutcome.Pasted,
        $"Safe injection returned {result.InjectionOutcome?.ToString() ?? "no outcome"}.");
    Assert(injection.Writes.Count == 1 && Normalize(injection.Writes[0]) == Normalize(ExpectedText),
        "The transcript was not written exactly once to the isolated injection target.");

    var rows = await history.ListAsync();
    Assert(rows.Count == 1, $"Expected one history row, found {rows.Count}.");
    var row = rows[0];
    Assert(row.Status == TranscriptStatus.Completed, $"History status was {row.Status}.");
    Assert(Normalize(row.Text) == Normalize(ExpectedText), "Persisted history text did not match.");
    Assert(row.AudioFilePath is not null && Path.GetFullPath(row.AudioFilePath).StartsWith(
        Path.GetFullPath(paths.RecordingsDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
        "The captured WAV was not retained inside the isolated recordings directory.");
    Assert(new FileInfo(row.AudioFilePath!).Length > 44, "The captured WAV has no PCM payload.");
}

Console.WriteLine(live
    ? "PASS: private PipeWire virtual microphone -> production Pulse recorder -> local Whisper -> SQLite history -> safe injection"
    : "PASS: deterministic virtual-microphone harness, workflow, SQLite history, and safe injection seam");
return 0;

static string RequireEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required for --live.");

static string RequireEnvironmentFile(string name)
{
    var path = Path.GetFullPath(RequireEnvironment(name));
    return File.Exists(path) ? path : throw new FileNotFoundException($"{name} does not identify a file.", path);
}

static async Task PlayAsync(string player, string sink, string audioPath)
{
    var start = new ProcessStartInfo
    {
        FileName = player,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    if (Path.GetFileName(player).StartsWith("ffmpeg", StringComparison.Ordinal))
    {
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-re", "-i", audioPath,
            "-ac", "1", "-ar", "48000", "-f", "pulse", sink,
        }) start.ArgumentList.Add(argument);
    }
    else
    {
        start.ArgumentList.Add($"--device={sink}");
        start.ArgumentList.Add(audioPath);
    }
    using var process = Process.Start(start)
        ?? throw new InvalidOperationException("The isolated audio player could not be started.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await process.WaitForExitAsync(timeout.Token);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Audio playback failed: {await process.StandardError.ReadToEndAsync()}");
}

static string Normalize(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var result = new StringBuilder(value.Length);
    var needsSpace = false;
    foreach (var rune in value.ToLowerInvariant().EnumerateRunes())
    {
        if (Rune.IsLetterOrDigit(rune))
        {
            if (needsSpace && result.Length > 0) result.Append(' ');
            result.Append(rune);
            needsSpace = false;
        }
        else needsSpace = true;
    }
    return result.ToString();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FixedInputDeviceService(string id) : IAudioInputDeviceService
{
    public event EventHandler? DevicesChanged { add { } remove { } }
    public PlatformResult<IReadOnlyList<AudioInputDevice>> GetAvailableDevices() =>
        PlatformResult<IReadOnlyList<AudioInputDevice>>.Success([new AudioInputDevice(id, "Isolated virtual microphone", true)]);
    public void Dispose() { }
}

sealed class SafeInjectionTarget : ITextInjectionService
{
    public List<string> Writes { get; } = [];
    public bool IsCapturedTargetAvailable { get; private set; }
    public void CaptureTarget() => IsCapturedTargetAvailable = true;
    public void StartSession() { }
    public void EndSession() => IsCapturedTargetAvailable = false;
    public void CancelPendingClipboardRestore() { }
    public void ScheduleClipboardRestore(TimeSpan delay) { }
    public ValueTask<PlatformResult> RestoreClipboardImmediatelyAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformResult.Success());
    public ValueTask<PlatformResult> CopyToClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The E2E must not use a real or fallback clipboard target.");
    public ValueTask<TextInjectionOutcome> InjectTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsCapturedTargetAvailable)
            throw new InvalidOperationException("The safe injection target was not captured.");
        Writes.Add(text);
        return ValueTask.FromResult(TextInjectionOutcome.Pasted);
    }
    public void Dispose() => EndSession();
}

sealed class DeterministicTranscriber(string text) : IRecordedAudioTranscriber
{
    public TranscriptionBackendCapability Capability { get; } = new(true, "Deterministic local Whisper seam");
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableTranscriptionResult.Success(text, Capability.DisplayName));
}

sealed class AudioSanityCheckingTranscriber(IRecordedAudioTranscriber inner, string sourcePath)
    : IRecordedAudioTranscriber, IDisposable
{
    public TranscriptionBackendCapability Capability => inner.Capability;
    public Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, string? language,
        CancellationToken cancellationToken = default) =>
        TranscribeAsync(audioPath, new TranscriptionWorkflowRequest(language), cancellationToken);
    public async Task<PortableTranscriptionResult> TranscribeAsync(string audioPath, TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            WaveSanity.AssertEquivalentTiming(sourcePath, audioPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return PortableTranscriptionResult.Failed(
                PortableTranscriptionErrorCode.InvalidRequest,
                exception.Message,
                Capability.DisplayName);
        }
        return await inner.TranscribeAsync(audioPath, request, cancellationToken).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (inner is IDisposable disposable) disposable.Dispose();
    }
}

static class WaveSanity
{
    public static void AssertEquivalentTiming(string sourcePath, string capturedPath)
    {
        var source = ReadPcm16Mono(sourcePath);
        var captured = ReadPcm16Mono(capturedPath);
        var sourceActive = ActiveDuration(source.Samples, source.SampleRate);
        var capturedActive = ActiveDuration(captured.Samples, captured.SampleRate);
        var ratio = capturedActive.TotalSeconds / sourceActive.TotalSeconds;
        if (sourceActive < TimeSpan.FromSeconds(1) || ratio is < 0.90 or > 1.10)
            throw new InvalidOperationException(
                $"Virtual microphone timing mismatch before ASR: source active {sourceActive.TotalSeconds:F3}s, " +
                $"capture active {capturedActive.TotalSeconds:F3}s (ratio {ratio:F3}).");
    }

    private static (int SampleRate, short[] Samples) ReadPcm16Mono(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Expected a RIFF WAV.");
        _ = reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Expected a WAVE file.");
        short format = 0, channels = 0, bits = 0;
        var rate = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            if (size < 0 || reader.BaseStream.Position + size > reader.BaseStream.Length)
                throw new InvalidDataException("Invalid WAV chunk length.");
            if (id == "fmt ")
            {
                format = reader.ReadInt16(); channels = reader.ReadInt16(); rate = reader.ReadInt32();
                _ = reader.ReadInt32(); _ = reader.ReadInt16(); bits = reader.ReadInt16();
                reader.BaseStream.Position += size - 16;
            }
            else if (id == "data") data = reader.ReadBytes(size);
            else reader.BaseStream.Position += size;
            if ((size & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length) reader.BaseStream.Position++;
        }
        if (format != 1 || channels != 1 || bits != 16 || rate <= 0 || data is null)
            throw new InvalidDataException("The E2E WAV must be mono 16-bit PCM.");
        var samples = new short[data.Length / 2];
        Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 2);
        return (rate, samples);
    }

    private static TimeSpan ActiveDuration(short[] samples, int rate)
    {
        const int threshold = 256;
        var first = Array.FindIndex(samples, sample => Math.Abs((int)sample) >= threshold);
        var last = Array.FindLastIndex(samples, sample => Math.Abs((int)sample) >= threshold);
        return first < 0 || last <= first ? TimeSpan.Zero : TimeSpan.FromSeconds((double)(last - first + 1) / rate);
    }
}

sealed class DeterministicRecorder(TestPaths paths, string seed) : IAudioRecorder
{
    private string? _path;
    public event EventHandler<float>? AudioLevelChanged;
    public bool IsRecording { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(1);
    public PlatformResult Start(AudioRecordingOptions options)
    {
        Directory.CreateDirectory(paths.RecordingsDirectory);
        _path = Path.Combine(paths.RecordingsDirectory, "deterministic-capture.wav");
        var pcm = Encoding.UTF8.GetBytes(seed);
        using var stream = File.Create(_path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        IsRecording = true;
        AudioLevelChanged?.Invoke(this, 0.5f);
        return PlatformResult.Success();
    }
    public PlatformResult<string> Stop()
    {
        IsRecording = false;
        return _path is null
            ? PlatformResult<string>.Failure("test.not_started", "The deterministic recorder was not started.")
            : PlatformResult<string>.Success(_path);
    }
    public void Dispose() { }
}

sealed class TestPaths(string root) : IAppPaths
{
    public string DataDirectory { get; } = Path.Combine(root, "data");
    public string ConfigDirectory { get; } = Path.Combine(root, "config");
    public string CacheDirectory { get; } = Path.Combine(root, "cache");
    public string StateDirectory { get; } = Path.Combine(root, "state");
    public string LogsDirectory { get; } = Path.Combine(root, "state", "logs");
    public string ModelsDirectory { get; } = Path.Combine(root, "data", "models");
    public string RecordingsDirectory { get; } = Path.Combine(root, "data", "recordings");
    public string RuntimeDirectory { get; } = Path.Combine(root, "runtime");
    public string TemporaryDirectory { get; } = Path.Combine(root, "temporary");
}

sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"hyperwhisper-virtual-mic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }
    public string Root { get; }
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("HW_E2E_KEEP_TEMP") == "1")
        {
            Console.Error.WriteLine($"E2E workspace retained for diagnostics: {Root}");
            return;
        }
        var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar + "hyperwhisper-virtual-mic-";
        var full = Path.GetFullPath(Root);
        if (full.StartsWith(prefix, StringComparison.Ordinal) && Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
