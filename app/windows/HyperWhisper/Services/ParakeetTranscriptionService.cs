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
public partial class ParakeetTranscriptionService : ITranscriptionProvider, ILocalVocabularyCorrection, IDisposable
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

    /// <summary>How often a waiting teardown re-cancels the request holding the lock.</summary>
    private static readonly TimeSpan TeardownCancelSlice = TimeSpan.FromMilliseconds(250);

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

    /// <summary>
    /// Set when <see cref="TryDisposeModelIfIdle"/> found the daemon busy, so the last
    /// request or lease to leave unloads it (#1544). Cleared by a deliberate teardown or
    /// mode switch. Guarded by <see cref="_drainSync"/>.
    /// </summary>
    private bool _unloadWhenDrained;

    /// <summary>
    /// Asked again when a deferred unload runs: does the app still not need the daemon?
    /// The selected mode can change back to Parakeet while the job runs, and that daemon
    /// must then stay. Null means "still not needed". Guarded by <see cref="_drainSync"/>.
    /// </summary>
    private Func<bool>? _unloadWhenDrainedStillWanted;

    /// <summary>
    /// Counts every arm of <see cref="_unloadWhenDrained"/>, so a deferred unload that decided
    /// to keep the daemon does not clear an arm made after it asked. Guarded by <see cref="_drainSync"/>.
    /// </summary>
    private int _unloadArmCount;

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

    /// <summary>Test seam: the daemon binary to start. Null is the shipped engine.</summary>
    internal string? DaemonPathOverride { get; set; }

    /// <summary>
    /// Test seam: starts the daemon process. A test throws the Win32Exception that
    /// Application Control raises (#2379), which no test machine can raise for real.
    /// </summary>
    internal Func<Process, bool> StartDaemonProcess { get; set; } = static process => process.Start();

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
