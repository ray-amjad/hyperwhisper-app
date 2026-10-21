using System.Diagnostics;
using HyperWhisper.Linux;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.Platform.Abstractions.Audio;

// Issue #1004: History selects an item on the UI thread, and the playback wrapper ran ffmpeg there
// and waited for it, so every m4a selection froze the window for as long as the decode took. These
// drive the real wrapper with a real ffmpeg behind a slow shim, so "Load returned before the decode
// finished" is a measured fact and not a claim about which thread a Task happened to run on.
static class FfmpegPlaybackOffUiThreadTests
{
    private const string Ffmpeg = "/usr/bin/ffmpeg";
    private static readonly TimeSpan ShimDelay = TimeSpan.FromSeconds(3);

    public static async Task LoadReturnsBeforeDecodeAndHoldsPlay()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = await MakeM4aAsync(directory, "recording.m4a");
            var shim = WriteShim(directory, $"sleep {ShimDelay.TotalSeconds}\nexec {Ffmpeg} \"$@\"");
            var inner = new RecordingPlayer();
            using var decoding = new FfmpegDecodingAudioPlaybackService(inner, new StaticPaths(directory), shim);

            var clock = Stopwatch.StartNew();
            var loaded = decoding.Load(source);
            var returnedAfter = clock.Elapsed;
            Check(loaded.IsSuccess, $"the m4a load failed: {loaded.Error?.Code}");
            Check(returnedAfter < TimeSpan.FromSeconds(1),
                $"Load blocked the caller for {returnedAfter.TotalMilliseconds:F0} ms behind a {ShimDelay.TotalSeconds} s decode (#1004)");
            Check(decoding.IsLoaded && decoding.LoadedFilePath == source,
                "during the decode the selection did not read as loaded, so History would disable Play");
            Check(inner.Loads.Count == 0, "the inner player was handed a file before the decode finished");

            // History's Play and seek-bar land while ffmpeg is still running.
            decoding.Seek(TimeSpan.FromMilliseconds(40));
            decoding.Play();
            Check(decoding.IsPlaying, "a Play during the decode did not read back as playing");
            Check(inner.Plays == 0, "the inner player was told to play before it had the decoded file");

            await decoding.PendingLoad.WaitAsync(TimeSpan.FromSeconds(30));
            Check(inner.Loads is [var decoded] && Path.GetExtension(decoded) == ".wav" && File.Exists(decoded),
                "the decoded WAV never reached the inner player");
            Check(inner.Seeks is [var seek] && seek == TimeSpan.FromMilliseconds(40), "the held seek was not applied");
            Check(inner.Plays == 1 && decoding.IsPlaying, "the held Play was not applied once the decode finished");
        });
    }

    public static async Task NewSelectionKillsTheOldDecode()
    {
        await WithDirectoryAsync(async directory =>
        {
            var first = await MakeM4aAsync(directory, "first.m4a");
            var second = await MakeM4aAsync(directory, "second.m4a");
            // The first decode never ends on its own: only a kill finishes it.
            var shim = WriteShim(directory, $"case \"$*\" in *first.m4a*) sleep 600;; esac\nexec {Ffmpeg} \"$@\"");
            var inner = new RecordingPlayer();
            using var decoding = new FfmpegDecodingAudioPlaybackService(inner, new StaticPaths(directory), shim);
            var failures = new List<PlatformError>();
            decoding.PlaybackFailed += (_, error) => { lock (failures) failures.Add(error); };

            Check(decoding.Load(first).IsSuccess, "the first load failed");
            decoding.Play();
            var superseded = decoding.PendingLoad;
            Check(decoding.Load(second).IsSuccess, "the second load failed");
            await superseded.WaitAsync(TimeSpan.FromSeconds(10));
            await decoding.PendingLoad.WaitAsync(TimeSpan.FromSeconds(30));

            Check(inner.Loads.Count == 1 && decoding.LoadedFilePath == second,
                "the superseded decode reached the player, or the new selection lost its path");
            Check(inner.Plays == 0, "a Play aimed at the old selection started the new one");
            Check(failures.Count == 0, "a superseded decode reported a failure for a selection nobody is on");
            Check(Directory.GetFiles(directory, "playback-*.wav").Length == 1,
                "the killed decode left its temporary WAV behind");
        });
    }

    public static async Task FailedDecodeReportsThroughPlaybackFailed()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = await MakeM4aAsync(directory, "recording.m4a");
            var shim = WriteShim(directory, "exit 1");
            var inner = new RecordingPlayer();
            using var decoding = new FfmpegDecodingAudioPlaybackService(inner, new StaticPaths(directory), shim);
            var failures = new List<PlatformError>();
            decoding.PlaybackFailed += (_, error) => { lock (failures) failures.Add(error); };

            Check(decoding.Load(source).IsSuccess, "the load did not hand the decode to the background");
            decoding.Play();
            await decoding.PendingLoad.WaitAsync(TimeSpan.FromSeconds(30));

            Check(failures is [{ Code: "audio_decode_failed" }],
                $"a failed decode was not reported as audio_decode_failed (got {string.Join(",", failures.Select(f => f.Code))})");
            Check(!decoding.IsLoaded && !decoding.IsPlaying && decoding.LoadedFilePath is null,
                "after a failed decode the selection still read as loaded or playing");
            Check(inner.Loads.Count == 0 && inner.Plays == 0, "a failed decode still reached the player");
            Check(Directory.GetFiles(directory, "playback-*.wav").Length == 0, "a failed decode left a temporary WAV");
        });
    }

    public static async Task DisposeKillsTheDecode()
    {
        await WithDirectoryAsync(async directory =>
        {
            var source = await MakeM4aAsync(directory, "recording.m4a");
            var shim = WriteShim(directory, "sleep 600");
            var inner = new RecordingPlayer();
            var decoding = new FfmpegDecodingAudioPlaybackService(inner, new StaticPaths(directory), shim);
            Check(decoding.Load(source).IsSuccess, "the load failed");
            var pending = decoding.PendingLoad;
            decoding.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(inner.Disposed && inner.Loads.Count == 0, "Dispose let the decode reach the player");
        });
    }

    private static async Task WithDirectoryAsync(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hyperwhisper-playback-async-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { await body(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<string> MakeM4aAsync(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        using var process = Process.Start(new ProcessStartInfo(Ffmpeg)
        {
            UseShellExecute = false,
            ArgumentList = { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.1", "-c:a", "aac", path },
        })!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0 && File.Exists(path), $"could not make the {name} fixture");
        return path;
    }

    private static string WriteShim(string directory, string body)
    {
        var path = Path.Combine(directory, $"ffmpeg-shim-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Reads the canonical layout like the Pulse player, and records what it was told.</summary>
    private sealed class RecordingPlayer : IAudioPlaybackService
    {
        private readonly object _gate = new();
        private readonly List<string> _loads = [];
        private readonly List<TimeSpan> _seeks = [];
        private int _plays;
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public event EventHandler<TimeSpan>? PositionChanged { add { } remove { } }
        public event EventHandler<TimeSpan>? DurationReady { add { } remove { } }
        public event EventHandler<PlatformError>? PlaybackFailed { add { } remove { } }
        public bool IsPlaying { get; private set; }
        public bool IsLoaded => LoadedFilePath is not null;
        public TimeSpan TotalDuration => TimeSpan.Zero;
        public string? LoadedFilePath { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> Loads { get { lock (_gate) return [.. _loads]; } }
        public IReadOnlyList<TimeSpan> Seeks { get { lock (_gate) return [.. _seeks]; } }
        public int Plays { get { lock (_gate) return _plays; } }

        public PlatformResult Load(string audioPath)
        {
            using (var stream = File.OpenRead(audioPath))
                if (PcmWaveHeader.TryRead(stream, out _) != PcmWaveHeaderStatus.Valid)
                    return PlatformResult.Failure("audio_format_unsupported", "Not canonical PCM.");
            lock (_gate) _loads.Add(audioPath);
            LoadedFilePath = audioPath;
            return PlatformResult.Success();
        }
        public void Play() { lock (_gate) _plays++; IsPlaying = true; }
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(TimeSpan position) { lock (_gate) _seeks.Add(position); }
        public void Dispose() => Disposed = true;
    }
}
