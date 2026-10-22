// PARAKEET TRANSCRIPTION SERVICE — TIMEOUTS
// The pure response, drain and teardown budgets and the teardown-generation rules
// that decide whether a failed request is Cancelled or may auto-restart.

using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class ParakeetTranscriptionService
{
    /// <summary>
    /// The response-timeout floor, in seconds, for one request: the whole budget
    /// for a short clip. Qwen3 decodes autoregressively and Nemotron-online streams
    /// on CPU, so both get a longer floor than the Parakeet transducer.
    /// </summary>
    internal static int ResponseFloorSeconds(bool isQwen3, bool isOnline) =>
        isQwen3 ? 180 : (isOnline ? 120 : 60);

    /// <summary>
    /// The hard ceiling on one request's response timeout. It only guards against a
    /// nonsense duration from a broken header; no real input reaches it before
    /// several hours of audio.
    /// </summary>
    internal static readonly TimeSpan MaxResponseTimeout = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to wait for the daemon's reply to one <c>audio_path</c> request (#1562).
    ///
    /// The daemon answers once, for the whole file, so the wait must grow with the
    /// audio: <c>floor + audioSeconds * floor / 30</c>. That is 2 s of wait per audio
    /// second for Parakeet, 4 s for Nemotron-online and 6 s for Qwen3, keeping the
    /// engines' existing 1:2:3 ratio. Parakeet ran at ~0.4x real time on a DirectML
    /// GPU in #1562, so 2x leaves 5x headroom for a slower GPU or the CPU provider.
    /// A short clip keeps today's floor. An unknown, zero, negative or non-finite
    /// duration falls back to the floor alone.
    /// </summary>
    internal static TimeSpan ComputeResponseTimeout(int floorSeconds, double? audioSeconds)
    {
        var floor = TimeSpan.FromSeconds(floorSeconds);
        if (audioSeconds is not { } seconds || !double.IsFinite(seconds) || seconds <= 0)
        {
            return floor;
        }

        var scaledSeconds = floorSeconds + seconds * floorSeconds / 30.0;
        if (scaledSeconds >= MaxResponseTimeout.TotalSeconds)
        {
            return MaxResponseTimeout;
        }

        return TimeSpan.FromSeconds(Math.Ceiling(scaledSeconds));
    }

    /// <summary>
    /// How long the background drain of a caller-cancelled request may wait for the
    /// daemon's reply: what is left of the request's budget, but never more than the
    /// engine floor. The drain holds the transcription lock, so a cancelled multi-hour
    /// file must not keep the next dictation waiting; past this the daemon is killed.
    /// </summary>
    internal static TimeSpan ComputeDrainBudget(int floorSeconds, TimeSpan remainingBudget)
    {
        var floor = TimeSpan.FromSeconds(floorSeconds);
        if (remainingBudget <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remainingBudget < floor ? remainingBudget : floor;
    }

    /// <summary>
    /// The longest <see cref="DisposeModel"/> may block its (UI) thread on the
    /// transcription lock: the engine floor plus a 5 s margin, as on main. Teardown
    /// never waits out a scaled per-request budget (#1562), which can be hours.
    /// </summary>
    internal static TimeSpan TeardownLockWait(int floorSeconds) =>
        TimeSpan.FromSeconds(floorSeconds + 5);

    /// <summary>
    /// How long <see cref="DisposeModel"/> lets the in-flight request finish on its own
    /// before it cancels it (#1562). A short clip (budget up to twice the floor, so at
    /// most ~30 s of audio) gets the floor, as every request did on main, so a mode
    /// switch does not throw away a dictation that is about to land. A longer request
    /// cannot be counted on to finish inside the teardown wait, so it is cancelled at
    /// once rather than freezing the UI for the floor and then being cut off anyway.
    /// No request in flight → no grace.
    /// </summary>
    internal static TimeSpan ComputeTeardownGrace(int floorSeconds, TimeSpan? activeRequestBudget)
    {
        if (activeRequestBudget is not { } budget)
        {
            return TimeSpan.Zero;
        }

        var floor = TimeSpan.FromSeconds(floorSeconds);
        return budget <= floor + floor ? floor : TimeSpan.Zero;
    }

    /// <summary>
    /// True when a failure of the in-flight request is the teardown ending it, so it
    /// must surface as <see cref="TranscriptionErrorCode.Cancelled"/> and never as
    /// <see cref="TranscriptionErrorCode.DaemonCrashed"/>: the auto-restart in
    /// <see cref="TranscribeAsync"/> would reload the old model, undo the mode switch
    /// and re-run the whole file. A caller cancel wins, so callers keep their own
    /// cancel handling; an already-converted exception is left alone.
    /// </summary>
    internal static bool IsEndedByTeardown(Exception ex, bool teardownRequested, bool callerCancelled) =>
        teardownRequested
        && !callerCancelled
        && ex is not TranscriptionException { Code: TranscriptionErrorCode.Cancelled };

    /// <summary>
    /// True when a teardown ran after the request captured <paramref name="capturedGeneration"/>
    /// at entry. Such a request was meant for the model that teardown unloaded: it must not
    /// read daemon state, kill a daemon, or auto-restart; it fails as Cancelled.
    /// </summary>
    internal static bool IsStaleTeardownGeneration(long capturedGeneration, long currentGeneration) =>
        capturedGeneration != currentGeneration;

    /// <summary>
    /// Whether a failed request may reload <c>_lastModelDirectory</c> and retry: only a
    /// DaemonCrashed failure of a request no teardown has overtaken. Never a Cancelled one,
    /// and never a stale one (its reload would undo the mode switch that tore it down).
    /// </summary>
    internal static bool ShouldAutoRestart(TranscriptionErrorCode code, long capturedGeneration, long currentGeneration) =>
        code == TranscriptionErrorCode.DaemonCrashed
        && !IsStaleTeardownGeneration(capturedGeneration, currentGeneration);

    /// <summary>
    /// The failure a request ended by model teardown raises. Its code is Cancelled,
    /// which the auto-restart filter does not match and the Local API already maps.
    /// </summary>
    internal static TranscriptionException CreateTeardownCancelledException(Exception? inner) =>
        new(
            TranscriptionErrorCode.Cancelled,
            "Parakeet transcription was cancelled because the model was unloaded",
            "Parakeet",
            innerException: inner);

    /// <summary>
    /// The audio length of the file the daemon is about to read, or null when it
    /// cannot be read. Only the header is parsed for a WAV.
    /// </summary>
    private static double? TryGetAudioDurationSeconds(string audioPath)
    {
        try
        {
            using var reader = AudioFileDecoder.Open(audioPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: Could not read audio duration, using the fixed timeout floor: {ex.Message}");
            return null;
        }
    }
}
