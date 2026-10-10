// PARAKEET TRANSCRIPTION SERVICE
// Manages the lifecycle of the parakeet-engine.exe daemon process for speech-to-text
// transcription via stdio pipes using a JSON protocol.
//
// DAEMON COMMUNICATION PROTOCOL:
// - Startup: daemon prints {"status":"ready","provider":"directml"} on stdout
// - Transcribe: write {"audio_path":"..."} to stdin, read {"text":"...","duration_ms":N} from stdout
// - Quit: write {"command":"quit"} to stdin
// - Errors: daemon writes diagnostic messages to stderr
//
// DESIGN NOTES:
// - Only one transcription at a time (stdio is serial) — enforced by SemaphoreSlim
// - Auto-restart on daemon crash during transcription (single retry)
// - Supports both DirectML (GPU) and CPU providers via ONNX Runtime

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.SharedCore;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// Transcription provider that delegates to an external parakeet-engine.exe daemon process.
/// Communicates via stdin/stdout JSON lines protocol.
///
/// The daemon is a C++ process that loads ONNX Parakeet TDT models and performs
/// speech-to-text transcription using either DirectML (GPU) or CPU backends.
/// </summary>
public class ParakeetTranscriptionService : ITranscriptionProvider, ILocalVocabularyCorrection, IDisposable
{
    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>
    /// The daemon process handle. Null when no daemon is running.
    /// </summary>
    private Process? _daemonProcess;

    /// <summary>
    /// Writer to the daemon's stdin for sending commands.
    /// </summary>
    private StreamWriter? _stdinWriter;

    /// <summary>
    /// Reader from the daemon's stdout for receiving responses.
    /// </summary>
    private StreamReader? _stdoutReader;

    /// <summary>
    /// Whether the daemon has sent the READY signal and is accepting commands.
    /// Set to false on daemon crash or disposal.
    /// </summary>
    private bool _isReady;

    /// <summary>
    /// The model directory passed to the last successful InitializeAsync call.
    /// Used for display purposes and auto-restart.
    /// </summary>
    private string? _loadedModelId;

    /// <summary>
    /// The provider reported by the daemon (e.g., "directml", "cpu").
    /// Set from the READY JSON response.
    /// </summary>
    private string? _activeProvider;

    /// <summary>
    /// The language passed to the last successful InitializeAsync call.
    /// Used for auto-restart after daemon crash.
    /// </summary>
    private string? _lastLanguage;

    /// <summary>
    /// The model directory passed to the last successful InitializeAsync call.
    /// Used for auto-restart after daemon crash.
    /// </summary>
    private string? _lastModelDirectory;

    /// <summary>
    /// True when the loaded model runs the Qwen3 engine. Qwen3 loads ~1.2 GB of
    /// ONNX sessions and decodes autoregressively, so it gets longer startup and
    /// response timeouts than the small Parakeet transducer.
    /// </summary>
    private bool _isQwen3;

    /// <summary>
    /// True when the loaded model runs the Nemotron-3.5 online/streaming engine.
    /// Online models take language as "auto" when no explicit language is set
    /// (the daemon maps it to the model's auto language detection) and stream
    /// on CPU, so they get a slightly longer startup/response budget than the
    /// offline Parakeet transducer.
    /// </summary>
    private bool _isOnline;

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

    /// <summary>How often a waiting teardown re-cancels the request holding the lock.</summary>
    private static readonly TimeSpan TeardownCancelSlice = TimeSpan.FromMilliseconds(250);

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

    /// <summary>
    /// Serializes access to stdin/stdout — only one transcription can be in flight at a time.
    /// </summary>
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);

    /// <summary>
    /// Coordinates the background drain that owns _transcriptionLock after caller cancellation.
    /// </summary>
    private readonly object _drainSync = new();
    private Task? _inFlightDrainTask;
    private CancellationTokenSource? _inFlightDrainCts;

    /// <summary>
    /// The request that holds _transcriptionLock right now, so <see cref="DisposeModel"/>
    /// can end it on purpose instead of disposing the streams under its read (#1562).
    /// Guarded by <see cref="_drainSync"/>. The budget is the request's response timeout.
    /// </summary>
    private CancellationTokenSource? _activeRequestTeardownCts;
    private TimeSpan _activeRequestBudget;

    /// <summary>
    /// The idle reload (<see cref="ReloadWhenIdleAsync"/>) that holds _transcriptionLock
    /// right now, so <see cref="DisposeModel"/> can cancel it at once, exactly as it cancels
    /// an active request, instead of blocking the UI thread through a daemon start (#1608
    /// review). Guarded by <see cref="_drainSync"/>; never set together with
    /// <see cref="_activeRequestTeardownCts"/>, since both hold the lock.
    /// </summary>
    private CancellationTokenSource? _idleReloadTeardownCts;

    /// <summary>
    /// Advanced (Interlocked) by every deliberate teardown — <see cref="DisposeModel"/>,
    /// including the one inside a mode switch's <see cref="InitializeAsync"/> — BEFORE it
    /// cancels or waits. <see cref="TranscribeAsync"/> captures it at entry, before the
    /// lock wait, so a request that began before a teardown (e.g. a Local API call queued
    /// behind the request being torn down) knows it is stale once it gets the lock, and
    /// fails as Cancelled without touching the daemon or auto-restarting the old model.
    /// The auto-restart's own reload does not advance it.
    /// </summary>
    private long _teardownGeneration;

    private long CurrentTeardownGeneration => Interlocked.Read(ref _teardownGeneration);

    /// <summary>
    /// Requests inside <see cref="TranscribeAsync"/> right now (running, queued on the
    /// lock, or auto-restarting) plus the callers holding a <see cref="ModelLease"/> or
    /// running an idle reload. Guarded by <see cref="_drainSync"/>, and changed in the
    /// same step as the generation capture, so <see cref="ReloadWhenIdleAsync"/> sees an
    /// idle daemon only when no request could be ended by its teardown, and no other
    /// caller's model could be replaced before it has transcribed (#1608).
    /// </summary>
    private int _pendingRequests;

    /// <summary>How often <see cref="ReloadWhenIdleAsync"/> re-checks a busy daemon.</summary>
    private static readonly TimeSpan IdleReloadPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Options for serializing daemon requests. Uses the relaxed encoder so non-ASCII
    /// characters in file paths stay literal UTF-8 instead of being escaped as \uXXXX.
    /// Safe here because the payload is written to a child process's stdin, never to HTML/JS.
    /// </summary>
    private static readonly JsonSerializerOptions s_requestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Set by <see cref="Transcription.TranscriptionRuntime"/> for the
    /// process-wide singleton. When true, <see cref="Dispose"/> is a no-op so
    /// the API server and GUI safely share the same instance.
    /// </summary>
    private readonly bool _isShared;

    public ParakeetTranscriptionService() : this(isShared: false) { }

    internal ParakeetTranscriptionService(bool isShared)
    {
        _isShared = isShared;
    }

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Whether the daemon is running and ready to accept transcription requests.
    /// </summary>
    public bool IsAvailable => _isReady && _daemonProcess != null && !_daemonProcess.HasExited;

    /// <summary>
    /// Display name including the loaded model and active provider.
    /// </summary>
    public string Name => _loadedModelId != null
        ? $"Parakeet {_loadedModelId} ({_activeProvider ?? "CPU"})"
        : "Parakeet (not loaded)";

    /// <summary>
    /// The provider reported by the daemon (e.g., "directml", "cpu").
    /// Null if no daemon is running.
    /// </summary>
    public string? ActiveProvider => _activeProvider;

    /// <summary>
    /// The model ID that was loaded (directory name of the model).
    /// Null if no model is loaded.
    /// </summary>
    public string? LoadedModelId => _loadedModelId;

    /// <summary>
    /// The selected language affecting daemon behavior for the loaded model.
    /// Null if no model is loaded. ACCEPTED STALENESS: after a TDT mode switch
    /// that <see cref="NeedsReload"/> skipped (same join class, e.g.
    /// en→fr), this still reports the last SPAWN's language, not the mode's —
    /// the daemon genuinely runs with that spawn hint. Consumers (auto-restart,
    /// Qwen3 empty-result heuristic) only need the spawn value.
    /// </summary>
    public string? LoadedLanguage => _lastLanguage;

    /// <summary>
    /// Whether the daemon is initialized and ready. Alias for compatibility with existing patterns.
    /// </summary>
    public bool IsInitialized => _isReady;

    /// <summary>
    /// Whether the daemon joins transcription segments for <paramref name="code"/>
    /// without spaces. KEEP IN SYNC with tools/parakeet-engine/main.cpp
    /// <c>is_no_space_language</c>.
    /// </summary>
    internal static bool IsNoSpaceLanguage(string code) =>
        code is "ja" or "zh" or "ko" or "yue";

    /// <summary>
    /// Canonicalizes a mode/request language for reload comparison:
    /// null / whitespace / "auto" → "auto"; everything else trimmed + lowercased.
    /// </summary>
    internal static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "auto";
        }

        var normalized = language.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "auto" : normalized;
    }

    /// <summary>
    /// How the daemon spawned with <paramref name="normalizedLanguage"/> joins its
    /// segments. THREE classes, not two — <c>"auto"</c> is its own.
    /// </summary>
    /// <remarks>
    /// The daemon's join used to have two outcomes, so comparing
    /// <see cref="IsNoSpaceLanguage"/> across a mode switch was enough. Since
    /// issue #286 it has three: a no-space code joins with <c>""</c>, any other
    /// declared code joins with <c>" "</c>, and <c>"auto"</c> decides per boundary
    /// from the segment text. <c>"en"</c> and <c>"auto"</c> therefore produce
    /// DIFFERENT transcripts for the same Japanese audio, while
    /// <c>IsNoSpaceLanguage</c> calls them the same class — so a warm daemon
    /// spawned for <c>en</c> was kept for an auto-language mode and went on
    /// joining Japanese with spaces.
    /// <para>
    /// Over-classifying is safe here: the only cost of an unnecessary class change
    /// is one extra daemon respawn.
    /// </para>
    /// </remarks>
    internal static string ResolveJoinClass(string normalizedLanguage) => normalizedLanguage switch
    {
        "auto" => "auto",
        var code when IsNoSpaceLanguage(code) => "no-space",
        _ => "spaced",
    };

    /// <summary>
    /// Whether switching to <paramref name="modelId"/> + <paramref name="requestedLanguage"/>
    /// (raw mode value; null/"auto" mean auto-detect) requires respawning the daemon.
    /// Single source of truth for the previously copy-pasted call-site checks.
    ///
    /// - Not ready, or a different model → reload.
    /// - Language-hint engines (Nemotron online, Qwen3) take the language at
    ///   spawn → reload on any normalized-language change.
    /// - Parakeet TDT auto-detects language; its spawn hint only picks the
    ///   segment join → reload only when the join CLASS changes
    ///   (en→fr skips; en→ja and en→auto reload — see <see cref="ResolveJoinClass"/>).
    /// </summary>
    public bool NeedsReload(string modelId, string? requestedLanguage)
    {
        // Lock-free reads of _isReady/_loadedModelId/_lastLanguage — same
        // consistency model as the call sites this consolidates.
        if (!_isReady)
        {
            return true;
        }

        if (!string.Equals(_loadedModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requested = NormalizeLanguage(requestedLanguage);
        var loaded = NormalizeLanguage(_lastLanguage);

        if (_isOnline || _isQwen3)
        {
            return !string.Equals(requested, loaded, StringComparison.Ordinal);
        }

        return !string.Equals(ResolveJoinClass(requested), ResolveJoinClass(loaded), StringComparison.Ordinal);
    }

    // =========================================================================
    // DAEMON PATHS
    // =========================================================================

    /// <summary>
    /// Resolves the absolute path to the parakeet-engine.exe daemon binary.
    /// </summary>
    private static string GetDaemonPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "parakeet-engine.exe");
    }

    /// <summary>
    /// Resolves the absolute path to the Silero VAD ONNX model used by the daemon.
    /// </summary>
    private static string GetVadModelPath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appDir, "parakeet-engine", "silero_vad.onnx");
    }

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Initializes the Parakeet transcription service by spawning the daemon process
    /// and waiting for the READY signal.
    ///
    /// DAEMON STARTUP PROCESS:
    /// 1. Validate daemon binary and model directory exist
    /// 2. Spawn parakeet-engine.exe with model/vad/engine arguments
    /// 3. Wait for {"status":"ready","provider":"..."} on stdout (30s timeout)
    /// 4. Start background stderr reader for diagnostics
    /// 5. Register Process.Exited handler for crash detection
    /// </summary>
    /// <param name="modelDirectory">Path to the directory containing the ONNX model files.</param>
    /// <param name="language">
    /// Requested language code. Parakeet TDT auto-detects language and does not
    /// apply this at decode time; engines that support language hints receive it.
    /// </param>
    public Task InitializeAsync(string modelDirectory, string? language) =>
        InitializeCoreAsync(modelDirectory, language, advanceTeardownGeneration: true);

    /// <summary>
    /// Makes the daemon fit <paramref name="modelId"/> + <paramref name="language"/> for a
    /// caller that shares it with someone else's job — the Local API, a History retry,
    /// onboarding's Try It — without ending that job (#1608), and returns a LEASE the
    /// caller holds until its own transcription has ended.
    /// <see cref="InitializeAsync"/> is the user's own mode switch: it cancels whatever is
    /// in flight. This instead waits, honouring <paramref name="cancellationToken"/>, until
    /// it holds the transcription lock with nobody else counted in (a request inside
    /// <see cref="TranscribeAsync"/>, running, queued or auto-restarting, or another
    /// caller's lease; a caller-cancel drain holds the lock, so it is waited out too). It
    /// then reloads while still holding the lock, so no request can start on the old
    /// daemon between the wait and the teardown.
    /// <para>
    /// The reload and the transcription it is for are ONE reserved unit: the lease counts
    /// the caller as a pending request from the moment the model fits until it is
    /// disposed, so a second waiting reload (another model) cannot load its model in the
    /// gap before the caller's <see cref="TranscribeAsync"/> (review round 1). When the
    /// warm daemon already fits, the lease is taken at once without the lock.
    /// </para>
    /// <para>
    /// While it reloads, the reservation is visible to teardown like an active request:
    /// a GUI <see cref="DisposeModel"/> / <see cref="InitializeAsync"/> cancels it with no
    /// grace, the reload stops only the daemon it started and releases the lock, and this
    /// throws <see cref="TranscriptionErrorCode.Cancelled"/>. The user's deliberate mode
    /// switch wins and never waits out a 30–90 s daemon start on the UI thread.
    /// </para>
    /// </summary>
    /// <param name="language">The init form: null for auto-detect, as for InitializeAsync.</param>
    /// <returns>The lease; dispose it after the transcription. Reloaded is false when no reload was needed.</returns>
    public async Task<ModelLease> ReloadWhenIdleAsync(
        string modelId,
        string modelDirectory,
        string? language,
        CancellationToken cancellationToken)
    {
        var loggedWait = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The warm daemon fits: count the caller in and go, as before #1608 a warm
            // request did not wait for the job in flight either (it queues on the lock).
            if (TryLeaseWarmDaemon(modelId, language) is { } warmLease)
            {
                return warmLease;
            }

            await _transcriptionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-checked under the lock: a reload queued ahead of this one may have
                // loaded this very model already.
                if (TryLeaseWarmDaemon(modelId, language) is { } lease)
                {
                    return lease;
                }

                if (TryReserveIdleReload() is { } reloadTeardownCts)
                {
                    return await RunIdleReloadAsync(modelDirectory, language, reloadTeardownCts).ConfigureAwait(false);
                }
            }
            finally
            {
                _transcriptionLock.Release();
            }

            if (!loggedWait)
            {
                LoggingService.Info("ParakeetTranscriptionService: Waiting for the other transcription to finish before reloading the model");
                loggedWait = true;
            }

            await Task.Delay(IdleReloadPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The reserved reload, with the transcription lock held and the caller already counted
    /// in. On success the count passes to the returned lease; on any failure it is dropped.
    /// The caller's own token is not linked in: like the GUI's load, a load once started is
    /// finished, so a disconnecting client does not leave the shared daemon half-started.
    /// </summary>
    private async Task<ModelLease> RunIdleReloadAsync(
        string modelDirectory,
        string? language,
        CancellationTokenSource reloadTeardownCts)
    {
        var leased = false;
        try
        {
            try
            {
                await InitializeCoreAsync(
                    modelDirectory,
                    language,
                    advanceTeardownGeneration: false,
                    transcriptionLockHeld: true,
                    idleReloadCancellation: reloadTeardownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (reloadTeardownCts.IsCancellationRequested)
            {
                LoggingService.Info("ParakeetTranscriptionService: A model teardown cancelled the idle reload");
                throw CreateTeardownCancelledException(ex);
            }

            if (!EndIdleReload(reloadTeardownCts))
            {
                // The teardown asked after READY arrived. Its DisposeModel is waiting on the
                // lock to stop this daemon anyway; stopping it here (still under the lock, so
                // it is this reload's own) spares that wait its graceful-exit pause, and the
                // caller must not run on a daemon the user just unloaded.
                LoggingService.Info("ParakeetTranscriptionService: A model teardown overtook the idle reload; stopping its daemon");
                StopDaemonInstance(_daemonProcess);
                throw CreateTeardownCancelledException(null);
            }

            leased = true;
            return new ModelLease(this, reloaded: true);
        }
        finally
        {
            EndIdleReload(reloadTeardownCts);
            if (!leased)
            {
                ExitRequest();
            }
        }
    }

    /// <summary>
    /// The caller's hold on the model it asked <see cref="ReloadWhenIdleAsync"/> for. While
    /// it is held, the caller counts as a pending request, so no other idle reload replaces
    /// the model before the caller's transcription has run. Dispose it once that
    /// transcription has ended (success or failure); disposing twice is harmless.
    /// </summary>
    public sealed class ModelLease : IDisposable
    {
        private ParakeetTranscriptionService? _owner;

        internal ModelLease(ParakeetTranscriptionService owner, bool reloaded)
        {
            _owner = owner;
            Reloaded = reloaded;
        }

        /// <summary>True when this call respawned the daemon; false when the warm one fit.</summary>
        public bool Reloaded { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitRequest();
    }

    /// <summary>
    /// A lease without the lock when the warm daemon fits and no idle reload is running.
    /// Checked and counted in one step under <see cref="_drainSync"/>, where an idle reload
    /// registers itself, so a lease is never handed out on a model being replaced.
    /// </summary>
    private ModelLease? TryLeaseWarmDaemon(string modelId, string? language)
    {
        lock (_drainSync)
        {
            if (_idleReloadTeardownCts != null || NeedsReload(modelId, language))
            {
                return null;
            }

            _pendingRequests++;
        }

        return new ModelLease(this, reloaded: false);
    }

    /// <summary>
    /// Counts a request in and returns the teardown generation it belongs to, in one step
    /// under <see cref="_drainSync"/> so it cannot interleave with <see cref="TryReserveIdleReload"/>.
    /// </summary>
    private long EnterRequest()
    {
        lock (_drainSync)
        {
            _pendingRequests++;
            return CurrentTeardownGeneration;
        }
    }

    private void ExitRequest()
    {
        lock (_drainSync)
        {
            _pendingRequests--;
        }
    }

    /// <summary>
    /// With the transcription lock held and nobody counted in: advances the teardown
    /// generation, counts the reloading caller in, and registers the reload's teardown CTS
    /// (so <see cref="DisposeModel"/> can cancel it), all in one step. Null when busy. A
    /// request that enters after this belongs to the new generation, waits on the lock,
    /// and runs on the reloaded daemon.
    /// </summary>
    private CancellationTokenSource? TryReserveIdleReload()
    {
        lock (_drainSync)
        {
            if (_pendingRequests > 0)
            {
                return null;
            }

            Interlocked.Increment(ref _teardownGeneration);
            _pendingRequests++;
            var cts = new CancellationTokenSource();
            _idleReloadTeardownCts = cts;
            return cts;
        }
    }

    /// <summary>
    /// Unregisters the idle reload (idempotent). False when a teardown cancelled it. The
    /// CTS is not disposed, for the same reason as <see cref="EndActiveRequest"/>.
    /// </summary>
    private bool EndIdleReload(CancellationTokenSource reloadTeardownCts)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_idleReloadTeardownCts, reloadTeardownCts))
            {
                _idleReloadTeardownCts = null;
            }

            return !reloadTeardownCts.IsCancellationRequested;
        }
    }

    /// <param name="advanceTeardownGeneration">
    /// False for the auto-restart in <see cref="TranscribeAsync"/> (reloading the same model
    /// after a crash is not a teardown, so requests queued behind it stay current) and for
    /// <see cref="ReloadWhenIdleAsync"/>, which advanced it already when it found the daemon idle.
    /// </param>
    /// <param name="transcriptionLockHeld">
    /// True only from <see cref="ReloadWhenIdleAsync"/>, which holds the lock: the teardown
    /// must not wait on it or cancel anyone.
    /// </param>
    /// <param name="idleReloadCancellation">
    /// Only from <see cref="ReloadWhenIdleAsync"/>: cancelled by a GUI teardown. It cuts the
    /// old daemon's graceful-exit wait and the READY wait short; the load then stops the
    /// daemon it started and throws <see cref="OperationCanceledException"/>.
    /// </param>
    private async Task InitializeCoreAsync(
        string modelDirectory,
        string? language,
        bool advanceTeardownGeneration,
        bool transcriptionLockHeld = false,
        CancellationToken idleReloadCancellation = default)
    {
        LoggingService.Info("========== INITIALIZING PARAKEET TRANSCRIPTION SERVICE ==========");
        // The model id, not the directory. Models live under
        // %LOCALAPPDATA%\HyperWhisper\Models, so the directory carries the user's
        // Windows account name; the leaf folder IS the model id and is the only part
        // that answers "which model was this".
        LoggingService.Info($"  Model: {Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}");

        // Dispose any existing daemon first
        DisposeModelCore(advanceTeardownGeneration, transcriptionLockHeld, idleReloadCancellation);
        idleReloadCancellation.ThrowIfCancellationRequested();

        var daemonPath = GetDaemonPath();
        var vadModelPath = GetVadModelPath();

        // Guard: validate daemon binary exists
        if (!File.Exists(daemonPath))
        {
            LoggingService.Error("ParakeetTranscriptionService: Daemon binary not found in the app directory");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                // The path is out of the message too, not just the log line: this
                // message travels one frame up and is logged again as ex.Message by
                // TranscriptionRetryHandler and MainViewModel. GetUserMessage() is a
                // fixed sentence for this code, so nothing the user sees changes.
                "Parakeet engine binary not found in the app directory",
                "Parakeet");
        }

        // Guard: validate model directory exists
        if (!Directory.Exists(modelDirectory))
        {
            LoggingService.Error("ParakeetTranscriptionService: Model directory not found");
            throw new TranscriptionException(
                TranscriptionErrorCode.OnnxModelFileMissing,
                // Same reason as above - GetUserMessage() covers this code too.
                "Model directory not found",
                "Parakeet");
        }

        // Guard: validate VAD model exists
        if (!File.Exists(vadModelPath))
        {
            LoggingService.Warn("ParakeetTranscriptionService: VAD model not found in the app directory, proceeding without VAD");
        }

        // The daemon THIS load starts. Every kill below targets it, never whatever
        // _daemonProcess holds by then: a failed or cancelled load must not stop a
        // daemon someone else started (#1608 review).
        Process? process = null;
        try
        {
            // STEP 1: Spawn the daemon process
            LoggingService.Info("Step 1: Spawning parakeet-engine daemon...");

            // Resolve which engine the daemon should load from the model catalog
            // (the model directory's leaf name is the model Id). Falls back to the
            // Parakeet transducer for anything not in the catalog.
            var modelId = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var modelInfo = ParakeetModelInfo.AllModels.FirstOrDefault(m => m.Id == modelId);
            var engineArg = modelInfo?.DaemonEngineArg ?? "nemo_transducer";
            _isQwen3 = modelInfo?.Engine == ParakeetEngine.Qwen3;
            _isOnline = modelInfo?.Engine == ParakeetEngine.NemotronMl;
            LoggingService.Info($"  Engine: {engineArg}");

            // The caller passes null for "auto-detect" (MainViewModel converts the
            // "auto" UI value to null). Qwen3 and Nemotron have real auto language
            // handling, so forward "auto" instead of defaulting those engines to English.
            var supportsLanguageHint = _isOnline || _isQwen3;
            var loadedLanguage = language ?? "auto";
            var daemonLanguage = supportsLanguageHint ? loadedLanguage : "auto";
            if (supportsLanguageHint)
            {
                LoggingService.Info($"  Language: {daemonLanguage}");
            }
            else
            {
                LoggingService.Info($"  Requested language '{loadedLanguage}' is auto-detected by Parakeet TDT (not applied)");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = daemonPath,
                // Pin the daemon's working directory to its own folder. The child inherits
                // HyperWhisper.exe's CWD by default (often C:\Users\<user> when launched from
                // the Start Menu), and the Windows DLL search order probes the CWD before %PATH%.
                // Pinning it to the engine folder — where the legit ONNX Runtime / DirectML
                // native DLLs are installed — prevents DLL planting from a user-writable CWD.
                WorkingDirectory = Path.GetDirectoryName(daemonPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The daemon speaks raw UTF-8 on stdin/stdout (it puts both in _O_BINARY and
                // passes non-ASCII bytes through unescaped). Without these, redirected-stream
                // encoding defaults to the console code page — a legacy ANSI/OEM page on most
                // non-US Windows locales — which mojibakes accented/Cyrillic/Greek/CJK output
                // from multilingual Parakeet. UTF8Encoding(false) suppresses the BOM so the
                // stdin writer never prepends EF BB BF to the first JSON request line.
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };

            // Build arguments via ArgumentList so .NET applies correct Windows
            // quoting/escaping. String concatenation corrupts paths containing a
            // quote or a trailing backslash (e.g. a UNC path like \\server\share\),
            // where CommandLineToArgvW treats the trailing \" as an escaped quote.
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelDirectory);
            if (supportsLanguageHint)
            {
                startInfo.ArgumentList.Add("--language");
                startInfo.ArgumentList.Add(daemonLanguage);
            }
            else if (!string.IsNullOrWhiteSpace(language))
            {
                startInfo.ArgumentList.Add("--join-language");
                startInfo.ArgumentList.Add(language);
            }
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(vadModelPath);
            startInfo.ArgumentList.Add("--engine");
            startInfo.ArgumentList.Add(engineArg);

            // The argument SHAPE, not the command line. ArgumentList holds the model
            // directory and the VAD model path, both under the user's profile, so
            // joining it printed their Windows account name into the log file. The
            // engine, the language hint and the argument count are what a failed
            // start actually needs.
            LoggingService.Debug(
                $"ParakeetTranscriptionService: Starting daemon (engine={engineArg}, " +
                $"language_hint={(supportsLanguageHint ? daemonLanguage : "n/a")}, " +
                $"join_language={(!supportsLanguageHint && !string.IsNullOrWhiteSpace(language) ? language : "n/a")}, " +
                $"arg_count={startInfo.ArgumentList.Count})");

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _daemonProcess = process;

            // Register crash detection before starting
            process.Exited += OnDaemonExited;

            if (!process.Start())
            {
                LoggingService.Error("ParakeetTranscriptionService: Failed to start daemon process");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    "Failed to start parakeet-engine process",
                    "Parakeet");
            }

            _stdinWriter = process.StandardInput;
            _stdoutReader = process.StandardOutput;

            LoggingService.Info($"  Daemon PID: {process.Id}");

            // STEP 2: Start background stderr reader for diagnostics
            LoggingService.Debug("Step 2: Starting stderr reader thread...");
            var stderrCapture = StartStderrReader(process);

            // STEP 3: Wait for READY signal on stdout.
            // Qwen3 loads ~1.2 GB of ONNX sessions on a cold cache — give it longer.
            var readySeconds = _isQwen3 ? 90 : (_isOnline ? 45 : 30);
            LoggingService.Info($"Step 3: Waiting for daemon READY signal ({readySeconds}s timeout)...");

            var readyTimeout = TimeSpan.FromSeconds(readySeconds);
            // An idle reload's teardown token ends the wait too (#1608 review); the catch
            // below tells it apart from the READY timeout.
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(idleReloadCancellation);
            readyCts.CancelAfter(readyTimeout);

            // WaitAsync, not only the read's token: a read on the daemon's pipe is not
            // guaranteed to observe cancellation, and a cancelled idle reload must hand the
            // lock back at once. The abandoned read ends when the daemon is killed below.
            var readLineTask = _stdoutReader.ReadLineAsync(readyCts.Token).AsTask();
            try
            {
                // ConfigureAwait(false): ReloadWhenIdleAsync holds the transcription lock
                // across this wait, and DisposeModel can block the UI thread on that lock
                // until this load has seen its cancel, so the continuation must not need
                // the UI thread (#1608).
                var line = await readLineTask.WaitAsync(readyCts.Token).ConfigureAwait(false);

                if (line == null)
                {
                    LoggingService.Error("ParakeetTranscriptionService: Daemon closed stdout before sending READY");
                    var earlyExitCode = TryGetEarlyExitCode(process);
                    KillDaemonProcess(process);

                    // The daemon died while ONNX Runtime parsed the model: a damaged
                    // ONNX file throws a native exception nothing catches, and the
                    // engine fail-fasts with 0xC0000409 before READY (#1598). Mark the
                    // model broken so it stops counting as installed and Model Library
                    // offers it for download again (the only way out for Qwen3, whose
                    // file sizes are not pinned). Any other exit (a missing DLL, an
                    // access violation, OOM, a kill, a bad argument, still running)
                    // is not something a re-download fixes, so it is left alone.
                    if (IsModelLoadCrashExitCode(earlyExitCode))
                    {
                        LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine crashed while loading the model");
                    }
                    else
                    {
                        LoggingService.Warn($"ParakeetTranscriptionService: Daemon exit code {FormatExitCode(earlyExitCode)} is not a model-load crash; the model is not marked broken");
                    }
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        "Parakeet daemon closed stdout before sending READY signal",
                        "Parakeet");
                }

                LoggingService.Debug($"ParakeetTranscriptionService: Received from daemon: {DescribeDaemonResponse(line)}");

                // Parse the READY JSON
                using var readyDoc = JsonDocument.Parse(line);
                var root = readyDoc.RootElement;

                if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "ready")
                {
                    _activeProvider = root.TryGetProperty("provider", out var providerProp)
                        ? providerProp.GetString()
                        : "cpu";

                    _isReady = true;
                    _loadedModelId = Path.GetFileName(modelDirectory);
                    _lastModelDirectory = modelDirectory;
                    _lastLanguage = loadedLanguage;

                    LoggingService.Info($"  Daemon is READY (provider: {_activeProvider})");
                }
                else
                {
                    var errorMsg = root.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString() ?? "Unknown error"
                        : $"Unexpected response: {line}";

                    LoggingService.Error($"ParakeetTranscriptionService: Daemon reported error: {errorMsg}");
                    KillDaemonProcess(process);

                    // "Failed to load model" alone does not say WHY (#1598 review round 2):
                    // the daemon sends it for a missing sherpa-onnx/onnxruntime DLL, a
                    // type-initializer fault, OOM, a provider failure and a bad model file
                    // alike. Its stderr carries the cause, written before this line, so
                    // read that to its end (the daemon is gone) and mark the model broken
                    // only when it names a fault in the model's own files.
                    await stderrCapture.WaitForEndAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (IsModelFileLoadFailure(errorMsg, stderrCapture.Snapshot(), modelDirectory))
                    {
                        LocalModelHealth.MarkBroken(modelDirectory, "Parakeet engine could not load the model");
                    }
                    else if (IsModelLoadErrorResponse(errorMsg))
                    {
                        LoggingService.Warn("ParakeetTranscriptionService: The daemon's stderr names no fault in the model files (a runtime, provider or memory failure); the model is not marked broken");
                    }
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        $"Parakeet daemon failed to initialize: {errorMsg}",
                        "Parakeet");
                }
            }
            catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
            {
                // A GUI teardown cancelled this idle reload: stop the daemon it started and
                // hand the lock back at once (ReloadWhenIdleAsync reports Cancelled).
                LoggingService.Info("ParakeetTranscriptionService: Idle reload cancelled while waiting for READY; stopping its daemon");
                StopDaemonInstance(process);
                _ = ObserveInFlightReadAsync(readLineTask);
                throw;
            }
            catch (OperationCanceledException)
            {
                LoggingService.Error($"ParakeetTranscriptionService: Daemon did not send READY within {readySeconds} seconds");
                KillDaemonProcess(process);
                _ = ObserveInFlightReadAsync(readLineTask);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    $"Parakeet daemon timed out waiting for READY signal ({readySeconds}s)",
                    "Parakeet");
            }
            catch (JsonException ex)
            {
                LoggingService.Error("ParakeetTranscriptionService: Failed to parse daemon READY response", ex);
                KillDaemonProcess(process);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    "Parakeet daemon sent invalid JSON on startup",
                    "Parakeet",
                    ex);
            }

            LoggingService.Info("========== PARAKEET TRANSCRIPTION SERVICE READY ==========");
        }
        catch (TranscriptionException)
        {
            // Re-throw TranscriptionExceptions as-is
            throw;
        }
        catch (OperationCanceledException) when (idleReloadCancellation.IsCancellationRequested)
        {
            // The idle reload's cancel, already handled above; not a start failure.
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ParakeetTranscriptionService: Unexpected error during initialization", ex);
            // Only what this load started; null when it failed before the spawn.
            KillDaemonProcess(process);
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                $"Failed to start Parakeet daemon: {ex.Message}",
                "Parakeet",
                ex);
        }
    }

    // =========================================================================
    // TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes an audio file by sending the path to the daemon via stdin
    /// and reading the result from stdout.
    ///
    /// PROTOCOL:
    /// 1. Write {"audio_path":"/path/to/file.wav"} to stdin
    /// 2. Read {"text":"transcribed text","duration_ms":1234} from stdout
    ///
    /// AUTO-RESTART:
    /// If the daemon crashes during transcription, this method will attempt to
    /// restart the daemon once and retry the transcription.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // Captured before any lock wait: a teardown after this point makes the request stale.
        // Counted until it returns, so ReloadWhenIdleAsync never tears it down (#1608).
        var teardownGeneration = EnterRequest();
        try
        {
            // ConfigureAwait(false): ExitRequest must not wait for a UI-thread caller's
            // context; the caller's own await still resumes on it.
            return await TranscribeEnteredAsync(audioPath, vocabulary, teardownGeneration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitRequest();
        }
    }

    private async Task<string> TranscribeEnteredAsync(
        string audioPath,
        IReadOnlyList<string>? vocabulary,
        long teardownGeneration,
        CancellationToken cancellationToken)
    {
        // Guard: validate audio file exists
        if (!File.Exists(audioPath))
        {
            LoggingService.Error($"ParakeetTranscriptionService: Audio file not found: {LoggingService.DescribePath(audioPath)}");
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {LoggingService.DescribePath(audioPath)}",
                "Parakeet");
        }

        // Log warning for vocabulary — Parakeet TDT does not support it
        if (vocabulary != null && vocabulary.Count > 0)
        {
            LoggingService.Warn("ParakeetTranscriptionService: Parakeet TDT does not support vocabulary boosting — vocabulary will be ignored");
        }

        try
        {
            return await TranscribeInternalAsync(audioPath, teardownGeneration, cancellationToken);
        }
        catch (TranscriptionException ex) when (ex.Code == TranscriptionErrorCode.DaemonCrashed)
        {
            if (!ShouldAutoRestart(ex.Code, teardownGeneration, CurrentTeardownGeneration))
            {
                // A teardown overtook this request: reloading _lastModelDirectory would undo it.
                LoggingService.Info("ParakeetTranscriptionService: Daemon failure after a model teardown; not auto-restarting");
                throw CreateTeardownCancelledException(ex);
            }

            // Auto-restart: attempt to restart daemon and retry once
            LoggingService.Warn("ParakeetTranscriptionService: Daemon crashed during transcription, attempting auto-restart...");

            if (_lastModelDirectory == null)
            {
                LoggingService.Error("ParakeetTranscriptionService: Cannot auto-restart — no previous model directory");
                throw;
            }

            try
            {
                await InitializeCoreAsync(_lastModelDirectory, _lastLanguage, advanceTeardownGeneration: false);
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart successful, retrying transcription...");
                // Same entry generation: a teardown during the restart makes the retry stale.
                return await TranscribeInternalAsync(audioPath, teardownGeneration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart retry cancelled by caller");
                throw;
            }
            catch (TranscriptionException cancelledEx) when (cancelledEx.Code == TranscriptionErrorCode.Cancelled)
            {
                // A model teardown ended the retry; keep it Cancelled, not "restart failed".
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart retry ended by model teardown");
                throw;
            }
            catch (Exception restartEx)
            {
                LoggingService.Error("ParakeetTranscriptionService: Auto-restart failed", restartEx);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon crashed and auto-restart failed",
                    "Parakeet",
                    restartEx);
            }
        }
    }

    /// <summary>
    /// The on-device phonetic vocabulary pass over the raw engine result, via the
    /// shared Rust core (<c>hw-phonetic</c>, issue #283). It corrects a misheard
    /// token towards a spelling-hint row (a vocabulary row with NO replacement);
    /// the core skips every row that carries a replacement.
    ///
    /// <see cref="TranscribeAsync(string, string?, IReadOnlyList{string}?, CancellationToken)"/>
    /// returns the engine's text untouched. <see cref="Transcription.TranscriptionOrchestrator"/>
    /// calls this once, AFTER it has kept that text as <c>RawText</c> (the History
    /// row's raw transcript), and then runs its own <c>\b</c>-anchored
    /// <see cref="VocabularyProcessor"/> pass once over the result (issue #1596).
    ///
    /// There used to be a second local pass here: the unanchored,
    /// diacritic-insensitive <c>ApplySubstringVocabulary</c>, a copy of the macOS
    /// local providers' shape. It is gone on Windows (issue #1596, Ray's decision
    /// of 2026-10-09). It matched inside words ("art" -> "ART" turned "quarterly"
    /// into "quARTerly"), and the orchestrator's <c>\b</c> pass then applied every
    /// replacement row a second time ("fox" -> "fox terrier" gave
    /// "fox terrier terrier"). Replacement rows are now applied by the <c>\b</c>
    /// pass alone, whole words only, exactly as on Whisper. macOS and Linux are
    /// tracked in #1622.
    ///
    /// Why this cannot apply a replacement twice: the phonetic pass only ever
    /// writes a hint row's own spelling, never a replacement value, and the
    /// <c>\b</c> pass runs each replacement row once per transcription
    /// (<c>replace_all</c> never rescans the text it inserted).
    ///
    /// TDT + Nemotron only. Qwen3 gets no phonetic pass on macOS
    /// (Qwen3AsrProvider), so it is gated here too.
    ///
    /// Vocabulary is GLOBAL (all items, no settings gate) — macOS parity. One
    /// core call per transcription; the core owns its own process-wide code
    /// cache, so a fresh read of the vocabulary each time can never be stale.
    /// </summary>
    public string ApplyLocalVocabularyCorrection(string rawText)
        // Read _isQwen3 once: a model switch between the transcription and this
        // call is the only way it could disagree with the engine that produced
        // rawText.
        => ApplyLocalVocabularyCorrection(rawText, VocabularyService.Instance.GetAll, _isQwen3);

    /// <summary>
    /// Describes one daemon response line for the log without the transcript
    /// (#1645). The result line is <c>{"text":"…","duration_ms":N}</c>, and the
    /// log ships in the Export Diagnostics bundle, which promises no transcripts.
    ///
    /// Numbers, booleans and nulls are kept as they are. A string is kept only
    /// for <c>status</c>, <c>provider</c> and <c>error</c>, which the engine fills
    /// from fixed text; every other string, <c>text</c> included, is reduced to
    /// its length. A line that is not a JSON object is reduced to its length.
    /// </summary>
    internal static string DescribeDaemonResponse(string? responseLine)
    {
        if (string.IsNullOrEmpty(responseLine))
        {
            return "(empty)";
        }

        try
        {
            using var document = JsonDocument.Parse(responseLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"(JSON {root.ValueKind}, {responseLine.Length} chars)";
            }

            var parts = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                parts.Add(value.ValueKind switch
                {
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                        => $"{property.Name}={value.GetRawText()}",
                    JsonValueKind.String when s_daemonResponsePlainStringKeys.Contains(property.Name)
                        => $"{property.Name}={value.GetString()}",
                    JsonValueKind.String
                        => $"{property.Name}=<{value.GetString()?.Length ?? 0} chars>",
                    _ => $"{property.Name}=<{value.ValueKind}>"
                });
            }

            return parts.Count == 0 ? "{}" : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return $"(not JSON, {responseLine.Length} chars)";
        }
    }

    private static readonly HashSet<string> s_daemonResponsePlainStringKeys =
        new(StringComparer.Ordinal) { "status", "provider", "error" };

    /// <summary>
    /// <see cref="ApplyLocalVocabularyCorrection(string)"/> with the vocabulary
    /// source and the engine passed in, so a test can drive the real core call
    /// without a loaded model.
    /// </summary>
    internal static string ApplyLocalVocabularyCorrection(
        string rawText,
        Func<IEnumerable<VocabularyItem>> readVocabulary,
        bool isQwen3)
    {
        if (string.IsNullOrEmpty(rawText) || isQwen3)
        {
            return rawText;
        }

        // Crosses the FFI on the transcription hot path; macOS links the core
        // statically and has no equivalent failure mode, so degrade gracefully
        // here rather than failing the transcription.
        try
        {
            var entries = readVocabulary()
                .Select(item => new PortableVocabularyEntry(item.Word ?? string.Empty, item.Replacement))
                .ToList();
            if (entries.Count == 0)
            {
                return rawText;
            }

            var phonetic = SharedCoreBridge.ApplyPhoneticVocabulary(rawText, entries);
            // The count only: a match's token is a word the user spoke (#1645).
            if (phonetic.Matches.Count > 0)
            {
                LoggingService.Debug($"Phonetic match: {phonetic.Matches.Count} token(s) corrected");
            }
            return phonetic.Text;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Local vocabulary pass failed, returning unmatched text: {ex.Message}");
            return rawText;
        }
    }

    /// <summary>
    /// Internal transcription implementation that handles the stdio protocol.
    /// Separated from TranscribeAsync to allow retry logic in the caller.
    /// </summary>
    private async Task<string> TranscribeInternalAsync(string audioPath, long teardownGeneration, CancellationToken cancellationToken)
    {
        // Acquire the transcription lock — stdio is serial.
        //
        // Every await in this method uses ConfigureAwait(false). DisposeModel blocks its
        // caller (the UI thread, on a mode switch) on this same lock; a continuation that
        // needed that thread could not run to release the lock, so teardown would always
        // sit out its whole wait.
        await _transcriptionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var releaseLockInFinally = true;
        var teardownCts = BeginActiveRequest();

        try
        {
            // Guard: a teardown ran while this request waited for the lock (#1562 review).
            // It was meant for the model that teardown unloaded, so it ends here as
            // Cancelled: it must not read daemon state, and its failure must not reach the
            // auto-restart (which would reload the old model and undo a mode switch).
            if (IsStaleTeardownGeneration(teardownGeneration, CurrentTeardownGeneration))
            {
                LoggingService.Info("ParakeetTranscriptionService: Model was torn down while this transcription waited; cancelling it");
                throw CreateTeardownCancelledException(null);
            }

            // Guard: daemon must be ready
            if (!IsAvailable)
            {
                LoggingService.Error("ParakeetTranscriptionService: Daemon is not running or not ready");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon is not running",
                    "Parakeet");
            }

            // The daemon instance this request talks to. Every kill below targets this
            // instance, never whatever _daemonProcess holds by then: if a teardown gave up
            // on the lock and a new model loaded, this request must not kill the new daemon.
            var daemonProcess = _daemonProcess;

            var stopwatch = Stopwatch.StartNew();
            LoggingService.Info("========== STARTING PARAKEET TRANSCRIPTION ==========");
            LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");
            LoggingService.Info($"  Provider: {_activeProvider ?? "unknown"}");
            LoggingService.Info($"  Model: {_loadedModelId ?? "unknown"}");

            // Log audio file info
            var fileInfo = new FileInfo(audioPath);
            LoggingService.Info($"  Audio file size: {fileInfo.Length:N0} bytes");

            // STEP 1: Write the audio path to stdin as JSON
            var request = JsonSerializer.Serialize(new { audio_path = audioPath }, s_requestJsonOptions);
            LoggingService.Debug($"ParakeetTranscriptionService: Sending request: {request}");

            await _stdinWriter!.WriteLineAsync(request).ConfigureAwait(false);
            await _stdinWriter.FlushAsync().ConfigureAwait(false);

            // STEP 2: Read the response from stdout with a model-aware timeout that
            // grows with the audio length (#1562): the daemon replies once for the
            // whole file, so a fixed ceiling failed every long Transcribe File.
            var audioSeconds = TryGetAudioDurationSeconds(audioPath);
            var responseTimeout = ComputeResponseTimeout(ResponseFloorSeconds(_isQwen3, _isOnline), audioSeconds);
            var responseSeconds = (long)responseTimeout.TotalSeconds;
            var responseDeadlineTicks = Environment.TickCount64 + (long)responseTimeout.TotalMilliseconds;
            var audioLabel = audioSeconds is { } knownSeconds ? $"{knownSeconds:F1}s" : "unknown";
            LoggingService.Debug($"ParakeetTranscriptionService: Waiting for transcription response ({responseSeconds}s timeout, audio {audioLabel})...");
            SetActiveRequestBudget(teardownCts, responseTimeout);

            var readTimeoutCts = new CancellationTokenSource(responseTimeout);
            var responseReadTask = _stdoutReader!.ReadLineAsync(readTimeoutCts.Token).AsTask();
            var readTimeoutTransferredToDrain = false;
            // The wait ends on a caller cancel OR a model teardown; the read's own token
            // stays the response timeout, so the catch filters below tell the three apart.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, teardownCts.Token);

            string? responseLine;
            try
            {
                responseLine = await responseReadTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (teardownCts.IsCancellationRequested)
            {
                // DisposeModel is unloading the model (#1562). End this request on purpose:
                // the daemon is mid-decode and cannot read a quit until it finishes, so kill
                // it now. The read then ends, this request releases the lock, and teardown
                // takes the lock normally instead of disposing the streams under a live
                // read. The outer catch turns this into Cancelled, never DaemonCrashed, so
                // TranscribeAsync does not auto-restart the old model.
                LoggingService.Info("ParakeetTranscriptionService: Model teardown ended the in-flight transcription; killing daemon");
                _ = ObserveInFlightReadAsync(responseReadTask);
                StopDaemonInstance(daemonProcess);
                throw;
            }
            catch (OperationCanceledException) when (readTimeoutCts.IsCancellationRequested)
            {
                // Timeout — kill the daemon
                LoggingService.Error($"ParakeetTranscriptionService: Transcription timed out after {responseSeconds} seconds");
                _ = ObserveInFlightReadAsync(responseReadTask);
                StopDaemonInstance(daemonProcess);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonTimeout,
                    $"Parakeet daemon did not respond within {responseSeconds} seconds",
                    "Parakeet");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !readTimeoutCts.IsCancellationRequested)
            {
                // The request was already written to stdin, so the daemon will still produce
                // exactly one result line. Rather than SIGKILL the daemon — which forces a
                // 5-30s cold-start (model + DirectML reload) on the next recording — drain and
                // discard that in-flight line so stdout stays aligned and the daemon survives.
                // Transfer _transcriptionLock ownership to the background drain: cancellation
                // returns to the caller immediately, while the next transcription still waits
                // until stdout is aligned.
                LoggingService.Info("ParakeetTranscriptionService: Transcription cancelled by caller, draining in-flight result in background to keep daemon alive");
                releaseLockInFinally = false;
                readTimeoutTransferredToDrain = true;
                // The drain holds the lock, so the next dictation waits on it. A long file's
                // budget can be hours (#1562); bound the drain to the engine floor so a
                // cancelled long job is killed instead of blocking the next request.
                var remainingBudget = TimeSpan.FromMilliseconds(
                    Math.Max(0, responseDeadlineTicks - Environment.TickCount64));
                var drainBudget = ComputeDrainBudget(ResponseFloorSeconds(_isQwen3, _isOnline), remainingBudget);
                readTimeoutCts.CancelAfter(drainBudget);
                StartInFlightDrain(responseReadTask, readTimeoutCts, teardownCts, daemonProcess);
                // Throw the standard cancellation shape so UI/API callers reach their
                // dedicated cancel handlers instead of showing a transcription error.
                throw new OperationCanceledException("Parakeet transcription was cancelled", cancellationToken);
            }
            finally
            {
                if (!readTimeoutTransferredToDrain)
                {
                    readTimeoutCts.Dispose();
                }
            }

            if (responseLine == null)
            {
                LoggingService.Error("ParakeetTranscriptionService: Daemon closed stdout during transcription (crashed?)");
                if (ReferenceEquals(_daemonProcess, daemonProcess))
                {
                    _isReady = false;
                }
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon closed stdout unexpectedly",
                    "Parakeet");
            }

            LoggingService.Debug($"ParakeetTranscriptionService: Received response: {DescribeDaemonResponse(responseLine)}");

            // STEP 3: Parse the JSON response
            string transcribedText;
            long durationMs = 0;

            try
            {
                using var responseDoc = JsonDocument.Parse(responseLine);
                var root = responseDoc.RootElement;

                // Check for error response from daemon
                if (root.TryGetProperty("error", out var errorProp))
                {
                    var errorMsg = errorProp.GetString() ?? "Unknown daemon error";
                    LoggingService.Error($"ParakeetTranscriptionService: Daemon returned error: {errorMsg}");
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonCrashed,
                        $"Parakeet daemon error: {errorMsg}",
                        "Parakeet");
                }

                transcribedText = root.TryGetProperty("text", out var textProp)
                    ? textProp.GetString() ?? ""
                    : "";

                if (root.TryGetProperty("duration_ms", out var durationProp))
                {
                    durationMs = durationProp.GetInt64();
                }
            }
            catch (JsonException ex)
            {
                LoggingService.Error($"ParakeetTranscriptionService: Failed to parse daemon response: {DescribeDaemonResponse(responseLine)}", ex);
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon returned invalid JSON response",
                    "Parakeet",
                    ex);
            }

            // Qwen3-only cleanup: repair truncated UTF-8 (U+FFFD) and collapse
            // decoder repetition loops. Also warn on wrong-script hallucinations.
            if (_isQwen3)
            {
                if (Qwen3TextPostProcessor.LooksLikeWrongScript(transcribedText, _lastLanguage))
                {
                    LoggingService.Warn($"ParakeetTranscriptionService: Qwen3 produced no CJK characters but language is '{_lastLanguage}' — possible wrong-script hallucination");
                }
                transcribedText = Qwen3TextPostProcessor.Clean(transcribedText);
            }

            stopwatch.Stop();

            // Log performance summary
            LoggingService.Info("========== PARAKEET TRANSCRIPTION COMPLETE ==========");
            LoggingService.Info($"  Characters: {transcribedText.Length}");
            LoggingService.Info($"  Daemon inference time: {durationMs}ms");
            LoggingService.Info($"  Total round-trip time: {stopwatch.ElapsedMilliseconds}ms");

            if (string.IsNullOrWhiteSpace(transcribedText))
            {
                LoggingService.Warn("ParakeetTranscriptionService: Transcription returned empty text — audio may be silent or unrecognizable");
            }

            return transcribedText;
        }
        catch (Exception ex) when (IsEndedByTeardown(
            ex,
            teardownCts.IsCancellationRequested || IsStaleTeardownGeneration(teardownGeneration, CurrentTeardownGeneration),
            cancellationToken.IsCancellationRequested))
        {
            // Teardown cancelled (or began after) this request. Whatever ended it (the kill
            // above, or a stream disposed if teardown ever had to give up on the lock),
            // report it as Cancelled so the auto-restart never undoes the teardown.
            throw CreateTeardownCancelledException(ex);
        }
        finally
        {
            EndActiveRequest(teardownCts);
            if (releaseLockInFinally)
            {
                _transcriptionLock.Release();
            }
        }
    }

    /// <summary>
    /// Drains and discards the single in-flight result line the daemon produces for a
    /// request that was cancelled by the caller after the request was sent to stdin.
    ///
    /// Decode is synchronous in the daemon, so the result arrives within normal inference
    /// time. Draining it keeps the stdout protocol aligned so the next transcription reads
    /// its own result instead of this stale one — letting the daemon survive cancellation
    /// instead of paying a 5-30s cold-start reload.
    ///
    /// MUST be called while holding <see cref="_transcriptionLock"/> so no other
    /// transcription races on the stdout stream. If the drain times out or the daemon has
    /// gone away, the daemon is force-killed so the next call reloads from a clean state
    /// rather than reading a desynced stdout line.
    /// </summary>
    private void StartInFlightDrain(Task<string?> inFlightReadTask, CancellationTokenSource drainCts, CancellationTokenSource requestTeardownCts, Process? daemonProcess)
    {
        lock (_drainSync)
        {
            // Hand over from "active request" to "drain" in one step under _drainSync, the
            // lock DisposeModel cancels under. Teardown therefore sees either the request
            // (and cancels requestTeardownCts, checked below) or the drain (and cancels it).
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestTeardownCts = null;
            }

            var drainTask = DrainInFlightResultAndReleaseLockAsync(inFlightReadTask, drainCts, daemonProcess);
            _inFlightDrainCts = drainCts;
            _inFlightDrainTask = drainTask;
            if (drainTask.IsCompleted)
            {
                _inFlightDrainCts = null;
                _inFlightDrainTask = null;
            }
            else if (requestTeardownCts.IsCancellationRequested)
            {
                // Teardown cancelled the request after its caller did: drain no longer
                // needed — the daemon is going away — so end it now.
                _ = drainCts.CancelAsync();
            }
        }
    }

    /// <summary>
    /// Registers the request that now holds _transcriptionLock so DisposeModel can end it.
    /// Its budget starts at the floor and is raised once the audio length is known.
    /// </summary>
    private CancellationTokenSource BeginActiveRequest()
    {
        var cts = new CancellationTokenSource();
        lock (_drainSync)
        {
            _activeRequestTeardownCts = cts;
            _activeRequestBudget = TimeSpan.FromSeconds(ResponseFloorSeconds(_isQwen3, _isOnline));
        }
        return cts;
    }

    private void SetActiveRequestBudget(CancellationTokenSource requestTeardownCts, TimeSpan budget)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestBudget = budget;
            }
        }
    }

    /// <summary>
    /// Unregisters the request. The CTS is not disposed: it has no timer and no wait
    /// handle, and DisposeModel may still be running its CancelAsync callbacks.
    /// </summary>
    private void EndActiveRequest(CancellationTokenSource requestTeardownCts)
    {
        lock (_drainSync)
        {
            if (ReferenceEquals(_activeRequestTeardownCts, requestTeardownCts))
            {
                _activeRequestTeardownCts = null;
            }
        }
    }

    /// <summary>The budget of the request holding the lock, or null when none is.</summary>
    private TimeSpan? SnapshotActiveRequestBudget()
    {
        lock (_drainSync)
        {
            return _activeRequestTeardownCts != null ? _activeRequestBudget : null;
        }
    }

    /// <summary>
    /// Cancels whatever holds the lock on someone's behalf: the in-flight request, or an
    /// idle reload (#1608 review), if any. CancelAsync flips the token at once but runs
    /// the continuation on the thread pool, never inline on this (UI) thread under
    /// _drainSync.
    /// </summary>
    private void CancelActiveRequest(string reason)
    {
        lock (_drainSync)
        {
            if (_activeRequestTeardownCts is { IsCancellationRequested: false } cts)
            {
                LoggingService.Info($"ParakeetTranscriptionService: Cancelling the in-flight transcription for {reason}");
                _ = cts.CancelAsync();
            }

            if (_idleReloadTeardownCts is { IsCancellationRequested: false } reloadCts)
            {
                LoggingService.Info($"ParakeetTranscriptionService: Cancelling the idle model reload for {reason}");
                _ = reloadCts.CancelAsync();
            }
        }
    }

    private async Task DrainInFlightResultAndReleaseLockAsync(Task<string?> inFlightReadTask, CancellationTokenSource drainCts, Process? daemonProcess)
    {
        try
        {
            // Bounded timeout that is NOT linked to the (already-cancelled) caller token, so the
            // original stdout read is not cancelled immediately. The result should arrive within
            // inference time; this just guards against a wedged daemon.
            try
            {
                var drained = await inFlightReadTask.ConfigureAwait(false);
                if (drained == null)
                {
                    // Daemon closed stdout (crashed or exited) — clean up so the next call reloads.
                    LoggingService.Warn("ParakeetTranscriptionService: Daemon closed stdout while draining cancelled result; resetting");
                    StopDaemonInstance(daemonProcess);
                }
                else
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Drained in-flight result after cancellation; daemon kept alive");
                }
            }
            catch (Exception ex)
            {
                // Timeout or stream error — fall back to killing the daemon to guarantee the
                // stdout protocol is aligned for the next transcription.
                LoggingService.Warn($"ParakeetTranscriptionService: Failed to drain in-flight result ({ex.Message}); killing daemon to stay aligned");
                StopDaemonInstance(daemonProcess);
            }
        }
        finally
        {
            try
            {
                _transcriptionLock.Release();
            }
            catch (ObjectDisposedException ex)
            {
                LoggingService.Debug($"ParakeetTranscriptionService: Drain completed after transcription lock disposal: {ex.Message}");
            }

            lock (_drainSync)
            {
                if (ReferenceEquals(_inFlightDrainCts, drainCts))
                {
                    _inFlightDrainCts = null;
                    _inFlightDrainTask = null;
                }
            }

            drainCts.Dispose();
        }
    }

    private static async Task ObserveInFlightReadAsync(Task<string?> inFlightReadTask)
    {
        try
        {
            await inFlightReadTask.ConfigureAwait(false);
        }
        catch
        {
            // The daemon is being killed on timeout; this only observes late read faults.
        }
    }

    private void CancelAndWaitForInFlightDrain(string reason)
    {
        Task? drainTask;
        CancellationTokenSource? drainCts;
        lock (_drainSync)
        {
            drainTask = _inFlightDrainTask;
            drainCts = _inFlightDrainCts;
        }

        if (drainTask == null || drainTask.IsCompleted)
        {
            return;
        }

        LoggingService.Debug($"ParakeetTranscriptionService: Cancelling in-flight drain before {reason}");
        try
        {
            drainCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Drain completed between the state snapshot and cancellation.
        }

        try
        {
            if (!drainTask.Wait(TimeSpan.FromSeconds(1)))
            {
                LoggingService.Warn($"ParakeetTranscriptionService: In-flight drain did not finish before {reason}; continuing teardown");
            }
        }
        catch (AggregateException ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: In-flight drain ended during {reason}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // =========================================================================
    // DAEMON LIFECYCLE
    // =========================================================================

    /// <summary>
    /// Handles the daemon process exiting unexpectedly (crash detection).
    /// Sets _isReady to false so subsequent transcription attempts will fail fast
    /// or trigger auto-restart.
    /// </summary>
    private void OnDaemonExited(object? sender, EventArgs e)
    {
        var exitCode = -1;
        try
        {
            exitCode = _daemonProcess?.ExitCode ?? -1;
        }
        catch
        {
            // Process may already be disposed
        }

        _isReady = false;
        LoggingService.Warn($"ParakeetTranscriptionService: Daemon process exited unexpectedly (exit code: {exitCode})");
    }

    /// <summary>
    /// Starts a background thread that reads stderr from the daemon and logs
    /// each line as debug output. This captures diagnostic messages from the
    /// C++ engine without blocking the main communication channel.
    /// </summary>
    private static StderrCapture StartStderrReader(Process process)
    {
        var capture = new StderrCapture();
        _ = ReadStderrAsync(process.StandardError, capture);
        return capture;
    }

    private static async Task ReadStderrAsync(StreamReader stderrReader, StderrCapture capture)
    {
        try
        {
            while (true)
            {
                var line = await stderrReader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break; // Stream closed

                capture.Add(line);
                LoggingService.Debug($"ParakeetTranscriptionService [stderr]: {line}");
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected when process is disposed during shutdown
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"ParakeetTranscriptionService: Stderr reader stopped: {ex.Message}");
        }
        finally
        {
            capture.Complete();
        }
    }

    /// <summary>
    /// The daemon's first stderr lines (its startup diagnostics), kept so a startup
    /// error can be classified by its cause (#1598). Bounded: a long-lived daemon's
    /// later lines are only logged.
    /// </summary>
    private sealed class StderrCapture
    {
        private const int MaxLines = 200;
        private readonly List<string> _lines = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(string line)
        {
            lock (_lines)
            {
                if (_lines.Count < MaxLines) _lines.Add(line);
            }
        }

        public void Complete() => _ended.TrySetResult();

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }

        /// <summary>Waits until stderr reaches its end, or the timeout passes.</summary>
        public async Task WaitForEndAsync(TimeSpan timeout)
        {
            try
            {
                await _ended.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Classify on what arrived; a missing cause means "not marked".
            }
        }
    }

    /// <summary>
    /// Forcefully kills the daemon process if it is still running.
    /// Used during timeout and error recovery scenarios.
    /// </summary>
    private void KillDaemonProcess() => KillDaemonProcess(_daemonProcess);

    /// <summary>
    /// The exit code of a daemon that closed stdout before READY, once it has
    /// exited (waits up to 1 s), or null when it is still running or unreadable.
    /// </summary>
    private static int? TryGetEarlyExitCode(Process? process)
    {
        try
        {
            if (process == null) return null;
            return process.WaitForExit(1000) ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The error text the parakeet-engine daemon sends as its startup
    /// <c>{"status":"error"}</c> line when <c>EngineSession.Create</c> throws, i.e.
    /// when no recognizer could be built from the model directory
    /// (tools/parakeet-engine/main.cpp, the x64 build: the sherpa-onnx C API returned
    /// null; tools/parakeet-engine-dotnet/Program.cs, the ARM64 build: any exception).
    /// </summary>
    internal const string DaemonModelLoadError = "Failed to load model";

    /// <summary>
    /// True when the daemon's startup error says it could not load the model's own
    /// files (#1598), so a re-download can fix it. Its other startup error,
    /// "Invalid arguments", is the app's fault and does not count.
    /// </summary>
    internal static bool IsModelLoadErrorResponse(string? error) =>
        string.Equals(error?.Trim(), DaemonModelLoadError, StringComparison.Ordinal);

    /// <summary>
    /// Stderr text that names a fault in one of the model's own files, written by
    /// sherpa-onnx's config check (a model file that "does not exist") or by ONNX
    /// Runtime when it cannot parse a model (#1598). Counts only on a line that also
    /// names the model directory, so the shipped silero_vad.onnx never counts.
    /// </summary>
    private static readonly string[] ModelFileFaultMarkers =
    {
        "does not exist",
        "Protobuf parsing failed",
        "INVALID_PROTOBUF",
        "INVALID_GRAPH",
        "No graph was found in the protobuf",
        "Load model from",
    };

    /// <summary>
    /// The message of the SEHException .NET raises when sherpa-onnx's native code
    /// throws (ONNX Runtime rejecting a damaged model) inside the recognizer
    /// constructor. The .NET engine (tools/parakeet-engine-dotnet, the ARM64 build)
    /// logs it with no path, so it counts on its own.
    /// </summary>
    private const string NativeLoadExceptionMarker = "External component has thrown an exception";

    /// <summary>
    /// True when the daemon's startup error is "Failed to load model" AND its stderr
    /// names a fault in this model's files (#1598), so a re-download can fix it.
    /// The daemon sends the same line for every failure to build the recognizer: a
    /// DllNotFoundException or TypeInitializationException for sherpa-onnx or
    /// onnxruntime, OutOfMemoryException, or a DirectML and CPU provider that both
    /// fail on a valid model. None of those name a model file on stderr, so none mark
    /// the model broken. "Invalid arguments" and the Nemotron validation errors
    /// (a pinned-size download already rules out a truncated vocab) never count.
    /// </summary>
    internal static bool IsModelFileLoadFailure(string? error, IReadOnlyList<string>? stderrLines, string? modelDirectory)
    {
        if (!IsModelLoadErrorResponse(error) || stderrLines == null) return false;

        var directory = NormalizeForMatch(modelDirectory);
        foreach (var raw in stderrLines)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (raw.Contains(NativeLoadExceptionMarker, StringComparison.OrdinalIgnoreCase)) return true;
            if (directory.Length == 0) continue;

            var line = NormalizeForMatch(raw);
            if (!line.Contains(directory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var marker in ModelFileFaultMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    private static string NormalizeForMatch(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// True only for 0xC0000409, the fail-fast exit of a daemon whose ONNX Runtime
    /// threw on a damaged model file while it loaded the model (#1598: a truncated
    /// encoder.int8.onnx). Every other pre-READY exit (a missing DLL, a DLL init
    /// failure, an access violation from a GPU driver, OOM, a kill, an argument
    /// error, or a daemon still alive) is not something a re-download can fix, so
    /// it does not mark the model broken.
    /// </summary>
    internal static bool IsModelLoadCrashExitCode(int? exitCode) =>
        exitCode is { } code && unchecked((uint)code) == 0xC0000409;

    private static string FormatExitCode(int? exitCode) =>
        exitCode is { } code ? $"0x{unchecked((uint)code):X8}" : "unknown (still running)";

    private static void KillDaemonProcess(Process? process)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                LoggingService.Debug($"ParakeetTranscriptionService: Killing daemon process (PID: {process.Id})");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Failed to kill daemon process: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills the daemon instance a request (or its drain) captured, and clears
    /// _isReady only while that instance is still the current daemon. If a teardown
    /// already replaced it with a new model's daemon, the new one is left alone (#1562).
    /// </summary>
    private void StopDaemonInstance(Process? process)
    {
        if (ReferenceEquals(_daemonProcess, process))
        {
            _isReady = false;
        }

        KillDaemonProcess(process);
    }

    // =========================================================================
    // MODEL DISPOSAL
    // =========================================================================

    /// <summary>
    /// Gracefully shuts down the daemon process and cleans up resources.
    ///
    /// SHUTDOWN PROTOCOL:
    /// 1. Send {"command":"quit"} to stdin (graceful shutdown)
    /// 2. Wait up to 3 seconds for process to exit
    /// 3. If still running, force-kill the process
    /// 4. Clean up streams and process handle
    /// </summary>
    public void DisposeModel() => DisposeModelCore(advanceTeardownGeneration: true);

    /// <summary>
    /// The housekeeping unload (#1544): stops the daemon only when nobody is using it,
    /// and never waits for or cancels a job. <see cref="DisposeModel"/> is the user's own
    /// teardown; this is for "the selected mode no longer needs Parakeet", which a mode
    /// write by the Local API or the GUI re-runs while a Local API job may be on the
    /// daemon. That caller must not block its thread for the job (seconds to minutes),
    /// and must not end someone else's transcription to free memory.
    /// <para>
    /// The lock is tried without waiting and the pending count checked under
    /// <see cref="_drainSync"/> in the same step as the generation advance, exactly as
    /// <see cref="TryReserveIdleReload"/> does: a request or lease counted in before this
    /// keeps the daemon; one counted in after belongs to the new generation, waits on the
    /// lock, and finds the daemon gone (a lease-less request then auto-restarts it).
    /// </para>
    /// </summary>
    /// <returns>False when the daemon is busy and was left running.</returns>
    public bool TryDisposeModelIfIdle()
    {
        if (!_transcriptionLock.Wait(0))
        {
            return false;
        }

        try
        {
            lock (_drainSync)
            {
                if (_pendingRequests > 0)
                {
                    return false;
                }

                Interlocked.Increment(ref _teardownGeneration);
            }

            DisposeModelCore(advanceTeardownGeneration: false, transcriptionLockHeld: true);
            return true;
        }
        finally
        {
            _transcriptionLock.Release();
        }
    }

    /// <param name="transcriptionLockHeld">
    /// The caller (<see cref="ReloadWhenIdleAsync"/>) already holds the lock with no request
    /// pending, so there is nothing to cancel or wait for, and the lock stays the caller's.
    /// </param>
    /// <param name="idleReloadCancellation">
    /// The idle reload's teardown token: a GUI teardown cuts the old daemon's graceful-exit
    /// wait short (it is force-killed), so the reload hands the lock back at once.
    /// </param>
    private void DisposeModelCore(
        bool advanceTeardownGeneration,
        bool transcriptionLockHeld = false,
        CancellationToken idleReloadCancellation = default)
    {
        // FIRST, before any cancel or wait: every request that entered TranscribeAsync
        // before this point is now stale. One still queued on the lock fails as Cancelled
        // when it gets it, instead of finding the daemon gone and auto-restarting the old
        // model (#1562 review). Only the auto-restart's own reload skips this.
        if (advanceTeardownGeneration)
        {
            Interlocked.Increment(ref _teardownGeneration);
        }

        // Mark the provider unavailable BEFORE waiting on the lock. IsAvailable keys off
        // _isReady, so clearing it here closes the window where — during the up-to-65s
        // wait below — the Local API path (TranscriptionOrchestrator.TranscribeLocalAsync)
        // could still see IsAvailable == true and queue another Parakeet request behind
        // the in-flight transcription. Such a queued request would resume after teardown,
        // hit DaemonCrashed, and trigger an auto-restart that undoes this disposal / mode
        // switch. A transcription already past its IsAvailable guard holds the lock; the
        // wait below either lets it finish (a short clip) or cancels it as Cancelled. An
        // idle reload holding the lock has no grace (no active request budget) and is
        // cancelled at once (#1608 review).
        _isReady = false;
        if (!transcriptionLockHeld)
        {
            CancelAndWaitForInFlightDrain("model disposal");
        }

        // Serialize teardown against an in-flight TranscribeInternalAsync (which holds
        // this same lock while awaiting ReadLineAsync/WriteLineAsync on the stdio streams).
        // Without this, a re-init — e.g. a mode switch, file transcription, or retry on
        // the UI thread — could dispose the StreamReader/StreamWriter and the daemon
        // process out from under a transcription running on a thread-pool thread (the
        // provider is a process-wide singleton shared with the Local API server),
        // surfacing as an ObjectDisposedException / NullReferenceException.
        //
        // This runs on the UI thread (mode switch), so the total wait never exceeds the
        // per-engine response FLOOR (60 / 120 / 180 s) plus 5 s, as on main. Since #1562 a
        // request's own budget can be hours, so teardown must not simply wait the request
        // out and then dispose the streams under its live read: the read would fail as
        // DaemonCrashed and TranscribeAsync would auto-restart the OLD model, undoing the
        // mode switch and re-running the whole file. Instead:
        //   1. A short in-flight request (budget up to 2x the floor) gets up to the floor
        //      to finish on its own, as every request did on main.
        //   2. Otherwise (or if it has not finished by then) teardown cancels it. The
        //      request kills the daemon, fails as Cancelled (never DaemonCrashed, so no
        //      auto-restart), and releases the lock within milliseconds.
        //   3. Teardown takes the lock for the rest of the floor + 5 s. Only a request
        //      that ignores the cancel runs that out; teardown then proceeds rather than
        //      deadlock, and that request still reports Cancelled.
        var lockTaken = false;
        if (!transcriptionLockHeld)
        {
            var floorSeconds = ResponseFloorSeconds(_isQwen3, _isOnline);
            var teardownClock = Stopwatch.StartNew();
            var teardownWait = TeardownLockWait(floorSeconds);
            var grace = ComputeTeardownGrace(floorSeconds, SnapshotActiveRequestBudget());
            lockTaken = grace > TimeSpan.Zero && _transcriptionLock.Wait(grace);
            if (!lockTaken)
            {
                CancelActiveRequest("model disposal");
                // A caller cancel may have handed the request to a drain meanwhile.
                CancelAndWaitForInFlightDrain("model disposal");
                // Wait in short slices and re-cancel each time, so a request that took the
                // lock just before it registered itself is cancelled too.
                while (true)
                {
                    var remaining = teardownWait - teardownClock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        LoggingService.Warn($"ParakeetTranscriptionService: Transcription lock not released within {teardownWait.TotalSeconds:F0}s; tearing down anyway");
                        break;
                    }

                    lockTaken = _transcriptionLock.Wait(remaining < TeardownCancelSlice ? remaining : TeardownCancelSlice);
                    if (lockTaken)
                    {
                        break;
                    }

                    CancelActiveRequest("model disposal");
                }
            }
        }
        try
        {
            // Step 1: Send quit command if daemon is running
            if (_daemonProcess != null && !_daemonProcess.HasExited && _stdinWriter != null)
            {
                try
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Sending quit command to daemon...");
                    var quitCommand = JsonSerializer.Serialize(new { command = "quit" }, s_requestJsonOptions);
                    _stdinWriter.WriteLine(quitCommand);
                    _stdinWriter.Flush();
                }
                catch (Exception ex)
                {
                    LoggingService.Debug($"ParakeetTranscriptionService: Failed to send quit command: {ex.Message}");
                }
            }

            // Step 2: Wait for graceful exit
            if (_daemonProcess != null && !_daemonProcess.HasExited)
            {
                LoggingService.Debug("ParakeetTranscriptionService: Waiting for daemon to exit (3s timeout)...");
                var exited = WaitForDaemonExit(_daemonProcess, TimeSpan.FromSeconds(3), idleReloadCancellation);

                if (!exited)
                {
                    // Step 3: Force kill if still running
                    LoggingService.Warn("ParakeetTranscriptionService: Daemon did not exit gracefully, force-killing...");
                    KillDaemonProcess();
                }
                else
                {
                    LoggingService.Debug("ParakeetTranscriptionService: Daemon exited gracefully");
                }
            }

            // Step 4: Clean up resources using SafeDispose pattern
            if (_stdinWriter != null)
            {
                try { _stdinWriter.Dispose(); } catch (Exception ex) { LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose stdin writer: {ex.Message}"); }
                _stdinWriter = null;
            }

            if (_stdoutReader != null)
            {
                try { _stdoutReader.Dispose(); } catch (Exception ex) { LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose stdout reader: {ex.Message}"); }
                _stdoutReader = null;
            }

            if (_daemonProcess != null)
            {
                try
                {
                    _daemonProcess.Exited -= OnDaemonExited;
                    _daemonProcess.Dispose();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose daemon process: {ex.Message}");
                }
                _daemonProcess = null;
            }

            _loadedModelId = null;
            _activeProvider = null;

            LoggingService.Debug("ParakeetTranscriptionService: Model disposed and daemon stopped");
        }
        finally
        {
            if (lockTaken) _transcriptionLock.Release();
        }
    }

    /// <summary>
    /// Waits for the daemon to exit, up to <paramref name="timeout"/>. With a cancellable
    /// token (an idle reload's) it waits in short slices and gives up as soon as the token
    /// is cancelled, so a GUI teardown is not held for the graceful-exit pause.
    /// </summary>
    private static bool WaitForDaemonExit(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return process.WaitForExit(timeout);
        }

        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (process.WaitForExit(remaining < IdleReloadPollInterval ? remaining : IdleReloadPollInterval))
            {
                return true;
            }
        }

        return process.HasExited;
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    /// <summary>
    /// Disposes the service, shutting down the daemon and releasing all resources.
    /// </summary>
    public void Dispose()
    {
        if (_isShared)
        {
            // Process-wide singleton via TranscriptionRuntime — the GUI and
            // API server share this. Disposing would kill the daemon out from
            // under the other consumer. Caller probably meant DisposeModel().
            LoggingService.Debug("ParakeetTranscriptionService: Dispose() called on shared instance — ignoring (use DisposeModel)");
            return;
        }
        LoggingService.Info("ParakeetTranscriptionService: Disposing...");

        DisposeModel();

        try { _transcriptionLock.Dispose(); }
        catch (Exception ex) { LoggingService.Warn($"ParakeetTranscriptionService: Failed to dispose transcription lock: {ex.Message}"); }

        GC.SuppressFinalize(this);
    }
}
