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
using HyperWhisper.Models;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// Transcription provider that delegates to an external parakeet-engine.exe daemon process.
/// Communicates via stdin/stdout JSON lines protocol.
///
/// The daemon is a C++ process that loads ONNX Parakeet TDT models and performs
/// speech-to-text transcription using either DirectML (GPU) or CPU backends.
/// </summary>
public class ParakeetTranscriptionService : ITranscriptionProvider, IDisposable
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
    /// Null if no model is loaded.
    /// </summary>
    public string? LoadedLanguage => _lastLanguage;

    /// <summary>
    /// Whether the daemon is initialized and ready. Alias for compatibility with existing patterns.
    /// </summary>
    public bool IsInitialized => _isReady;

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
    public async Task InitializeAsync(string modelDirectory, string? language)
    {
        LoggingService.Info("========== INITIALIZING PARAKEET TRANSCRIPTION SERVICE ==========");
        LoggingService.Info($"  Model Directory: {modelDirectory}");

        // Dispose any existing daemon first
        DisposeModel();

        var daemonPath = GetDaemonPath();
        var vadModelPath = GetVadModelPath();

        // Guard: validate daemon binary exists
        if (!File.Exists(daemonPath))
        {
            LoggingService.Error($"ParakeetTranscriptionService: Daemon binary not found at {daemonPath}");
            throw new TranscriptionException(
                TranscriptionErrorCode.DaemonStartFailed,
                $"Parakeet engine not found at {daemonPath}",
                "Parakeet");
        }

        // Guard: validate model directory exists
        if (!Directory.Exists(modelDirectory))
        {
            LoggingService.Error($"ParakeetTranscriptionService: Model directory not found at {modelDirectory}");
            throw new TranscriptionException(
                TranscriptionErrorCode.OnnxModelFileMissing,
                $"Model directory not found at {modelDirectory}",
                "Parakeet");
        }

        // Guard: validate VAD model exists
        if (!File.Exists(vadModelPath))
        {
            LoggingService.Warn($"ParakeetTranscriptionService: VAD model not found at {vadModelPath}, proceeding without VAD");
        }

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

            LoggingService.Debug($"ParakeetTranscriptionService: Command: {daemonPath} {string.Join(" ", startInfo.ArgumentList)}");

            _daemonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Register crash detection before starting
            _daemonProcess.Exited += OnDaemonExited;

            if (!_daemonProcess.Start())
            {
                LoggingService.Error("ParakeetTranscriptionService: Failed to start daemon process");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    "Failed to start parakeet-engine process",
                    "Parakeet");
            }

            _stdinWriter = _daemonProcess.StandardInput;
            _stdoutReader = _daemonProcess.StandardOutput;

            LoggingService.Info($"  Daemon PID: {_daemonProcess.Id}");

            // STEP 2: Start background stderr reader for diagnostics
            LoggingService.Debug("Step 2: Starting stderr reader thread...");
            StartStderrReader(_daemonProcess);

            // STEP 3: Wait for READY signal on stdout.
            // Qwen3 loads ~1.2 GB of ONNX sessions on a cold cache — give it longer.
            var readySeconds = _isQwen3 ? 90 : (_isOnline ? 45 : 30);
            LoggingService.Info($"Step 3: Waiting for daemon READY signal ({readySeconds}s timeout)...");

            var readyTimeout = TimeSpan.FromSeconds(readySeconds);
            using var readyCts = new CancellationTokenSource(readyTimeout);

            try
            {
                var readLineTask = _stdoutReader.ReadLineAsync(readyCts.Token);
                var line = await readLineTask;

                if (line == null)
                {
                    LoggingService.Error("ParakeetTranscriptionService: Daemon closed stdout before sending READY");
                    KillDaemonProcess();
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        "Parakeet daemon closed stdout before sending READY signal",
                        "Parakeet");
                }

                LoggingService.Debug($"ParakeetTranscriptionService: Received from daemon: {line}");

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
                    KillDaemonProcess();
                    throw new TranscriptionException(
                        TranscriptionErrorCode.DaemonStartFailed,
                        $"Parakeet daemon failed to initialize: {errorMsg}",
                        "Parakeet");
                }
            }
            catch (OperationCanceledException)
            {
                LoggingService.Error($"ParakeetTranscriptionService: Daemon did not send READY within {readySeconds} seconds");
                KillDaemonProcess();
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonStartFailed,
                    $"Parakeet daemon timed out waiting for READY signal ({readySeconds}s)",
                    "Parakeet");
            }
            catch (JsonException ex)
            {
                LoggingService.Error("ParakeetTranscriptionService: Failed to parse daemon READY response", ex);
                KillDaemonProcess();
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
        catch (Exception ex)
        {
            LoggingService.Error("ParakeetTranscriptionService: Unexpected error during initialization", ex);
            KillDaemonProcess();
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
        // Guard: validate audio file exists
        if (!File.Exists(audioPath))
        {
            LoggingService.Error($"ParakeetTranscriptionService: Audio file not found: {audioPath}");
            throw new TranscriptionException(
                TranscriptionErrorCode.AudioFileNotFound,
                $"Audio file not found: {audioPath}",
                "Parakeet");
        }

        // Log warning for vocabulary — Parakeet TDT does not support it
        if (vocabulary != null && vocabulary.Count > 0)
        {
            LoggingService.Warn("ParakeetTranscriptionService: Parakeet TDT does not support vocabulary boosting — vocabulary will be ignored");
        }

        try
        {
            return await TranscribeInternalAsync(audioPath, cancellationToken);
        }
        catch (TranscriptionException ex) when (ex.Code == TranscriptionErrorCode.DaemonCrashed)
        {
            // Auto-restart: attempt to restart daemon and retry once
            LoggingService.Warn("ParakeetTranscriptionService: Daemon crashed during transcription, attempting auto-restart...");

            if (_lastModelDirectory == null)
            {
                LoggingService.Error("ParakeetTranscriptionService: Cannot auto-restart — no previous model directory");
                throw;
            }

            try
            {
                await InitializeAsync(_lastModelDirectory, _lastLanguage);
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart successful, retrying transcription...");
                return await TranscribeInternalAsync(audioPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LoggingService.Info("ParakeetTranscriptionService: Auto-restart retry cancelled by caller");
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
    /// Internal transcription implementation that handles the stdio protocol.
    /// Separated from TranscribeAsync to allow retry logic in the caller.
    /// </summary>
    private async Task<string> TranscribeInternalAsync(string audioPath, CancellationToken cancellationToken)
    {
        // Acquire the transcription lock — stdio is serial
        await _transcriptionLock.WaitAsync(cancellationToken);
        var releaseLockInFinally = true;

        try
        {
            // Guard: daemon must be ready
            if (!IsAvailable)
            {
                LoggingService.Error("ParakeetTranscriptionService: Daemon is not running or not ready");
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon is not running",
                    "Parakeet");
            }

            var stopwatch = Stopwatch.StartNew();
            LoggingService.Info("========== STARTING PARAKEET TRANSCRIPTION ==========");
            LoggingService.Info($"  Audio Path: {audioPath}");
            LoggingService.Info($"  Provider: {_activeProvider ?? "unknown"}");
            LoggingService.Info($"  Model: {_loadedModelId ?? "unknown"}");

            // Log audio file info
            var fileInfo = new FileInfo(audioPath);
            LoggingService.Info($"  Audio file size: {fileInfo.Length:N0} bytes");

            // STEP 1: Write the audio path to stdin as JSON
            var request = JsonSerializer.Serialize(new { audio_path = audioPath }, s_requestJsonOptions);
            LoggingService.Debug($"ParakeetTranscriptionService: Sending request: {request}");

            await _stdinWriter!.WriteLineAsync(request);
            await _stdinWriter.FlushAsync();

            // STEP 2: Read the response from stdout with a model-aware timeout.
            // Qwen3 decodes autoregressively (much slower than the Parakeet
            // transducer), so it gets a longer ceiling.
            var responseSeconds = _isQwen3 ? 180 : (_isOnline ? 120 : 60);
            LoggingService.Debug($"ParakeetTranscriptionService: Waiting for transcription response ({responseSeconds}s timeout)...");

            var responseTimeout = TimeSpan.FromSeconds(responseSeconds);
            var readTimeoutCts = new CancellationTokenSource(responseTimeout);
            var responseReadTask = _stdoutReader!.ReadLineAsync(readTimeoutCts.Token).AsTask();
            var readTimeoutTransferredToDrain = false;

            string? responseLine;
            try
            {
                responseLine = await responseReadTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (readTimeoutCts.IsCancellationRequested)
            {
                // Timeout — kill the daemon
                LoggingService.Error($"ParakeetTranscriptionService: Transcription timed out after {responseSeconds} seconds");
                _ = ObserveInFlightReadAsync(responseReadTask);
                _isReady = false;
                KillDaemonProcess();
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
                StartInFlightDrain(responseReadTask, readTimeoutCts);
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
                _isReady = false;
                throw new TranscriptionException(
                    TranscriptionErrorCode.DaemonCrashed,
                    "Parakeet daemon closed stdout unexpectedly",
                    "Parakeet");
            }

            LoggingService.Debug($"ParakeetTranscriptionService: Received response: {responseLine}");

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
                LoggingService.Error($"ParakeetTranscriptionService: Failed to parse daemon response: {responseLine}", ex);
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
        finally
        {
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
    private void StartInFlightDrain(Task<string?> inFlightReadTask, CancellationTokenSource drainCts)
    {
        lock (_drainSync)
        {
            var drainTask = DrainInFlightResultAndReleaseLockAsync(inFlightReadTask, drainCts);
            _inFlightDrainCts = drainCts;
            _inFlightDrainTask = drainTask;
            if (drainTask.IsCompleted)
            {
                _inFlightDrainCts = null;
                _inFlightDrainTask = null;
            }
        }
    }

    private async Task DrainInFlightResultAndReleaseLockAsync(Task<string?> inFlightReadTask, CancellationTokenSource drainCts)
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
                    _isReady = false;
                    KillDaemonProcess();
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
                _isReady = false;
                KillDaemonProcess();
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
    private void StartStderrReader(Process process)
    {
        var stderrReader = process.StandardError;

        Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await stderrReader.ReadLineAsync();
                    if (line == null) break; // Stream closed

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
        });
    }

    /// <summary>
    /// Forcefully kills the daemon process if it is still running.
    /// Used during timeout and error recovery scenarios.
    /// </summary>
    private void KillDaemonProcess()
    {
        try
        {
            if (_daemonProcess != null && !_daemonProcess.HasExited)
            {
                LoggingService.Debug($"ParakeetTranscriptionService: Killing daemon process (PID: {_daemonProcess.Id})");
                _daemonProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ParakeetTranscriptionService: Failed to kill daemon process: {ex.Message}");
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
    public void DisposeModel()
    {
        // Mark the provider unavailable BEFORE waiting on the lock. IsAvailable keys off
        // _isReady, so clearing it here closes the window where — during the up-to-65s
        // wait below — the Local API path (TranscriptionOrchestrator.TranscribeLocalAsync)
        // could still see IsAvailable == true and queue another Parakeet request behind
        // the in-flight transcription. Such a queued request would resume after teardown,
        // hit DaemonCrashed, and trigger an auto-restart that undoes this disposal / mode
        // switch. Any transcription already past its IsAvailable guard is unaffected — it
        // keeps running under the lock we are about to wait for.
        _isReady = false;
        CancelAndWaitForInFlightDrain("model disposal");

        // Serialize teardown against an in-flight TranscribeInternalAsync (which holds
        // this same lock while awaiting ReadLineAsync/WriteLineAsync on the stdio streams).
        // Without this, a re-init — e.g. a mode switch, file transcription, or retry on
        // the UI thread — could dispose the StreamReader/StreamWriter and the daemon
        // process out from under a transcription running on a thread-pool thread (the
        // provider is a process-wide singleton shared with the Local API server),
        // surfacing as an ObjectDisposedException / NullReferenceException.
        //
        // Size the wait off the SAME per-engine read budget that TranscribeInternalAsync
        // uses (_isQwen3 ? 180 : _isOnline ? 120 : 60), plus a small margin so a legitimate
        // in-flight transcription can finish and release the lock before we tear down. A
        // hard-coded 65s would expire mid-transcription for Qwen3 (180s) and Nemotron-online
        // (120s) models and proceed to dispose the streams/process out from under the running
        // read. If the wait still times out we proceed with teardown anyway rather than
        // deadlock — a genuinely hung transcription gets its daemon killed regardless.
        var teardownWaitSeconds = (_isQwen3 ? 180 : (_isOnline ? 120 : 60)) + 5;
        bool lockTaken = _transcriptionLock.Wait(TimeSpan.FromSeconds(teardownWaitSeconds));
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
                var exited = _daemonProcess.WaitForExit(TimeSpan.FromSeconds(3));

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
