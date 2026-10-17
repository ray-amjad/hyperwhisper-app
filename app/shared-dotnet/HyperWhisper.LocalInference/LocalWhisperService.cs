using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace HyperWhisper.LocalInference;

/// <summary>
/// Cross-platform Whisper.net host. Runtime selection is process-wide in
/// Whisper.net, so one service instance owns the factory and serializes work.
/// </summary>
public sealed class LocalWhisperService : ILocalWhisperService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _runtime;
    private string? _modelPath;
    private WhisperFactoryOptions? _factoryOptions;
    private bool _disposed;

    public bool IsLoaded => _factory is not null;
    public string? Runtime => _runtime;

    public async Task<LocalWhisperResult> LoadAsync(
        LocalWhisperLoadOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.ModelPath))
        {
            return LocalWhisperResult.Failed(
                LocalWhisperErrorCode.InvalidRequest,
                "The Whisper model file does not exist.");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocalWhisperResult.Failed(LocalWhisperErrorCode.Cancelled, "Model loading was cancelled.");
        }
        try
        {
            await DisposeFactoryAsync().ConfigureAwait(false);
            ConfigureRuntime(options);
            var modelPath = Path.GetFullPath(options.ModelPath);
            var factoryOptions = FactoryOptionsFor(options);
            // Set inside the load: true once the native library is up, so a
            // fault before that point is the runtime's and not the model file's.
            var runtimeLoaded = false;
            try
            {
                _factory = await Task.Run(
                    () =>
                    {
                        // Loads the native library alone, before any model is read.
                        _ = WhisperFactory.GetRuntimeInfo();
                        runtimeLoaded = true;
                        var factory = WhisperFactory.FromPath(modelPath, factoryOptions);
                        if (options.VerifyModelOnLoad)
                        {
                            try
                            {
                                _ = factory.CreateBuilder();
                            }
                            catch
                            {
                                factory.Dispose();
                                throw;
                            }
                        }
                        return factory;
                    },
                    cancellationToken).ConfigureAwait(false);
                _modelPath = modelPath;
                _factoryOptions = factoryOptions;
                _runtime = $"{RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}: "
                    + (WhisperFactory.GetRuntimeInfo() ?? "runtime information unavailable");
                return LocalWhisperResult.Success(string.Empty, _runtime);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return LocalWhisperResult.Failed(LocalWhisperErrorCode.Cancelled, "Model loading was cancelled.");
            }
            catch (Exception exception)
            {
                await DisposeFactoryAsync().ConfigureAwait(false);
                // The message stays the same for both codes: the Linux head shows
                // it, and only the code is new (#1598 across the worker boundary).
                return LocalWhisperResult.Failed(
                    ClassifyLoadFault(exception, runtimeLoaded),
                    "The Whisper model could not be loaded by the selected runtime.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalWhisperResult> TranscribeAsync(
        LocalWhisperRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.AudioPath))
        {
            return LocalWhisperResult.Failed(
                LocalWhisperErrorCode.InvalidRequest,
                "The audio file does not exist.",
                _runtime);
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocalWhisperResult.Failed(
                LocalWhisperErrorCode.Cancelled,
                "Transcription was cancelled.",
                _runtime);
        }
        try
        {
            if (_factory is null || _runtime is null)
            {
                return LocalWhisperResult.Failed(
                    LocalWhisperErrorCode.RuntimeUnavailable,
                    "No Whisper model is loaded.");
            }

            var text = new StringBuilder();
            var segments = new List<LocalWhisperSegmentTimestamp>();
            var words = new List<LocalWhisperWordTimestamp>();
            var segmentCount = 0;
            string? detectedLanguage = null;
            WhisperFactory? requestFactory = null;
            WhisperProcessorBuilder? requestBuilder = null;
            if (request.CreateFactoryOnRequestThread)
            {
                // The ARM64 thread rule: this factory and its builder are made on
                // the same thread, with nothing awaited in between.
                try
                {
                    requestFactory = WhisperFactory.FromPath(_modelPath!, _factoryOptions!.Value);
                    requestBuilder = requestFactory.CreateBuilder();
                }
                catch (Exception exception)
                {
                    requestFactory?.Dispose();
                    return LocalWhisperResult.Failed(
                        ClassifyLoadFault(exception, runtimeLoaded: true),
                        "The Whisper model could not be loaded by the selected runtime.",
                        _runtime);
                }
            }
            try
            {
                var builder = requestBuilder ?? _factory.CreateBuilder();
                builder.WithGreedySamplingStrategy();
                builder.WithThreads(Math.Clamp(request.ThreadCount ?? Math.Max(1, Environment.ProcessorCount - 1), 1, 64));
                builder.WithTemperature(0);
                builder.WithNoSpeechThreshold(0.6f);
                builder.WithEntropyThreshold(2.4f);
                builder.WithNoContext();
                if (request.MaxLastTextTokensZero) builder.WithMaxLastTextTokens(0);
                if (request.IncludeWordTimestamps) builder.WithTokenTimestamps();
                builder.WithSegmentEventHandler(segment =>
                {
                    segmentCount++;
                    detectedLanguage ??= segment.Language;
                    text.Append(segment.Text);
                    if (!request.IncludeWordTimestamps) return;
                    // A segment of only non-speech markers is not speech, so it
                    // gets no segment or word timestamps (issue #1742).
                    if (WhisperNonSpeechMarkers.Strip(segment.Text).Length == 0) return;
                    segments.Add(new(
                        segments.Count,
                        segment.Start.TotalSeconds,
                        segment.End.TotalSeconds,
                        segment.Text));
                    foreach (var token in segment.Tokens)
                    {
                        var word = token.Text?.Trim() ?? string.Empty;
                        if (word.Length == 0 || word.StartsWith("<|", StringComparison.Ordinal)
                            || token.Start < 0 || token.End < token.Start) continue;
                        words.Add(new(
                            word,
                            token.Start / 100d,
                            token.End / 100d,
                            float.IsFinite(token.Probability) ? token.Probability : null));
                    }
                });
                if (request.SingleSegment)
                {
                    builder.WithTemperatureInc(0).WithSingleSegment();
                }
                else
                {
                    builder.WithTemperatureInc(0.2f).WithLogProbThreshold(-1.0f);
                }
                if (string.IsNullOrWhiteSpace(request.Language))
                {
                    builder.WithLanguageDetection();
                }
                else
                {
                    builder.WithLanguage(request.Language.Trim());
                }

                await using var processor = builder.Build();
                await using var stream = new FileStream(
                    Path.GetFullPath(request.AudioPath),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await foreach (var _ in processor
                    .ProcessAsync(stream, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                }
                // Whisper writes its own non-speech markers (a blank-audio tag and kin)
                // as segment text. A silent file or mic must come back empty, so
                // the workflow reports no speech instead of saving or pasting the
                // marker as a transcript (issue #1742, the Linux sibling of #1449).
                var rawText = WhisperNonSpeechMarkers.Strip(text.ToString());
                var timestamps = request.IncludeWordTimestamps && rawText.Length > 0
                    && (segments.Count > 0 || words.Count > 0)
                    ? new LocalWhisperTimestamps(segments, words, rawText)
                    : null;
                return LocalWhisperResult.Success(rawText, _runtime, timestamps) with
                {
                    UnstrippedText = text.ToString(),
                    DetectedLanguage = detectedLanguage,
                    SegmentCount = segmentCount,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return LocalWhisperResult.Failed(
                    LocalWhisperErrorCode.Cancelled,
                    "Transcription was cancelled.",
                    _runtime);
            }
            catch (Exception)
            {
                return LocalWhisperResult.Failed(
                    LocalWhisperErrorCode.TranscriptionFailed,
                    "Local Whisper transcription failed.",
                    _runtime);
            }
            finally
            {
                // After the processor: `await using` disposed it when the try ended.
                requestFactory?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeFactoryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static void ConfigureRuntime(LocalWhisperLoadOptions options)
    {
        if (RuntimeOptions.LoadedLibrary is not null)
        {
            return;
        }
        RuntimeOptions.RuntimeLibraryOrder = RuntimeOrderFor(options);
    }

    internal static List<RuntimeLibrary> RuntimeOrderFor(LocalWhisperLoadOptions options) =>
        options.RuntimeOrder is { Count: > 0 } order
            ? [.. order]
            : RuntimeOrderFor(options.Backend, options.AllowCpuFallback);

    internal static WhisperFactoryOptions FactoryOptionsFor(LocalWhisperLoadOptions options) => new()
    {
        UseGpu = options.Backend != LocalWhisperBackend.Cpu,
        UseFlashAttention = options.UseFlashAttention ?? options.Backend != LocalWhisperBackend.Cpu,
        GpuDevice = Math.Max(0, options.GpuDevice),
    };

    /// <summary>
    /// A load that failed before the native library was up, or with a missing,
    /// wrong-architecture or incomplete native DLL anywhere in the exception
    /// chain, is the runtime's fault: a new model download cannot fix it, so it
    /// must not read as a broken model file (#1598).
    /// </summary>
    internal static LocalWhisperErrorCode ClassifyLoadFault(Exception exception, bool runtimeLoaded) =>
        !runtimeLoaded || IsNativeRuntimeFault(exception)
            ? LocalWhisperErrorCode.RuntimeUnavailable
            : LocalWhisperErrorCode.ModelLoadFailed;

    internal static bool IsNativeRuntimeFault(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }
        }
        return false;
    }

    internal static List<RuntimeLibrary> RuntimeOrderFor(
        LocalWhisperBackend backend,
        bool allowCpuFallback) => backend switch
        {
            LocalWhisperBackend.Cuda12 when allowCpuFallback =>
                [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu],
            LocalWhisperBackend.Cuda12 => [RuntimeLibrary.Cuda12],
            LocalWhisperBackend.Vulkan when allowCpuFallback =>
                [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            LocalWhisperBackend.Vulkan => [RuntimeLibrary.Vulkan],
            _ => [RuntimeLibrary.Cpu],
        };

    private Task DisposeFactoryAsync()
    {
        _factory?.Dispose();
        _factory = null;
        _runtime = null;
        _modelPath = null;
        _factoryOptions = null;
        return Task.CompletedTask;
    }
}
