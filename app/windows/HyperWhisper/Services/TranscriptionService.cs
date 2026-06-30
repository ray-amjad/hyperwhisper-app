using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// Detects if an NVIDIA GPU is present in the system (for CUDA-specific logic).
    /// </summary>
    private static bool HasNvidiaGpu()
    {
        var gpu = GpuInfoService.GetBestGpu();
        if (gpu == null) return false;
        var name = gpu.Name.ToUpperInvariant();
        return name.Contains("NVIDIA") || name.Contains("GEFORCE") ||
               name.Contains("RTX") || name.Contains("GTX");
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
    /// Held across <see cref="InitializeAsync"/> and <see cref="UnloadModel"/>
    /// so they serialize against each other and against the in-flight wait.
    /// </summary>
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    /// <summary>
    /// Number of <see cref="TranscribeFileInternalAsync"/> calls currently in
    /// the critical native code. <see cref="UnloadModel"/> waits for this to
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
        LoggingService.Info($"  Model Path: {modelPath}");

        // Serialize against UnloadModel and concurrent InitializeAsync calls.
        // The API server may already be holding the lock via UnloadModel; wait
        // it out instead of racing the factory pointer.
        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            LoggingService.Error($"Model file not found: {modelPath}");
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
    /// Releases the loaded Whisper model so a different model can be loaded
    /// without disposing the service. Waits for any in-flight transcription
    /// to finish (no AV/crash on concurrent /transcribe). After this returns,
    /// <see cref="IsAvailable"/> is false until <see cref="InitializeAsync"/>
    /// is called again.
    /// </summary>
    public async Task UnloadModelAsync(CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Synchronous wrapper around <see cref="UnloadModelAsync"/> for callers
    /// that can't await (e.g. the existing GUI Whisper→Parakeet switch path).
    /// </summary>
    public void UnloadModel()
    {
        UnloadModelAsync().GetAwaiter().GetResult();
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
    /// 2. Create processor from factory with language settings
    /// 3. Process audio and collect segments
    /// 4. Return combined text
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="language">Language code (e.g., "en", "ja"). Null for auto-detect.</param>
    /// <returns>Transcribed text.</returns>
    public string TranscribeFile(string audioPath, string? language = null)
    {
        return TranscribeFileInternalAsync(audioPath, language, CancellationToken.None)
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
        LoggingService.Info($"  Audio Path: {audioPath}");
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
            LoggingService.Error($"Audio file not found: {audioPath}");
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
            // STEP 1: Prepare audio stream
            stepStopwatch.Restart();
            LoggingService.Debug("Step 1: Preparing audio stream...");
            using var audioStream = PrepareAudioStream(audioPath);
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

                builder.WithSegmentEventHandler((segment) =>
                    {
                        segmentCount++;
                        var segmentText = segment.Text ?? "";
                        text.Append(segmentText);
                        LoggingService.Debug($"  Segment: [{segment.Start:mm\\:ss} - {segment.End:mm\\:ss}] {segmentText.Trim()}");
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

                using var processor = builder.Build();
                stepStopwatch.Stop();
                LoggingService.Debug($"Step 2: Complete ({stepStopwatch.ElapsedMilliseconds}ms)");

                // STEP 3: Run transcription (segments collected via event handler above)
                stepStopwatch.Restart();
                LoggingService.Info("Step 3: Running transcription...");
                LoggingService.Debug($"  Start time: {DateTime.Now:HH:mm:ss.fff}");

                // Process audio - segments are collected via the event handler
                await foreach (var _ in processor
                    .ProcessAsync(audioStream, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                }

            stepStopwatch.Stop();
            var inferenceTimeMs = stepStopwatch.ElapsedMilliseconds;
            LoggingService.Info($"Step 3: Complete ({inferenceTimeMs}ms)");

            // Log warning if inference took unusually long
            if (inferenceTimeMs > 30000)
            {
                LoggingService.Warn($"  WARNING: Inference took {inferenceTimeMs}ms (>30s) - potential performance issue");
            }

            var rawText = text.ToString().Trim();

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

    /// <summary>
    /// Prepares an audio stream for Whisper transcription.
    /// Whisper.net requires a WAV stream with proper RIFF headers at 16kHz mono.
    ///
    /// CRITICAL: Whisper.net's Process(Stream) method expects a complete WAV file
    /// with RIFF headers, not raw PCM samples. This method uses NAudio's
    /// WaveFileWriter.WriteWavFileToStream() to ensure proper WAV formatting.
    ///
    /// See: https://github.com/sandrohanea/whisper.net/issues/154
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <returns>A MemoryStream containing a complete WAV file (16kHz mono 16-bit).</returns>
    private static Stream PrepareAudioStream(string audioPath)
    {
        using var reader = new AudioFileReader(audioPath);

        ISampleProvider provider = reader;

        // Log conversion info
        if (reader.WaveFormat.SampleRate == 16000 && reader.WaveFormat.Channels == 1)
        {
            LoggingService.Debug("  Audio already 16kHz mono, writing with WAV headers");
        }
        else
        {
            LoggingService.Debug($"  Resampling from {reader.WaveFormat.SampleRate}Hz {reader.WaveFormat.Channels}ch to 16kHz mono");
        }

        // Convert to mono if stereo
        if (reader.WaveFormat.Channels > 1)
        {
            provider = provider.ToMono();
        }

        // Resample to 16kHz if needed
        if (reader.WaveFormat.SampleRate != 16000)
        {
            provider = new WdlResamplingSampleProvider(provider, 16000);
        }

        // Write to MemoryStream with proper WAV headers (RIFF format)
        // This is critical - Whisper.net expects a complete WAV file, not raw samples
        // Pre-allocate MemoryStream to avoid LOH-triggering buffer doublings
        // 16kHz * 2 bytes/sample * 1 channel * duration + 44 byte WAV header
        long estimatedBytes = (long)(reader.TotalTime.TotalSeconds * 32000) + 44;
        var output = new MemoryStream((int)Math.Min(estimatedBytes, int.MaxValue));
        WaveFileWriter.WriteWavFileToStream(output, provider.ToWaveProvider16());
        output.Position = 0;

        LoggingService.Debug($"  Prepared WAV stream: {output.Length:N0} bytes");
        return output;
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
    /// Strategy: try NAudio's AudioFileReader (supports WAV/MP3/etc.). If that fails
    /// for any reason, fall back to a rough estimate from file size assuming 16kHz
    /// mono 16-bit PCM (the format we record in). If everything fails, return 0 so
    /// we default to the short-clip fast path (safer than wrongly enabling fallback
    /// retries on a legit short clip).
    /// </summary>
    private static double GetAudioDurationSeconds(string audioPath)
    {
        try
        {
            using var reader = new AudioFileReader(audioPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex)
        {
            LoggingService.Debug($"  Duration probe via AudioFileReader failed: {ex.Message}");

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
            // meant UnloadModel(); release the model without killing the
            // service.
            LoggingService.Debug("TranscriptionService: Dispose() called on shared instance — ignoring (use UnloadModel)");
            return;
        }
        LoggingService.Info("TranscriptionService: Disposing...");
        DisposeModel();
        _modelLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
