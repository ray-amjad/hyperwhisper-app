using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;
using HyperWhisper.Utilities;

namespace HyperWhisper.Services;

/// <summary>
/// TRANSCRIPTION SERVICE - WHISPER.NET INTEGRATION
///
/// Purpose:
/// Service for transcribing audio using Whisper.net (sandrohanea/whisper.net).
/// Supports both ARM64 (CPU) and x64 (CUDA GPU acceleration) platforms.
///
/// WHISPER.NET ADVANTAGES:
/// - Cross-platform support including ARM64
/// - CUDA GPU acceleration on x64 with NVIDIA GPUs
/// - CPU fallback for systems without compatible GPUs
/// - Uses standard GGML model format from Hugging Face
///
/// GPU ACCELERATION:
/// On x64 with NVIDIA GPUs, Whisper.net uses CUDA for GPU acceleration.
/// On ARM64 or x64 without NVIDIA, it falls back to CPU processing.
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
    /// Whisper.net automatically detects and uses the best available runtime:
    /// - CUDA (if NVIDIA GPU and Whisper.net.Runtime.Cuda is installed)
    /// - Vulkan (if any GPU and Whisper.net.Runtime.Vulkan is installed — works with NVIDIA, AMD, Intel)
    /// - CPU (fallback)
    ///
    /// The runtime selection happens automatically when WhisperFactory.FromPath() is called.
    /// </summary>

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
    // STATIC INITIALIZATION
    // =========================================================================

    /// <summary>
    /// Force Vulkan as the preferred GPU runtime. Vulkan is vendor-agnostic
    /// (works with NVIDIA, AMD, Intel) and avoids needing the CUDA toolkit.
    /// Must run before any WhisperFactory is created.
    /// </summary>
    static TranscriptionService()
    {
        RuntimeOptions.RuntimeLibraryOrder = [
            RuntimeLibrary.Vulkan,
            RuntimeLibrary.Cpu,
            RuntimeLibrary.CpuNoAvx,
        ];
    }

    // =========================================================================
    // STATE
    // =========================================================================

    /// <summary>
    /// The Whisper factory for creating processors.
    /// Holds the loaded model in memory.
    /// </summary>
    private WhisperFactory? _whisperFactory;

    /// <summary>
    /// Whether the service has been initialized with a model.
    /// </summary>
    public bool IsInitialized => _whisperFactory != null;

    /// <summary>
    /// Held across <see cref="InitializeAsync"/> and <see cref="UnloadModelAsync"/>
    /// so they serialize against each other and against the in-flight wait.
    /// </summary>
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    /// <summary>
    /// Number of <see cref="TranscribeFileInternalAsync"/> calls currently in
    /// the critical native code. <see cref="UnloadModelAsync"/> waits for this to
    /// reach zero before disposing the WhisperFactory.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Set by <see cref="TranscriptionRuntime"/> for the process-wide singleton
    /// instance. When true, <see cref="Dispose"/> is a no-op — the API server
    /// and GUI share this instance and the OS reclaims native handles at
    /// process exit. Direct <c>new TranscriptionService()</c> callers (e.g.
    /// benchmarks or tests) still dispose normally.
    /// </summary>
    private readonly bool _isShared;

    public TranscriptionService() : this(isShared: false) { }

    internal TranscriptionService(bool isShared)
    {
        _isShared = isShared;
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
    /// Path to the currently loaded model (for display purposes).
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
    /// Initializes the transcription service by loading a Whisper model.
    ///
    /// MODEL LOADING PROCESS:
    /// 1. Validate model file exists
    /// 2. Load GGML model using WhisperFactory
    /// 3. Model stays loaded for fast subsequent transcriptions
    ///
    /// PERFORMANCE NOTE:
    /// Loading the model takes a few seconds depending on model size.
    /// Once loaded, transcriptions are fast because the model is in memory.
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
        // The API server may already be holding the lock via UnloadModelAsync; wait
        // it out instead of racing the factory pointer.
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that saw an unload pending queues here behind it (#1609).
            // If that unload was cancelled, the model it would have disposed is
            // still loaded: keep it instead of waiting out the running job to
            // load the same file again. Every caller checks this before it calls,
            // so this only fires after a wait on the lock.
            if (IsInitialized
                && string.Equals(LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.Info("  Model already loaded while this load waited; keeping it");
                return;
            }

            // Wait for in-flight transcriptions to drain before disposing the old
            // factory. They were started before we acquired the lock and the still
            // running native processor is bound to the current WhisperFactory;
            // disposing it out from under them is a corrupted-state AccessViolation
            // that tears down the process. Mirrors UnloadModelAsync.
            while (Volatile.Read(ref _inFlight) > 0)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            // Dispose any previously loaded model
            DisposeModel();

        // Validate model file exists
        if (!File.Exists(modelPath))
        {
            LoggingService.Error($"Model file not found: {Path.GetFileName(modelPath)}");
            throw new FileNotFoundException("Model file not found", modelPath);
        }

        LoggingService.Info($"  Model file size: {new FileInfo(modelPath).Length:N0} bytes");

        try
        {
            // Report 0% progress at start
            progress?.Invoke(0.0);

            // STEP 1: Detect GPU information for logging
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

            // STEP 2: Load the Whisper model
            // WhisperFactory.FromPath loads the model and determines the runtime to use
            LoggingService.Info("Step 2: Loading Whisper model (this may take a moment)...");

            // Load model on background thread to not block UI
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Enable Flash Attention for significantly faster GPU inference (2-3x speedup).
                // On multi-GPU systems (e.g. Intel UHD + NVIDIA), Vulkan defaults to
                // device 0 which is often the integrated GPU. Use the DXGI adapter index
                // of the best GPU so Vulkan targets the discrete GPU instead.
                var gpuDevice = gpu?.AdapterIndex ?? 0;
                LoggingService.Info($"  Using GpuDevice={gpuDevice}" + (gpu != null ? $" ({gpu.Name})" : ""));

                var options = new WhisperFactoryOptions
                {
                    UseGpu = true,
                    UseFlashAttention = true,
                    GpuDevice = gpuDevice
                };

                _whisperFactory = WhisperFactory.FromPath(modelPath, options);
            }, cancellationToken);

            LoadedModelPath = modelPath;

            // Log the actual runtime info to verify which backend (CUDA/Vulkan/CPU) loaded
            try
            {
                var runtimeInfo = WhisperFactory.GetRuntimeInfo();
                LoggingService.Info($"  Whisper Runtime Info: {runtimeInfo}");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"  Could not get runtime info: {ex.Message}");
            }

            // Determine if GPU is being used based on runtime configuration and GPU availability
            // With Vulkan support, AMD and Intel GPUs can also accelerate transcription
            if (!PlatformHelper.IsArm64 && HasDiscreteGpu())
            {
                ActiveGpuName = gpu?.Name;
            }
            else
            {
                ActiveGpuName = null;
            }

            // Report 100% progress at end
            progress?.Invoke(1.0);

            LoggingService.Info("Step 2: Model loaded successfully!");
            LoggingService.Info($"  Compute Backend: {(IsUsingGpu ? $"GPU ({ActiveGpuName})" : "CPU")}");
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
    /// True when a <see cref="WhisperModelLoadException"/> is about the model
    /// file, not the native runtime (#1598). A missing or wrong-architecture
    /// whisper DLL raises the same exception with a DllNotFoundException or
    /// BadImageFormatException inside it; a new download cannot fix that, so it
    /// must not mark the model broken.
    /// </summary>
    internal static bool IsModelFileLoadFault(Exception ex)
    {
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
    /// Releases the loaded Whisper model so a different model can be loaded
    /// without disposing the service. Waits for any in-flight transcription
    /// to finish (no AV/crash on concurrent /transcribe). After this returns,
    /// <see cref="IsAvailable"/> is false until <see cref="InitializeAsync"/>
    /// is called again.
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
                // Wait for in-flight transcriptions to drain. They were started
                // before we acquired the lock, but we still need to ensure the
                // native processor is gone before we dispose the factory.
                while (Volatile.Read(ref _inFlight) > 0)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
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
    /// work on a model about to be disposed: the work queued on the model lock
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
    /// 2. Create processor from factory with language settings
    /// 3. Process audio and collect segments
    /// 4. Return combined text
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="language">Language code (e.g., "en", "ja"). Null for auto-detect.</param>
    /// <returns>Transcribed text.</returns>
    public string TranscribeFile(string audioPath, string? language = null)
    {
        // Go through TranscribeFileAsync so the body runs on a thread-pool thread with no
        // SynchronizationContext. Not fixing a live deadlock — today's first suspension
        // point already uses ConfigureAwait(false), so nothing captures the UI context.
        // It's belt-and-braces: neither `await using` nor Whisper.net's DisposeAsync uses
        // ConfigureAwait(false), so a future edit that reaches the dispose with a captured
        // context would deadlock any UI-thread caller blocking on this wrapper.
        return TranscribeFileAsync(audioPath, language)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<string> TranscribeFileInternalAsync(
        string audioPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
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
        LoggingService.Info($"  Backend: {(IsUsingGpu ? $"GPU ({ActiveGpuName})" : "CPU")}");
        LoggingService.Info($"  Model: {Path.GetFileName(LoadedModelPath)}");
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

            // STEP 2: Create processor with language settings and segment handler
            stepStopwatch.Restart();
            LoggingService.Debug("Step 2: Creating transcription processor...");

            // Log factory state before CreateBuilder - helps diagnose ARM64 issues
            LoggingService.Debug($"  Factory state: {(_whisperFactory != null ? "loaded" : "NULL")}");
            LoggingService.Debug($"  Model path: {LoadedModelPath}");
            LoggingService.Debug($"  Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

            var text = new StringBuilder();
            int segmentCount = 0;

            // ARM64 WORKAROUND: Whisper.net 1.9.0 has a threading issue on ARM64 where
            // CreateBuilder() fails when called from a different thread than the one
            // that created the factory. As a workaround, recreate the factory on this thread.
            // On x64, we use the shared factory for performance.
            WhisperFactory? arm64Factory = null;
            WhisperProcessorBuilder builder;

            try
            {
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    LoggingService.Debug("  ARM64 detected - recreating factory on transcription thread...");
                    var arm64Options = new WhisperFactoryOptions
                    {
                        UseGpu = true,
                        UseFlashAttention = true
                    };
                    arm64Factory = WhisperFactory.FromPath(LoadedModelPath!, arm64Options);
                    builder = arm64Factory.CreateBuilder();
                    LoggingService.Debug("  ARM64 factory recreation successful!");
                }
                else
                {
                    builder = _whisperFactory!.CreateBuilder();
                }
            }
            catch (Whisper.net.WhisperModelLoadException ex)
            {
                // Enhanced logging for ARM64 model loading failures
                LoggingService.Error("========== WHISPER MODEL LOAD FAILURE ==========");
                LoggingService.Error($"  Exception: {ex.GetType().FullName}");
                LoggingService.Error($"  Message: {ex.Message}");
                LoggingService.Error($"  Model Path: {LoadedModelPath}");
                LoggingService.Error($"  Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                LoggingService.Error($"  OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
                LoggingService.Error($"  Thread ID: {Environment.CurrentManagedThreadId}");
                LoggingService.Error($"  Factory Hash: {_whisperFactory?.GetHashCode()}");

                // Log inner exception chain
                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    LoggingService.Error($"  Inner[{depth}] Type: {inner.GetType().FullName}");
                    LoggingService.Error($"  Inner[{depth}] Message: {inner.Message}");
                    if (inner is System.DllNotFoundException)
                    {
                        LoggingService.Error($"  Inner[{depth}] DLL Not Found - this is likely a native library issue");
                    }
                    if (inner is System.BadImageFormatException)
                    {
                        LoggingService.Error($"  Inner[{depth}] BadImageFormat - architecture mismatch (x64 vs ARM64?)");
                    }
                    inner = inner.InnerException;
                    depth++;
                }

                // Try to get native error via P/Invoke
                LoggingService.Error("  --- Native DLL Load Test ---");
                TryLoadNativeDlls();

                // Log native runtime information
                LoggingService.Error("  --- Native Runtime Diagnostics ---");
                LoggingService.LogRuntimesDirectory();
                LoggingService.LogLoadedAssemblies("Whisper");
                LoggingService.Error("========== END MODEL LOAD FAILURE ==========");

                // Whisper.net loads the file lazily here, not in InitializeAsync, so a
                // damaged file showed "Ready" and failed every transcription with no
                // in-app way out (#1598). Mark it broken: it then counts as not
                // installed, and Model Library offers it for download again.
                if (IsModelFileLoadFault(ex))
                {
                    LocalModelHealth.MarkBroken(LoadedModelPath, "Whisper model failed to load");
                }

                arm64Factory?.Dispose();
                throw;
            }

            try
            {
                // Configure inference parameters optimized for voice typing (short audio, 2-15s)
                // Greedy decoding: single decoder path, fastest for short utterances
                // Beam search (beam_size=5) allocates 5 parallel decoders with 7x KV cache —
                // unnecessary overhead for short voice clips where greedy produces identical results
                builder.WithGreedySamplingStrategy();

                // Set thread count to all available cores minus one (keeps UI responsive)
                var threadCount = Math.Max(1, Environment.ProcessorCount - 1);
                builder.WithThreads(threadCount);
                LoggingService.Debug($"  Threads: {threadCount}");

                // Temperature 0.0 = deterministic decoding (fastest, single decoder)
                // At temp > 0, greedy uses best_of candidates which multiplies decoder work
                builder.WithTemperature(0.0f);

                builder.WithNoSpeechThreshold(0.6f);
                builder.WithEntropyThreshold(2.4f);

                // Each voice recording is independent — don't use prior text as decoder context
                builder.WithNoContext();

                // WithNoContext only clears the context once, before the first 30s window.
                // whisper.cpp still primes every later window with the text decoded so far,
                // so on long recordings one window's phrase gets copied into the next and
                // replaces the real speech (a repetition loop the entropy gate misses, because
                // each repeat carries new timestamp tokens). 0 stops that carry-over. Safe
                // here: no initial prompt is set, and that prompt rides the same channel.
                builder.WithMaxLastTextTokens(0);

                if (isLongRecording)
                {
                    // Long recording — enable Whisper's built-in loop-recovery fallback.
                    // With 0.2 inc, whisper retries at [0.0, 0.2, 0.4, 0.6, 0.8, 1.0] when a
                    // segment fails the entropy/logprob gate, so the decoder can escape
                    // greedy repetition loops (see Phil feedback #4).
                    builder.WithTemperatureInc(0.2f);
                    // Gate fallback on average log-probability too, not just entropy.
                    // Catches stuck chunks that have low entropy (model is "confident"
                    // about repeating the same token) but very low log-prob (the chunk
                    // overall doesn't fit the audio). -1.0 is whisper.cpp's default.
                    builder.WithLogProbThreshold(-1.0f);
                    // NOTE: Intentionally do NOT call WithSingleSegment() for long audio —
                    // whisper.cpp needs to emit multiple segments so its per-segment gating
                    // (entropy/logprob) and fallback can actually kick in mid-recording.
                }
                else
                {
                    // Short voice-typing clip — keep the existing fast path.
                    // Disable temperature fallback passes entirely (temperature_inc=0)
                    // — wasteful for voice typing where retries rarely help.
                    builder.WithTemperatureInc(0.0f);
                    // Voice typing clips are short — stop after first segment
                    builder.WithSingleSegment();
                }

                // Whisper times each segment from the start of its chunk.
                var chunkOffset = TimeSpan.Zero;
                string? detectedLanguage = null;
                builder.WithSegmentEventHandler((segment) =>
                    {
                        segmentCount++;
                        detectedLanguage ??= segment.Language;
                        var segmentText = segment.Text ?? "";
                        text.Append(segmentText);
                        LoggingService.Debug(DescribeSegment(segment.Start + chunkOffset, segment.End + chunkOffset, segmentText));
                    });

                // Configure language
                if (!string.IsNullOrEmpty(language))
                {
                    builder.WithLanguage(language);
                    LoggingService.Debug($"  Language: {language}");
                }
                else
                {
                    // Auto-detect language
                    builder.WithLanguageDetection();
                    LoggingService.Debug("  Language: auto-detect");
                }

                // MUST be `await using` (IAsyncDisposable), not `using`.
                // Whisper.net's WhisperProcessor.Dispose() throws
                //   "Cannot dispose while processing, please use DisposeAsync instead."
                // whenever its processing semaphore is still held — which is exactly
                // the case when the caller cancels mid-transcription: ProcessAsync stops
                // enumerating as soon as it observes the cancellation, while the native
                // whisper_full call is still unwinding on its own thread and holding the
                // semaphore. The synchronous Dispose then threw from the `using` unwind,
                // replacing the real outcome with a raw Exception (surfaced to users as
                // "Transcription failed: Cannot dispose while processing...") and leaking
                // the processor's pinned GCHandles / unmanaged strings, which Dispose
                // never reached. DisposeAsync waits for the (already aborting) native
                // task first. See the cancellation catch below for how ProcessAsync
                // actually surfaces that cancellation — it races between two exception
                // types, which is why that filter is broad.
                await using var processor = builder.Build();
                stepStopwatch.Stop();
                LoggingService.Debug($"Step 2: Complete ({stepStopwatch.ElapsedMilliseconds}ms)");

                // STEP 3: Run transcription (segments collected via event handler above)
                stepStopwatch.Restart();
                LoggingService.Info("Step 3: Running transcription...");
                LoggingService.Debug($"  Start time: {DateTime.Now:HH:mm:ss.fff}");

                // Process audio one chunk at a time - segments are collected via the
                // event handler. Each ProcessAsync call is its own whisper_full run with
                // its own state, so only one chunk's samples and mel are ever held.
                if (chunkEnds.Count > 1)
                {
                    LoggingService.Debug($"  Chunks: {chunkEnds.Count} (at most {MaxChunkSamples / WhisperSampleRate / 60.0:F1} min each)");
                }
                bool languagePinned = !string.IsNullOrEmpty(language);
                foreach (var chunk in ReadAudioChunks(audioPath, chunkEnds, cancellationToken))
                {
                    chunkOffset = TimeSpan.FromSeconds(chunk.StartSample / (double)WhisperSampleRate);
                    await foreach (var _ in processor
                        .ProcessAsync(chunk.Samples, cancellationToken)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                    }

                    // Auto-detect used to run once, on the file's first 30 s. Keep it that
                    // way: the first chunk that yields speech decides the language, and
                    // every later chunk is decoded in it rather than detecting its own.
                    if (!languagePinned && !string.IsNullOrEmpty(detectedLanguage))
                    {
                        languagePinned = true;
                        if (chunkEnds.Count > 1)
                        {
                            processor.ChangeLanguage(detectedLanguage);
                            LoggingService.Debug($"  Language: detected {detectedLanguage}, kept for the remaining chunks");
                        }
                    }
                }

            stepStopwatch.Stop();
            var inferenceTimeMs = stepStopwatch.ElapsedMilliseconds;
            LoggingService.Info($"Step 3: Complete ({inferenceTimeMs}ms)");

            // Log warning if inference took unusually long
            if (inferenceTimeMs > 30000)
            {
                LoggingService.Warn($"  WARNING: Inference took {inferenceTimeMs}ms (>30s) - potential performance issue");
            }

            // Whisper writes its own non-speech markers ([BLANK_AUDIO] and kin) as
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
            finally
            {
                // Dispose ARM64 per-transcription factory
                arm64Factory?.Dispose();
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // User pressed Escape / hit Cancel. Not a failure — surface it as a
            // cancellation so the callers' OperationCanceledException handlers run and
            // the user sees "Recording cancelled" instead of an error toast.
            //
            // The filter is deliberately broad. Whisper.net 1.9.0 races here: its abort
            // callback is wired to our token, so whisper_full returns non-zero and the
            // background task faults with WhisperProcessingException. If that fault lands
            // before ProcessAsync's iterator observes the token, the iterator's
            // `ThrowIfCancellationRequested()` guard is skipped (processingTask.IsCompleted
            // is already true and the segment buffer is empty) and `await processingTask`
            // rethrows WhisperProcessingException instead of TaskCanceledException.
            // Cancelling during the encode pass hits that path; cancelling at a segment
            // boundary hits the OperationCanceledException path. Both mean "user cancelled".
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
            LoggingService.Error($"TranscriptionService: File transcription failed after {totalStopwatch.ElapsedMilliseconds}ms", ex);
            LoggingService.Error($"  Backend: {(IsUsingGpu ? ActiveGpuName : "CPU")}");
            throw;
        }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

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
    // NATIVE DLL DIAGNOSTICS
    // =========================================================================

    /// <summary>
    /// Attempts to load native Whisper DLLs directly using P/Invoke to get detailed error info.
    /// This helps diagnose ARM64 loading issues that Whisper.net doesn't expose.
    /// </summary>
    private static void TryLoadNativeDlls()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var rid = arch switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            System.Runtime.InteropServices.Architecture.X64 => "win-x64",
            System.Runtime.InteropServices.Architecture.X86 => "win-x86",
            _ => "unknown"
        };

        var runtimeDir = Path.Combine(baseDir, "runtimes", rid);
        LoggingService.Error($"  Testing native DLLs from: {runtimeDir}");

        var dllsToTest = new[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" };

        foreach (var dllName in dllsToTest)
        {
            var dllPath = Path.Combine(runtimeDir, dllName);
            if (!File.Exists(dllPath))
            {
                LoggingService.Error($"  [{dllName}] FILE NOT FOUND at {dllPath}");
                continue;
            }

            try
            {
                var handle = NativeLibrary.Load(dllPath);
                if (handle != IntPtr.Zero)
                {
                    LoggingService.Error($"  [{dllName}] LOADED SUCCESSFULLY (handle: 0x{handle:X})");
                    NativeLibrary.Free(handle);
                }
                else
                {
                    var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    LoggingService.Error($"  [{dllName}] LOAD FAILED - handle is null, Win32 error: {error}");
                }
            }
            catch (DllNotFoundException ex)
            {
                LoggingService.Error($"  [{dllName}] DllNotFoundException: {ex.Message}");
            }
            catch (BadImageFormatException ex)
            {
                LoggingService.Error($"  [{dllName}] BadImageFormatException (arch mismatch?): {ex.Message}");
            }
            catch (Exception ex)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                LoggingService.Error($"  [{dllName}] EXCEPTION: {ex.GetType().Name}: {ex.Message} (Win32 error: {error})");
            }
        }
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    /// <summary>
    /// Ensures the service has been initialized with a model.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_whisperFactory == null)
        {
            throw new InvalidOperationException(
                "TranscriptionService not initialized. Call InitializeAsync() first.");
        }
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
    /// Whisper's non-speech annotations: a bracketed or parenthesised marker it
    /// emits in place of words when it hears none. Only these fixed names match, so
    /// a bracket the speaker dictated survives.
    /// </summary>
    private static readonly Regex NonSpeechMarker = new(
        @"[\[(]\s*(?:BLANK[_ ]AUDIO|silence|no speech|inaudible|music)\s*[\])]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Removes Whisper's non-speech markers from a transcript and trims it. A
    /// transcript made only of markers comes back empty (issue #1449).
    /// </summary>
    internal static string StripNonSpeechMarkers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var trimmed = text.Trim();
        if (!NonSpeechMarker.IsMatch(trimmed)) return trimmed;

        var stripped = WhitespaceRun.Replace(NonSpeechMarker.Replace(trimmed, " "), " ").Trim();
        LoggingService.Info(
            $"  Removed Whisper non-speech markers ({trimmed.Length} → {stripped.Length} chars)");
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
        if (_whisperFactory != null)
        {
            LoggingService.Debug("TranscriptionService: Disposing WhisperFactory...");
            _whisperFactory.Dispose();
            _whisperFactory = null;
        }

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
            // referenced by both the API server and the GUI. Disposing would
            // leave one of them with a dead factory pointer. Caller probably
            // meant UnloadModelAsync(); release the model without killing the
            // service.
            LoggingService.Debug("TranscriptionService: Dispose() called on shared instance — ignoring (use UnloadModelAsync)");
            return;
        }
        LoggingService.Info("TranscriptionService: Disposing...");
        DisposeModel();
        _modelLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
