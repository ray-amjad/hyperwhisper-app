using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HyperWhisper.Utilities;
using HyperWhisper.WhisperWorker;

namespace HyperWhisper.Services;

/// <summary>
/// TRANSCRIPTION SERVICE - LOCAL WHISPER
///
/// Purpose:
/// Local Whisper transcription. The model runs in a separate process,
/// HyperWhisper.WhisperWorker.exe (issue #1194), driven over stdio by
/// <see cref="WhisperWorkerClient"/>. Whisper.net's Vulkan backend keeps the GPU
/// awake for as long as the process that used it lives, so the worker exits
/// after five idle minutes and the GPU drops back to its idle state. No Whisper
/// or ggml DLL loads in the app process.
///
/// WHAT STAYS HERE:
/// Audio preparation (NAudio decode, mono, 16 kHz, trailing-silence trim, ~5 min
/// chunks), the decoding regime per file, language pinning across chunks, and the
/// text post-processing (non-speech markers, repetition loops). Each chunk goes
/// to the worker as a 16-bit WAV that Whisper.net reads back to the very samples
/// the in-process decoder used to get.
///
/// MODEL STATE:
/// <see cref="InitializeAsync"/> selects a model; it does not start the worker
/// (D5), so a launch-time load leaves the GPU idle. <see cref="IsInitialized"/>
/// means "a model is selected and can serve", and stays true across the worker's
/// idle exits. A Whisper recording warms the worker up
/// (<see cref="NotifyWhisperRecordingStarted"/>); any transcription starts it on
/// demand.
///
/// INTERFACE IMPLEMENTATION:
/// Implements ITranscriptionProvider for unified provider abstraction.
/// This allows MainViewModel to switch between local and cloud providers seamlessly.
/// </summary>
public class TranscriptionService : ITranscriptionProvider, IDisposable
{
    // =========================================================================
    // RUNTIME DETECTION
    // =========================================================================

    /// <summary>
    /// Detects if a discrete GPU is present that can accelerate transcription.
    /// With Vulkan support, this now includes NVIDIA, AMD, and Intel discrete GPUs.
    /// </summary>
    private static bool HasDiscreteGpu()
    {
        var gpu = GpuInfoService.GetBestGpu();
        if (gpu == null) return false;
        var name = gpu.Name.ToUpperInvariant();
        // NVIDIA (CUDA + Vulkan), AMD (Vulkan), Intel Arc (Vulkan)
        return name.Contains("NVIDIA") || name.Contains("GEFORCE") ||
               name.Contains("RTX") || name.Contains("GTX") ||
               name.Contains("RADEON") || name.Contains("AMD") ||
               name.Contains("ARC");
    }

    // =========================================================================
    // THE WORKER
    // =========================================================================

    /// <summary>The worker's folder under the app, as build-release.ps1 publishes it.</summary>
    internal const string WorkerFolderName = "whisper-worker";

    internal const string WorkerExeName = "HyperWhisper.WhisperWorker.exe";

    internal static string WorkerExePath =>
        Path.Combine(AppContext.BaseDirectory, WorkerFolderName, WorkerExeName);

    /// <summary>Where each chunk's WAV waits for the worker. Deleted after each request.</summary>
    internal static string TempWavDirectory =>
        Path.Combine(Path.GetTempPath(), "HyperWhisper", WorkerFolderName);

    private readonly WhisperWorkerClient _worker;

    /// <summary>
    /// The most <see cref="Dispose"/> blocks its caller (often the UI thread) on
    /// the worker's exit. The client kills a busy worker after its own shutdown
    /// timeout, so this is only reached when something is badly stuck.
    /// </summary>
    private static readonly TimeSpan WorkerDisposeTimeout = TimeSpan.FromSeconds(10);

    private bool _disposed;

    /// <summary>Held while a Whisper-mode recording runs, so the worker it warmed up stays.</summary>
    private IDisposable? _recordingHold;
    private readonly object _recordingHoldLock = new();

    private int _staleWavSweepDone;

    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>
    /// Whether a model is selected and can serve. The worker process may be down
    /// (it exits when idle); the next transcription starts it and loads again.
    /// </summary>
    public bool IsInitialized => LoadedModelPath != null;

    /// <summary>
    /// Held across <see cref="InitializeAsync"/> and <see cref="UnloadModelAsync"/>
    /// so they serialize against each other and against the in-flight wait.
    /// </summary>
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    /// <summary>
    /// Number of <see cref="TranscribeFileInternalAsync"/> calls currently
    /// running. <see cref="UnloadModelAsync"/> and a model switch wait for this to
    /// reach zero before the worker is stopped.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Set by <see cref="TranscriptionRuntime"/> for the process-wide singleton
    /// instance. When true, <see cref="Dispose"/> is a no-op — the API server
    /// and GUI share this instance; the worker exits with the app (stdin EOF).
    /// Direct <c>new TranscriptionService()</c> callers (e.g. benchmarks or
    /// tests) still dispose normally.
    /// </summary>
    private readonly bool _isShared;

    public TranscriptionService() : this(isShared: false) { }

    internal TranscriptionService(bool isShared)
        : this(isShared, new WhisperWorkerProcessLauncher(
            WorkerExePath,
            line => LoggingService.Info($"[whisper-worker] {line}")))
    {
    }

    internal TranscriptionService(bool isShared, IWhisperWorkerLauncher launcher, WhisperWorkerClientTimings? timings = null)
    {
        _isShared = isShared;
        _worker = new WhisperWorkerClient(
            launcher,
            info: message => LoggingService.Info(message),
            warn: message => LoggingService.Warn(message),
            timings: timings);
    }

    // =========================================================================
    // ITranscriptionProvider IMPLEMENTATION
    // =========================================================================

    /// <summary>
    /// Whether Whisper.net transcription is supported on this platform.
    /// Whisper.net requires x64 architecture.
    /// </summary>
    public bool IsSupported => PlatformHelper.SupportsWhisperTranscription;

    /// <summary>
    /// Whether the provider is ready to transcribe (model is loaded).
    /// Required by ITranscriptionProvider interface.
    /// </summary>
    public bool IsAvailable => IsSupported && IsInitialized;

    /// <summary>
    /// Display name including the loaded model.
    /// Required by ITranscriptionProvider interface.
    /// </summary>
    public string Name => LoadedModelPath != null
        ? $"Whisper {Path.GetFileNameWithoutExtension(LoadedModelPath).Replace("ggml-", "")}"
        : "Whisper (not loaded)";

    /// <summary>
    /// Path to the selected model. Set while the model can serve, whether or not
    /// the worker process is up right now.
    /// </summary>
    public string? LoadedModelPath { get; private set; }

    /// <summary>
    /// Name of the GPU being used for acceleration, or null if using CPU.
    /// </summary>
    public string? ActiveGpuName { get; private set; }

    /// <summary>
    /// Whether GPU acceleration is being used for transcription.
    /// </summary>
    public bool IsUsingGpu => ActiveGpuName != null;

    /// <summary>
    /// The backend for the log (#1932). The worker's last load says what really
    /// ran, which is the CPU when Vulkan fell back. Before a load, a CPU-only
    /// <c>HW_WHISPER_WORKER_RUNTIMES</c> (a diagnostics override the worker
    /// inherits) means the CPU even on a GPU machine. The model is still sent the
    /// GPU's device index; the CPU runtime ignores it.
    /// </summary>
    internal string DescribeBackend()
    {
        if (_worker.LoadResult is { } load)
        {
            return load.Runtime == "vulkan"
                ? $"GPU ({load.Gpu ?? ActiveGpuName ?? "Vulkan"})"
                : $"CPU (runtime {load.Runtime})";
        }
        if (!IsUsingGpu) return "CPU";
        var order = Environment.GetEnvironmentVariable(WorkerRuntimeOrderVariable);
        return IsCpuOnlyRuntimeOrder(order)
            ? $"CPU ({WorkerRuntimeOrderVariable}={order})"
            : $"GPU ({ActiveGpuName})";
    }

    /// <summary>The worker's runtime order override (its <c>WorkerRuntime.OrderVariable</c>).</summary>
    internal const string WorkerRuntimeOrderVariable = "HW_WHISPER_WORKER_RUNTIMES";

    /// <summary>
    /// True when the override names a CPU runtime and no GPU one. The worker
    /// ignores names it does not know and keeps its default order (Vulkan first)
    /// when none is left, so an order with no CPU name is not CPU-only.
    /// </summary>
    internal static bool IsCpuOnlyRuntimeOrder(string? order)
    {
        if (string.IsNullOrWhiteSpace(order)) return false;
        var parts = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasCpu = parts.Any(p => p.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
                                    || p.Equals("CpuNoAvx", StringComparison.OrdinalIgnoreCase));
        var hasGpu = parts.Any(p => p.Equals("Vulkan", StringComparison.OrdinalIgnoreCase)
                                    || p.Equals("Cuda", StringComparison.OrdinalIgnoreCase)
                                    || p.Equals("OpenVino", StringComparison.OrdinalIgnoreCase)
                                    || p.Equals("CoreML", StringComparison.OrdinalIgnoreCase));
        return hasCpu && !hasGpu;
    }

    /// <summary>
    /// The app is closing. The shared service is never disposed: the worker reads
    /// stdin EOF when this process ends and exits with it, and nothing is left to
    /// log that. Logged here, so the log tells a normal close from a worker that
    /// was killed (#1932; the client warns when a worker exits unasked).
    /// </summary>
    public void LogWorkerAtAppExit()
    {
        LoggingService.Info(_worker.WorkerProcessId is { } pid
            ? $"TranscriptionService: App closing; the Whisper worker (pid {pid}) exits with it"
            : "TranscriptionService: App closing; no Whisper worker is running");
    }

    /// <summary>
    /// Counter for transcriptions performed since service initialization.
    /// Used to log GPU diagnostics on first transcription.
    /// </summary>
    private int _transcriptionCount = 0;

    /// <summary>
    /// Tracks if we've logged GPU diagnostics for this session.
    /// Only logs once per session to avoid log spam.
    /// </summary>
    private bool _hasLoggedGpuDiagnostics = false;

    /// <summary>
    /// List of all available GPU adapters detected on the system.
    /// </summary>
    public string[] AvailableGpus { get; private set; } = [];

    // =========================================================================
    // INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Selects the Whisper model to serve.
    ///
    /// Checks that the file exists, detects the GPU and records the choice. It
    /// does NOT start the worker or read the model (D5, issue #1194): the worker
    /// loads it when a Whisper recording starts or a transcription needs it. A
    /// broken model file is therefore found at that first load, and marked
    /// through <see cref="LocalModelHealth"/> then (#1598).
    ///
    /// Switching to another model stops a worker that holds the old one, and
    /// waits for it to exit, so two processes never hold a model at once.
    /// </summary>
    /// <param name="modelPath">Path to the GGML model file (.bin).</param>
    /// <param name="progress">Optional progress callback (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(
        string modelPath,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // ARM64 guard - ensure we're on a supported platform
        if (!PlatformHelper.SupportsLocalTranscription)
        {
            LoggingService.Info($"Local transcription not supported on {PlatformHelper.ArchitectureName} - skipping initialization");
            return;
        }

        LoggingService.Info("========== INITIALIZING TRANSCRIPTION SERVICE ==========");
        LoggingService.Info($"  Model file: {Path.GetFileName(modelPath)}");

        // Serialize against UnloadModelAsync and concurrent InitializeAsync calls.
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that saw an unload pending queues here behind it (#1609).
            // If that unload was cancelled, the model it would have released is
            // still selected: keep it.
            if (IsInitialized
                && string.Equals(LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.Info("  Model already loaded while this load waited; keeping it");
                return;
            }

            // Wait for in-flight transcriptions to drain before the switch stops
            // the worker that serves them. Mirrors UnloadModelAsync.
            while (Volatile.Read(ref _inFlight) > 0)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            DisposeModel();

            // Validate model file exists
            if (!File.Exists(modelPath))
            {
                LoggingService.Error($"Model file not found: {Path.GetFileName(modelPath)}");
                await _worker.SelectModelAsync(null, CancellationToken.None).ConfigureAwait(false);
                throw new FileNotFoundException("Model file not found", modelPath);
            }

            LoggingService.Info($"  Model file size: {new FileInfo(modelPath).Length:N0} bytes");

            try
            {
                progress?.Invoke(0.0);

                LoggingService.Info("Step 1: Detecting GPU information...");
                var gpu = GpuInfoService.GetBestGpu();
                if (gpu != null)
                {
                    AvailableGpus = [gpu.Name];
                    LoggingService.Info($"  Best GPU: {gpu.Name} ({gpu.VramDisplay})");
                }
                else
                {
                    AvailableGpus = [];
                    LoggingService.Info("  No GPU detected, using CPU");
                }

                // On multi-GPU systems (e.g. Intel UHD + NVIDIA), Vulkan defaults to
                // device 0, which is often the integrated GPU. The DXGI adapter index
                // of the best GPU makes Vulkan target the discrete one. Flash
                // attention stays on (2-3x faster GPU inference).
                var gpuDevice = gpu?.AdapterIndex ?? 0;
                LoggingService.Info($"  Using GpuDevice={gpuDevice}" + (gpu != null ? $" ({gpu.Name})" : ""));

                LoggingService.Info("Step 2: Selecting the model; the Whisper worker loads it on first use");
                await _worker.SelectModelAsync(
                    new WhisperWorkerModel(modelPath, gpuDevice, FlashAttention: true, GpuName: gpu?.Name),
                    cancellationToken).ConfigureAwait(false);

                LoadedModelPath = modelPath;

                // With Vulkan, AMD and Intel GPUs can also accelerate transcription.
                ActiveGpuName = !PlatformHelper.IsArm64 && HasDiscreteGpu() ? gpu?.Name : null;

                progress?.Invoke(1.0);

                LoggingService.Info("Step 2: Model selected");
                LoggingService.Info($"  Compute Backend: {DescribeBackend()}");
                LoggingService.Info("========== TRANSCRIPTION SERVICE READY ==========");
            }
            catch (OperationCanceledException)
            {
                LoggingService.Warn("TranscriptionService: Initialization cancelled");
                DisposeModel();
                throw;
            }
            catch (Exception ex)
            {
                LoggingService.Error("TranscriptionService: Failed to initialize", ex);
                DisposeModel();
                throw;
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// True when a Whisper load failure is about the model file, not the native
    /// runtime (#1598): a new download fixes the first, never the second.
    ///
    /// Across the worker boundary that is the worker's <c>model_file</c> code;
    /// the worker sorts a missing, wrong-architecture or incomplete native DLL
    /// into <c>runtime</c> instead. For an in-process exception the old rule
    /// holds: a DllNotFoundException, BadImageFormatException or
    /// EntryPointNotFoundException inside means the runtime.
    /// </summary>
    internal static bool IsModelFileLoadFault(Exception ex)
    {
        if (ex is WhisperWorkerException workerException)
        {
            return workerException.IsModelFileFault;
        }

        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Releases the selected Whisper model: stops the worker (it is sent
    /// <c>shutdown</c>) and clears the selection. Waits for any in-flight
    /// transcription to finish first. After this returns, <see cref="IsAvailable"/>
    /// is false until <see cref="InitializeAsync"/> is called again.
    ///
    /// There is deliberately no synchronous wrapper. The wait can last as long
    /// as a whole file job (74-92 s on a 3-hour file), and a UI-thread caller
    /// that blocked on it froze the main window for that long (#1534). Await it.
    /// </summary>
    public async Task UnloadModelAsync(CancellationToken cancellationToken = default)
    {
        // Counted before the lock wait, so the model reads as going away for the
        // whole wait (#1609). A cancelled unload keeps the model.
        Interlocked.Increment(ref _pendingUnloads);
        try
        {
            await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (Volatile.Read(ref _inFlight) > 0)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
                await _worker.SelectModelAsync(null, cancellationToken).ConfigureAwait(false);
                DisposeModel();
            }
            finally
            {
                _modelLock.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingUnloads);
        }
    }

    /// <summary>
    /// Number of <see cref="UnloadModelAsync"/> calls that have not returned.
    /// </summary>
    private int _pendingUnloads;

    /// <summary>
    /// True while an <see cref="UnloadModelAsync"/> call is waiting or running
    /// (#1609). The wait can last a whole file job, and <see cref="IsInitialized"/>
    /// stays true through it, so a caller that saw only IsInitialized started new
    /// work on a model about to be released: the work queued on the model lock
    /// behind the unload, then failed EnsureInitialized.
    /// </summary>
    public bool IsUnloadPending => Volatile.Read(ref _pendingUnloads) > 0;

    /// <summary>
    /// True when <paramref name="modelPath"/> is loaded (any model when null)
    /// and no unload is pending: new work can rely on it (#1609).
    /// </summary>
    public bool IsLoadedAndStaying(string? modelPath = null) =>
        IsInitialized
        && !IsUnloadPending
        && (modelPath == null || LoadedModelPath == modelPath);

    // =========================================================================
    // KEEP-WARM (D5)
    // =========================================================================

    /// <summary>
    /// A Whisper-mode recording started. If the worker is down, start it and load
    /// the model now, in parallel with the recording, so most of the start and
    /// load cost hides behind the speech. The worker cannot idle out until
    /// <see cref="NotifyWhisperRecordingEnded"/>.
    /// </summary>
    public void NotifyWhisperRecordingStarted()
    {
        if (!IsSupported || !IsLoadedAndStaying()) return;

        lock (_recordingHoldLock)
        {
            _recordingHold ??= _worker.HoldWarm();
        }
        _ = WarmUpInBackgroundAsync();
    }

    /// <summary>The Whisper-mode recording ended (stopped or cancelled): the idle timer may run again.</summary>
    public void NotifyWhisperRecordingEnded()
    {
        IDisposable? hold;
        lock (_recordingHoldLock)
        {
            hold = _recordingHold;
            _recordingHold = null;
        }
        hold?.Dispose();
    }

    private async Task WarmUpInBackgroundAsync()
    {
        var modelPath = LoadedModelPath;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _worker.WarmUpAsync().ConfigureAwait(false);
            LoggingService.Info($"TranscriptionService: Whisper worker warm ({result.Runtime}"
                + (result.Gpu != null ? $", {result.Gpu}" : "") + $") after {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (WhisperWorkerException ex) when (ex.Code == WhisperWorkerErrorCodes.NotLoaded)
        {
            // The model was unloaded while the recording started; nothing to warm.
        }
        catch (Exception ex)
        {
            // The transcription after the recording tries again and reports it.
            LoggingService.Warn($"TranscriptionService: Whisper warm-up failed ({DescribeWorkerFailure(ex)})");
            MarkBrokenIfModelFileFault(ex, modelPath);
        }
    }

    // =========================================================================
    // FILE TRANSCRIPTION
    // =========================================================================

    /// <summary>
    /// Transcribes an audio file.
    ///
    /// SUPPORTED FORMATS:
    /// NAudio supports WAV, MP3, and other common formats.
    /// Audio is resampled to 16kHz mono as required by Whisper.
    ///
    /// TRANSCRIPTION PROCESS:
    /// 1. Prepare audio stream (resample to 16kHz mono if needed)
    /// 2. Pick the decoding regime and plan the chunks
    /// 3. Send each chunk to the Whisper worker and collect its text
    /// 4. Return combined text
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="language">Language code (e.g., "en", "ja"). Null for auto-detect.</param>
    /// <returns>Transcribed text.</returns>
    public string TranscribeFile(string audioPath, string? language = null)
    {
        // Go through TranscribeFileAsync so the body runs on a thread-pool thread with no
        // SynchronizationContext, so a UI-thread caller blocking on this wrapper
        // cannot deadlock on a continuation that wants the UI thread.
        return TranscribeFileAsync(audioPath, language)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<string> TranscribeFileInternalAsync(
        string audioPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        string modelPath;
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            modelPath = EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();

            // Mark in-flight so model unload/reload can wait. The model lock keeps
            // a new transcription from starting after the drain observes zero.
            Interlocked.Increment(ref _inFlight);
        }
        finally
        {
            _modelLock.Release();
        }

        try
        {

        // Overall timing for the entire transcription
        var totalStopwatch = Stopwatch.StartNew();
        var stepStopwatch = new Stopwatch();

        // Increment transcription counter
        _transcriptionCount++;

        LoggingService.Info("========== STARTING FILE TRANSCRIPTION ==========");
        LoggingService.Info($"  Transcription #: {_transcriptionCount}");
        LoggingService.Info($"  Audio file: {LoggingService.DescribePath(audioPath)}");
        LoggingService.Info($"  Language: {language ?? "auto-detect"}");
        LoggingService.Info($"  Backend: {DescribeBackend()}");
        LoggingService.Info($"  Model: {Path.GetFileName(modelPath)}");
        LoggingService.Info($"  Thread ID: {Environment.CurrentManagedThreadId}");

        // Log GPU diagnostics on first transcription of the session
        if (!_hasLoggedGpuDiagnostics)
        {
            _hasLoggedGpuDiagnostics = true;
            LoggingService.LogGpuDiagnostics();
        }

        if (!File.Exists(audioPath))
        {
            LoggingService.Error($"Audio file not found: {LoggingService.DescribePath(audioPath)}");
            throw new FileNotFoundException("Audio file not found", audioPath);
        }

        // Log detailed audio file information
        var fileInfo = new FileInfo(audioPath);
        LoggingService.Info($"  Audio file size: {fileInfo.Length:N0} bytes");

        // Estimate duration for WAV files
        if (audioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var estimatedDurationSec = Math.Max(0, (fileInfo.Length - 44)) / 32000.0;
            LoggingService.Info($"  Estimated duration: {estimatedDurationSec:F2} seconds");

            if (estimatedDurationSec < 0.5)
            {
                LoggingService.Warn($"  WARNING: Very short audio ({estimatedDurationSec:F2}s) - may cause issues");
            }
        }

        try
        {
            // STEP 1: Scan the audio and plan its chunks. Only the plan is kept; the
            // samples are read again chunk by chunk in STEP 3 (#1451).
            stepStopwatch.Restart();
            LoggingService.Debug("Step 1: Preparing audio stream...");
            var chunkEnds = PlanAudioChunks(audioPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stepStopwatch.Stop();
            LoggingService.Debug($"Step 1: Complete ({stepStopwatch.ElapsedMilliseconds}ms)");

            // Determine audio duration so we can pick the right decoding regime.
            // Short voice-typing clips (<=15s) keep the fast, deterministic path.
            // Longer recordings enable whisper.cpp's built-in temperature fallback
            // and multi-segment output so the decoder can escape repetition loops.
            var durationSeconds = GetAudioDurationSeconds(audioPath);
            bool isLongRecording = durationSeconds > 15.0;
            LoggingService.Debug($"  Regime: {(isLongRecording ? "long-recording" : "short-clip")} (duration={durationSeconds:F1}s)");

            // STEP 2: The decoding options. The worker's LocalWhisperService always
            // sets greedy sampling, temperature 0, no-speech 0.6, entropy 2.4 and
            // no context; these are the switches on top:
            // - Threads: all cores but one (keeps the UI responsive).
            // - MaxLastTextTokens(0): WithNoContext only clears the context before
            //   the first 30 s window; whisper.cpp still primes every later window
            //   with the text decoded so far, so one window's phrase could replace
            //   the next window's speech. 0 stops that carry-over.
            // - Long recordings: temperature fallback (inc 0.2) gated on entropy and
            //   log-probability (-1.0), and multi-segment output so the per-segment
            //   gating can kick in mid-recording (see Phil feedback #4).
            // - Short clips: no fallback passes, and stop after the first segment.
            var options = new WhisperWorkerTranscribeOptions(
                SingleSegment: !isLongRecording,
                MaxLastTextTokensZero: true,
                Threads: Math.Max(1, Environment.ProcessorCount - 1));
            LoggingService.Debug($"  Threads: {options.Threads}");
            LoggingService.Debug($"  Language: {(string.IsNullOrEmpty(language) ? "auto-detect" : language)}");

            // STEP 3: Run transcription, one chunk per worker request.
            stepStopwatch.Restart();
            LoggingService.Info("Step 3: Running transcription...");
            LoggingService.Debug($"  Start time: {DateTime.Now:HH:mm:ss.fff}");
            if (chunkEnds.Count > 1)
            {
                LoggingService.Debug($"  Chunks: {chunkEnds.Count} (at most {MaxChunkSamples / WhisperSampleRate / 60.0:F1} min each)");
            }

            SweepStaleTempWavs();
            var text = new StringBuilder();
            int segmentCount = 0;
            string? chunkLanguage = string.IsNullOrEmpty(language) ? null : language;
            bool languagePinned = chunkLanguage != null;
            foreach (var chunk in ReadAudioChunks(audioPath, chunkEnds, cancellationToken))
            {
                var chunkStart = TimeSpan.FromSeconds(chunk.StartSample / (double)WhisperSampleRate);
                var result = await TranscribeChunkAsync(chunk.Samples, chunkLanguage, options, cancellationToken)
                    .ConfigureAwait(false);
                segmentCount += result.SegmentCount;
                text.Append(result.RawText);
                LoggingService.Debug(DescribeSegment(
                    chunkStart,
                    chunkStart + TimeSpan.FromSeconds(chunk.Samples.Length / (double)WhisperSampleRate),
                    result.RawText) + $" ({result.SegmentCount} segments)");

                // Auto-detect used to run once, on the file's first 30 s. Keep it that
                // way: the first chunk that yields speech decides the language, and
                // every later chunk is decoded in it rather than detecting its own.
                if (!languagePinned && !string.IsNullOrEmpty(result.Language))
                {
                    languagePinned = true;
                    if (chunkEnds.Count > 1)
                    {
                        chunkLanguage = result.Language;
                        LoggingService.Debug($"  Language: detected {result.Language}, kept for the remaining chunks");
                    }
                }
            }

            stepStopwatch.Stop();
            var inferenceTimeMs = stepStopwatch.ElapsedMilliseconds;
            LoggingService.Info($"Step 3: Complete ({inferenceTimeMs}ms)");
            if (_worker.LoadResult is { } load)
            {
                LoggingService.Info($"  Whisper worker runtime: {load.Runtime}" + (load.Gpu != null ? $" ({load.Gpu})" : ""));
            }

            // Log warning if inference took unusually long
            if (inferenceTimeMs > 30000)
            {
                LoggingService.Warn($"  WARNING: Inference took {inferenceTimeMs}ms (>30s) - potential performance issue");
            }

            // Whisper writes its own non-speech markers (a blank-audio tag and kin) as
            // segment text. A silent file or mic must come back empty, so the
            // orchestrator raises NoSpeechDetected instead of saving or pasting the
            // marker as a transcript (issue #1449).
            var rawText = StripNonSpeechMarkers(text.ToString());

            // Belt-and-braces: collapse whisper.cpp repetition loops that leak through
            // the decoder-level entropy/temperature gating. Requires repeated phrases
            // rather than repeated single words so normal emphasis survives.
            var finalText = CollapseRepetitionLoops(rawText);

            totalStopwatch.Stop();

            // Log performance summary
            LoggingService.Info($"========== FILE TRANSCRIPTION COMPLETE ==========");
            LoggingService.Info($"  Segments: {segmentCount}");
            LoggingService.Info($"  Characters: {finalText.Length}");
            LoggingService.Info($"  Inference time: {inferenceTimeMs}ms");
            LoggingService.Info($"  Total time: {totalStopwatch.ElapsedMilliseconds}ms");

            // Log if transcription returned empty
            if (segmentCount == 0)
            {
                LoggingService.Warn("  WARNING: No segments returned - audio may be silent, too short, or unrecognizable");
            }

            return finalText;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // User pressed Escape / hit Cancel. Not a failure — surface it as a
            // cancellation so the callers' OperationCanceledException handlers run and
            // the user sees "Recording cancelled" instead of an error toast. The
            // worker answers a cancelled request with code "cancelled"; any error
            // that races the cancel means the same.
            totalStopwatch.Stop();
            LoggingService.Info($"TranscriptionService: File transcription cancelled after {totalStopwatch.ElapsedMilliseconds}ms ({ex.GetType().Name})");
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            LoggingService.Error($"TranscriptionService: File transcription failed after {totalStopwatch.ElapsedMilliseconds}ms ({DescribeWorkerFailure(ex)})", ex);
            LoggingService.Error($"  Backend: {DescribeBackend()}");

            // The worker reads the model at its first load, not at selection, so a
            // damaged file used to show "Ready" and fail every transcription with
            // no in-app way out (#1598). Mark it broken: it then counts as not
            // installed, and Model Library offers it for download again.
            MarkBrokenIfModelFileFault(ex, modelPath);
            throw;
        }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// Hands one chunk to the worker as a temp 16-bit WAV and deletes the file
    /// afterwards, whatever happens to the request.
    /// </summary>
    private async Task<WhisperWorkerTranscript> TranscribeChunkAsync(
        ReadOnlyMemory<float> samples,
        string? language,
        WhisperWorkerTranscribeOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TempWavDirectory);
        var wavPath = Path.Combine(TempWavDirectory, $"chunk-{Guid.NewGuid():N}.wav");
        try
        {
            WhisperWav.Write(wavPath, samples.Span);
            // The chunk's length scales the worker's watchdog for this request.
            var duration = TimeSpan.FromSeconds(samples.Length / (double)WhisperWav.SampleRate);
            var transcript = await _worker.TranscribeAsync(wavPath, language, options, cancellationToken, duration).ConfigureAwait(false);
            if (transcript.StartAndLoad > TimeSpan.Zero)
            {
                LoggingService.Info($"  Worker start and model load: {transcript.StartAndLoad.TotalMilliseconds:0}ms");
            }
            WhisperLoadTiming.Add(transcript.StartAndLoad);
            return transcript;
        }
        finally
        {
            TryDeleteFile(wavPath);
        }
    }

    /// <summary>
    /// Deletes chunk WAVs left behind by an app that crashed mid-request. Once per
    /// session, and only files over an hour old, so a running request keeps its own.
    /// </summary>
    private void SweepStaleTempWavs()
    {
        if (Interlocked.Exchange(ref _staleWavSweepDone, 1) != 0) return;
        try
        {
            if (!Directory.Exists(TempWavDirectory)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(TempWavDirectory, "chunk-*.wav"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"TranscriptionService: Stale WAV sweep failed: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"TranscriptionService: Could not delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void MarkBrokenIfModelFileFault(Exception ex, string? modelPath)
    {
        if (modelPath != null && ex is WhisperWorkerException && IsModelFileLoadFault(ex))
        {
            LoggingService.Error("========== WHISPER MODEL LOAD FAILURE ==========");
            LoggingService.Error($"  Model: {Path.GetFileName(modelPath)}");
            LoggingService.Error($"  Worker code: {((WhisperWorkerException)ex).Code}");
            LocalModelHealth.MarkBroken(modelPath, "Whisper model failed to load");
        }
    }

    private static string DescribeWorkerFailure(Exception ex) => ex is WhisperWorkerException worker
        ? $"worker code {worker.Code}: {worker.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Transcribes an audio file asynchronously with cancellation-aware Whisper processing.
    /// </summary>
    public Task<string> TranscribeFileAsync(string audioPath, string? language = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            async () => await TranscribeFileInternalAsync(audioPath, language, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    /// <summary>
    /// ITranscriptionProvider interface implementation.
    /// Delegates to existing TranscribeFileAsync with vocabulary support (vocabulary ignored for local).
    /// </summary>
    /// <remarks>
    /// Local Whisper models don't support vocabulary boosting like cloud providers do.
    /// The vocabulary parameter is accepted for interface compatibility but ignored.
    /// </remarks>
    public Task<string> TranscribeAsync(
        string audioPath,
        string? language = null,
        IReadOnlyList<string>? vocabulary = null,
        CancellationToken cancellationToken = default)
    {
        // NOTE: Local Whisper.net does not support vocabulary boosting.
        // The vocabulary parameter is ignored for local transcription.
        // Cloud providers (OpenAI, etc.) can use vocabulary via the "prompt" parameter.
        return TranscribeFileAsync(audioPath, language, cancellationToken);
    }

    /// <summary>
    /// Ensures a model is selected, and returns its path.
    /// </summary>
    private string EnsureInitialized()
    {
        return LoadedModelPath ?? throw new InvalidOperationException(
            "TranscriptionService not initialized. Call InitializeAsync() first.");
    }
    // =========================================================================
    // AUDIO PREPARATION
    // =========================================================================

    /// <summary>Whisper's input rate. Every chunk constant below counts samples at this rate.</summary>
    private const int WhisperSampleRate = 16000;

    /// <summary>
    /// Whisper gets long audio in chunks of about five minutes, so a job never holds
    /// the whole decoded file. Holding it (twice as floats, once as an in-memory WAV,
    /// all on the large object heap) left a 3 h Transcribe File job's idle app at
    /// 3.1 GB private memory (#1451).
    /// </summary>
    internal const int TargetChunkSamples = WhisperSampleRate * 300;

    /// <summary>Each cut lands on the quietest 50 ms in the 10 s before its target, so it falls between words.</summary>
    private const int ChunkCutSearchSamples = WhisperSampleRate * 10;

    /// <summary>
    /// Up to 30 s past a target stays in the chunk, so the last chunk is never a
    /// fragment whisper.cpp skips (it ignores input under one second).
    /// </summary>
    private const int ChunkTailSamples = WhisperSampleRate * 30;

    internal const int MaxChunkSamples = TargetChunkSamples + ChunkTailSamples;

    /// <summary>
    /// One chunk of Whisper input. <see cref="Samples"/> is a view of a buffer that
    /// <see cref="ReadAudioChunks"/> refills for the next chunk, so finish with it first.
    /// </summary>
    internal readonly record struct AudioChunk(long StartSample, ReadOnlyMemory<float> Samples);

    /// <summary>
    /// Reads the audio once, without keeping its samples, and returns where each chunk
    /// ends (in 16 kHz samples). The last end is the trailing-silence trim point. Short
    /// audio is one chunk.
    /// </summary>
    internal static List<long> PlanAudioChunks(string audioPath, CancellationToken cancellationToken)
    {
        // AudioFileDecoder, not AudioFileReader: the Local API hands this method the
        // caller's own file, and a WAVE_FORMAT_EXTENSIBLE WAV would go to ACM and fail (#1450).
        using var reader = AudioFileDecoder.Open(audioPath);

        // Log conversion info
        if (reader.WaveFormat.SampleRate == WhisperSampleRate && reader.WaveFormat.Channels == 1)
        {
            LoggingService.Debug("  Audio already 16kHz mono");
        }
        else
        {
            LoggingService.Debug($"  Resampling from {reader.WaveFormat.SampleRate}Hz {reader.WaveFormat.Channels}ch to 16kHz mono");
        }

        var provider = ToWhisperSamples(reader);
        int window = WhisperSampleRate / 20;
        var rms = new List<double>((int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * 20 + 20));
        var buffer = new float[WhisperSampleRate];
        long count = 0;
        double sum = 0;
        int inWindow = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                var s = Math.Clamp(buffer[i], -1f, 1f);
                sum += s * s;
                if (++inWindow == window)
                {
                    rms.Add(Math.Sqrt(sum / window));
                    sum = 0;
                    inWindow = 0;
                }
            }
            count += read;
        }

        // Whisper invents a closing "Okay." or "Thank you." when a recording ends in
        // silence (the user pausing before the hotkey). Cut that silence off.
        var windowRms = CollectionsMarshal.AsSpan(rms);
        var keep = TrailingSilenceTrimPoint(windowRms, count, WhisperSampleRate);
        if (keep < count)
        {
            LoggingService.Debug($"  Trimmed {(count - keep) / (double)WhisperSampleRate:F2}s of trailing silence");
        }

        LoggingService.Debug($"  Prepared audio: {keep:N0} samples ({keep / (double)WhisperSampleRate:F2}s)");
        return ChunkEnds(windowRms, keep);
    }

    /// <summary>
    /// Splits the first <paramref name="keep"/> samples into chunks of about
    /// <see cref="TargetChunkSamples"/>, each cut at the quietest 50 ms window in the
    /// <see cref="ChunkCutSearchSamples"/> before its target. Returns each chunk's end.
    /// </summary>
    internal static List<long> ChunkEnds(ReadOnlySpan<double> windowRms, long keep)
    {
        int window = WhisperSampleRate / 20;
        var ends = new List<long>();
        long start = 0;
        while (keep - start > MaxChunkSamples)
        {
            long target = start + TargetChunkSamples;
            int first = (int)((target - ChunkCutSearchSamples) / window);
            int last = (int)(target / window) - 1;
            int quietest = last;
            for (int w = last - 1; w >= first; w--)
            {
                if (windowRms[w] < windowRms[quietest])
                {
                    quietest = w;
                }
            }
            long cut = (long)quietest * window + window / 2;
            ends.Add(cut);
            start = cut;
        }
        ends.Add(keep);
        return ends;
    }

    /// <summary>
    /// Reads the audio again and yields it chunk by chunk, ending each chunk at the
    /// matching entry of <paramref name="chunkEnds"/>. One buffer of the longest
    /// chunk's size is reused, so take each chunk before asking for the next.
    /// </summary>
    internal static IEnumerable<AudioChunk> ReadAudioChunks(
        string audioPath, IReadOnlyList<long> chunkEnds, CancellationToken cancellationToken)
    {
        using var reader = AudioFileDecoder.Open(audioPath);
        var provider = ToWhisperSamples(reader);

        long longest = 0;
        long previous = 0;
        foreach (var end in chunkEnds)
        {
            longest = Math.Max(longest, end - previous);
            previous = end;
        }

        var chunk = new float[longest];
        var buffer = new float[WhisperSampleRate];
        int buffered = 0;
        int position = 0;
        long start = 0;
        foreach (var end in chunkEnds)
        {
            int length = (int)(end - start);
            int filled = 0;
            while (filled < length)
            {
                if (position == buffered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffered = provider.Read(buffer, 0, buffer.Length);
                    position = 0;
                    if (buffered == 0)
                    {
                        break;
                    }
                }
                int take = Math.Min(buffered - position, length - filled);
                for (int i = 0; i < take; i++)
                {
                    chunk[filled + i] = ToWhisperInput(buffer[position + i]);
                }
                position += take;
                filled += take;
            }

            if (filled == 0)
            {
                yield break;
            }
            yield return new AudioChunk(start, chunk.AsMemory(0, filled));
            start = end;
        }
    }

    /// <summary>Folds to mono by averaging every channel, then resamples to 16 kHz if needed.</summary>
    private static ISampleProvider ToWhisperSamples(DecodedAudioFile reader)
    {
        // NAudio's ToMono() throws on more than two channels
        var provider = AudioFileDecoder.ToMono(reader.Samples);
        if (reader.WaveFormat.SampleRate != WhisperSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, WhisperSampleRate);
        }
        return provider;
    }

    /// <summary>
    /// The sample Whisper got when this audio went through a 16-bit WAV
    /// (NAudio's WaveFileWriter, then Whisper.net's WaveParser), so chunking
    /// leaves the input unchanged. The clamp stops resampler overshoot from
    /// wrapping around.
    /// </summary>
    internal static float ToWhisperInput(float sample) =>
        (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue) / 32768f;

    /// <summary>
    /// Returns how many samples to keep so the audio ends 0.3s after the last
    /// window loud enough to be speech. Loudness is measured in 50ms windows; a
    /// window counts as speech when it is 6x the recording's noise floor (its
    /// 10th-percentile window), capped at a fifth of its speech level (90th
    /// percentile) so a quiet last word on a noisy line still counts. Returns the
    /// full length when nothing stands out, and never less than 1.1s because
    /// whisper.cpp skips input shorter than one second.
    /// </summary>
    internal static int TrailingSilenceTrimPoint(ReadOnlySpan<float> samples, int sampleRate)
    {
        int window = sampleRate / 20;
        var rms = new double[samples.Length / window];
        for (int w = 0; w < rms.Length; w++)
        {
            double sum = 0;
            foreach (var s in samples.Slice(w * window, window))
            {
                sum += s * s;
            }
            rms[w] = Math.Sqrt(sum / window);
        }
        return (int)TrailingSilenceTrimPoint(rms, samples.Length, sampleRate);
    }

    /// <summary>
    /// <see cref="TrailingSilenceTrimPoint(ReadOnlySpan{float}, int)"/> from the RMS of
    /// each whole 50 ms window, so a caller can measure while it streams.
    /// </summary>
    internal static long TrailingSilenceTrimPoint(ReadOnlySpan<double> windowRms, long sampleCount, int sampleRate)
    {
        int window = sampleRate / 20;
        int windowCount = windowRms.Length;
        if (windowCount == 0)
        {
            return sampleCount;
        }

        var sorted = windowRms.ToArray();
        Array.Sort(sorted);
        double floor = sorted[windowCount / 10];
        double speech = sorted[windowCount * 9 / 10];
        // 0.0012 ≈ 40 on the 16-bit scale: a dead-quiet input's floor times six
        // would otherwise flag mic hiss as speech.
        double threshold = Math.Min(Math.Max(floor * 6, 0.0012), speech / 5);

        int last = windowCount - 1;
        while (last >= 0 && windowRms[last] <= threshold)
        {
            last--;
        }
        if (last < 0)
        {
            return sampleCount;
        }

        long keep = (long)(last + 1) * window + (int)(0.3 * sampleRate);
        long minimum = (int)(1.1 * sampleRate);
        return Math.Min(sampleCount, Math.Max(keep, minimum));
    }

    // =========================================================================
    // DURATION + POST-PROCESS HELPERS
    // =========================================================================

    /// <summary>
    /// Returns the duration of an audio file in seconds.
    ///
    /// Strategy: try AudioFileDecoder (WAV, including WAVE_FORMAT_EXTENSIBLE, MP3, etc.). If that fails
    /// for any reason, fall back to a rough estimate from file size assuming 16kHz
    /// mono 16-bit PCM (the format we record in). If everything fails, return 0 so
    /// we default to the short-clip fast path (safer than wrongly enabling fallback
    /// retries on a legit short clip).
    /// </summary>
    private static double GetAudioDurationSeconds(string audioPath)
    {
        try
        {
            using var reader = AudioFileDecoder.Open(audioPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"  Duration probe via AudioFileDecoder failed: {ex.Message}");

            // Fallback: estimate from WAV file size (16kHz mono 16-bit = 32000 B/s)
            try
            {
                var info = new FileInfo(audioPath);
                if (info.Exists && audioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    return Math.Max(0, (info.Length - 44)) / 32000.0;
                }
            }
            catch (Exception ex2)
            {
                LoggingService.Debug($"  Duration probe fallback failed: {ex2.Message}");
            }

            return 0.0;
        }
    }

    /// <summary>
    /// The log line for one Whisper segment: its timestamps and its length, never
    /// its words (#1645). The log ships in the Export Diagnostics bundle, which
    /// promises no transcripts, and it stays on disk for days.
    /// </summary>
    internal static string DescribeSegment(TimeSpan start, TimeSpan end, string? segmentText)
    {
        var characters = segmentText?.Trim().Length ?? 0;
        return $"  Segment: [{start:mm\\:ss} - {end:mm\\:ss}] {characters} chars";
    }

    /// <summary>
    /// Removes Whisper's non-speech markers from a transcript and trims it. A
    /// transcript made only of markers comes back empty (issue #1449). The rule is
    /// the shared core's (issue #1811), so Windows, Linux and macOS strip the same
    /// markers; its cases are shared-conformance/whisper-marker-vectors.json.
    /// </summary>
    internal static string StripNonSpeechMarkers(string? text)
    {
        // The core handles null, blank and surrounding whitespace itself; the
        // trimmed length is read only for the log line.
        var stripped = global::HyperWhisper.SharedCore.SharedCoreBridge.StripWhisperNonSpeechMarkers(text);
        var trimmedLength = text?.Trim().Length ?? 0;
        if (stripped.Length != trimmedLength)
        {
            LoggingService.Info(
                $"  Removed Whisper non-speech markers ({trimmedLength} → {stripped.Length} chars)");
        }
        return stripped;
    }

    /// <summary>
    /// Collapses runs of ≥3 consecutive identical repeated phrases (case-insensitive,
    /// whitespace-delimited word sequences) down to a single occurrence.
    ///
    /// This is a belt-and-braces guard against whisper.cpp's repetition-loop hallucination
    /// that can leak past the decoder-level entropy/temperature gating. Logs a warning
    /// whenever it fires so we can see loops in the wild via user log uploads.
    /// </summary>
    /// <remarks>
    /// Phrase lengths from three to twelve tokens are considered, but phrases that
    /// are themselves just a repeated one- or two-token pattern are ignored to
    /// avoid deleting ordinary emphasis.
    /// </remarks>
    internal static string CollapseRepetitionLoops(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Tokenise on whitespace; keep original tokens so we can reconstruct casing.
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        const int minPhraseTokens = 3;
        const int maxPhraseTokens = 12;
        const int minRepeats = 3;
        if (tokens.Length < minPhraseTokens * minRepeats) return text;

        // Normalized view for comparison. Trim edge punctuation so repeated sentence
        // fragments still match if the decoder varies commas/periods slightly.
        var normalized = new string[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            normalized[i] = NormalizeLoopToken(tokens[i]);
        }

        var keep = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) keep[i] = true;

        bool collapsed = false;
        int collapsedLoops = 0;
        int longestCollapsedPhrase = 0;

        int idx = 0;
        while (idx + (minPhraseTokens * minRepeats) - 1 < tokens.Length)
        {
            bool collapsedAtIndex = false;
            int maxLengthAtIndex = Math.Min(maxPhraseTokens, (tokens.Length - idx) / minRepeats);

            // Prefer the longest phrase. This keeps a full sentence fragment when
            // shorter sub-phrases inside it also happen to repeat.
            for (int phraseLength = maxLengthAtIndex; phraseLength >= minPhraseTokens; phraseLength--)
            {
                if (HasSmallerRepeatedPeriod(normalized, idx, phraseLength, maxPeriod: 2)) continue;

                int repeats = CountConsecutivePhraseRepeats(normalized, idx, phraseLength);
                if (repeats < minRepeats) continue;

                int next = idx + phraseLength * repeats;
                for (int k = idx + phraseLength; k < next; k++) keep[k] = false;
                collapsed = true;
                collapsedAtIndex = true;
                collapsedLoops++;
                longestCollapsedPhrase = Math.Max(longestCollapsedPhrase, phraseLength);
                idx = next; // skip past the collapsed run
                break;
            }

            if (!collapsedAtIndex)
            {
                idx++;
            }
        }

        if (!collapsed) return text;

        var sb = new StringBuilder(text.Length);
        int keptTokenCount = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!keep[i]) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(tokens[i]);
            keptTokenCount++;
        }

        var result = sb.ToString();
        LoggingService.Warn(
            $"  CollapseRepetitionLoops triggered: collapsed {collapsedLoops} repeated phrase loop(s), " +
            $"longest phrase={longestCollapsedPhrase} tokens ({tokens.Length} → {keptTokenCount} tokens). " +
            $"This indicates a Whisper repetition hallucination.");
        return result;
    }

    private static int CountConsecutivePhraseRepeats(string[] normalizedTokens, int start, int phraseLength)
    {
        int repeats = 1;
        int next = start + phraseLength;
        while (next + phraseLength <= normalizedTokens.Length
               && PhrasesEqual(normalizedTokens, start, next, phraseLength))
        {
            repeats++;
            next += phraseLength;
        }

        return repeats;
    }

    private static bool PhrasesEqual(string[] normalizedTokens, int firstStart, int secondStart, int phraseLength)
    {
        for (int i = 0; i < phraseLength; i++)
        {
            if (normalizedTokens[firstStart + i] != normalizedTokens[secondStart + i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSmallerRepeatedPeriod(string[] normalizedTokens, int start, int phraseLength, int maxPeriod)
    {
        for (int period = 1; period <= maxPeriod; period++)
        {
            if (phraseLength % period != 0) continue;

            bool matches = true;
            for (int i = period; i < phraseLength; i++)
            {
                if (normalizedTokens[start + i] != normalizedTokens[start + (i % period)])
                {
                    matches = false;
                    break;
                }
            }

            if (matches) return true;
        }

        return false;
    }

    private static string NormalizeLoopToken(string token)
    {
        var trimmed = token.Trim().Trim(
            '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}');

        return (trimmed.Length == 0 ? token : trimmed).ToLowerInvariant();
    }

    // =========================================================================
    // DISPOSAL
    // =========================================================================

    private void DisposeModel()
    {
        LoadedModelPath = null;
        ActiveGpuName = null;
        AvailableGpus = [];

        // Reset session tracking
        _transcriptionCount = 0;
        _hasLoggedGpuDiagnostics = false;
    }

    public void Dispose()
    {
        if (_isShared)
        {
            // Process-wide singleton — handed out by TranscriptionRuntime and
            // referenced by both the API server and the GUI. Caller probably
            // meant UnloadModelAsync(). The worker exits with the app: its stdin
            // closes when this process ends.
            LoggingService.Debug("TranscriptionService: Dispose() called on shared instance — ignoring (use UnloadModelAsync)");
            return;
        }
        if (_disposed) return;
        _disposed = true;
        LoggingService.Info("TranscriptionService: Disposing...");

        // Reverse order of creation; one failure must not block the rest.
        lock (_recordingHoldLock)
        {
            SafeDispose(ref _recordingHold);
        }
        try
        {
            DisposeModel();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"TranscriptionService: Clearing the model state failed: {ex.Message}");
        }
        if (DisposeWorker())
        {
            // Only once the worker is down: a request that outlived the bounded
            // wait may still release this lock.
            try
            {
                _modelLock.Dispose();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"TranscriptionService: Dispose failed for the model lock: {ex.Message}");
            }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Stops the worker, waiting at most <see cref="WorkerDisposeTimeout"/>. The
    /// client awaits with ConfigureAwait(false) throughout, so blocking here cannot
    /// deadlock a UI-thread caller. Returns false if it did not finish in time
    /// (the worker still exits when this process does: its stdin closes).
    /// </summary>
    private bool DisposeWorker()
    {
        try
        {
            var dispose = _worker.DisposeAsync().AsTask();
            if (dispose.Wait(WorkerDisposeTimeout)) return true;
            LoggingService.Warn($"TranscriptionService: The Whisper worker did not stop within {WorkerDisposeTimeout.TotalSeconds:0} s; not waiting longer");
            _ = dispose.ContinueWith(
                t => LoggingService.Warn($"TranscriptionService: Late worker dispose failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"TranscriptionService: Dispose failed for the Whisper worker: {(ex as AggregateException)?.GetBaseException().Message ?? ex.Message}");
            return false;
        }
    }

    private static void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"TranscriptionService: Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
    }
}
