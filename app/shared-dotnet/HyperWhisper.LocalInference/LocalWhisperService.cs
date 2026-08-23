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
            try
            {
                _factory = await Task.Run(
                    () => WhisperFactory.FromPath(
                        Path.GetFullPath(options.ModelPath),
                        new WhisperFactoryOptions
                        {
                            UseGpu = options.Backend != LocalWhisperBackend.Cpu,
                            UseFlashAttention = options.Backend != LocalWhisperBackend.Cpu,
                            GpuDevice = Math.Max(0, options.GpuDevice),
                        }),
                    cancellationToken).ConfigureAwait(false);
                _runtime = $"{RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}: "
                    + (WhisperFactory.GetRuntimeInfo() ?? "runtime information unavailable");
                return LocalWhisperResult.Success(string.Empty, _runtime);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return LocalWhisperResult.Failed(LocalWhisperErrorCode.Cancelled, "Model loading was cancelled.");
            }
            catch (Exception)
            {
                await DisposeFactoryAsync().ConfigureAwait(false);
                return LocalWhisperResult.Failed(
                    LocalWhisperErrorCode.ModelLoadFailed,
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
            try
            {
                var builder = _factory.CreateBuilder();
                builder.WithGreedySamplingStrategy();
                builder.WithThreads(Math.Clamp(request.ThreadCount ?? Math.Max(1, Environment.ProcessorCount - 1), 1, 64));
                builder.WithTemperature(0);
                builder.WithNoSpeechThreshold(0.6f);
                builder.WithEntropyThreshold(2.4f);
                builder.WithNoContext();
                if (request.IncludeWordTimestamps) builder.WithTokenTimestamps();
                builder.WithSegmentEventHandler(segment =>
                {
                    text.Append(segment.Text);
                    if (!request.IncludeWordTimestamps) return;
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
                var rawText = text.ToString().Trim();
                var timestamps = request.IncludeWordTimestamps && (segments.Count > 0 || words.Count > 0)
                    ? new LocalWhisperTimestamps(segments, words, rawText)
                    : null;
                return LocalWhisperResult.Success(rawText, _runtime, timestamps);
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
        RuntimeOptions.RuntimeLibraryOrder = RuntimeOrderFor(options.Backend, options.AllowCpuFallback);
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
        return Task.CompletedTask;
    }
}
