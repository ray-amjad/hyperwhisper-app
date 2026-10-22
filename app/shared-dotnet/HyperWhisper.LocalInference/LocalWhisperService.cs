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
    private FactoryThread<WhisperFactory>? _requestFactoryThread;
    private int _requestFactoriesBuilt;
    private bool _verifiedOnLoad;
    private bool _disposed;

    // Set only by a load that succeeded; the factory is then either _factory or
    // the one the ARM64 rule's thread holds, never both.
    public bool IsLoaded => _runtime is not null;
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
                if (options.FactoryOnOwnThread)
                {
                    await LoadOnFactoryThreadAsync(
                        modelPath,
                        factoryOptions,
                        options.VerifyModelOnLoad,
                        () => runtimeLoaded = true,
                        cancellationToken).ConfigureAwait(false);
                }
                else
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
                }
                _modelPath = modelPath;
                _factoryOptions = factoryOptions;
                _verifiedOnLoad = options.VerifyModelOnLoad;
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
            if (_runtime is null || (_factory is null && _requestFactoryThread is null))
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
            WhisperProcessor Configure(WhisperProcessorBuilder builder)
            {
                builder.WithGreedySamplingStrategy();
                builder.WithThreads(Math.Clamp(request.ThreadCount ?? Math.Max(1, Environment.ProcessorCount - 1), 1, 64));
                builder.WithTemperature(0);
                builder.WithNoSpeechThreshold(0.6f);
                builder.WithEntropyThreshold(2.4f);
                builder.WithNoContext();
                if (request.MaxLastTextTokensZero) builder.WithMaxLastTextTokens(0);
                if (!string.IsNullOrEmpty(request.Prompt)) builder.WithPrompt(request.Prompt);
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
                return builder.Build();
            }

            // Once the ARM64 rule's thread exists it holds the only factory, so
            // every request goes through it (x64 and Linux never start it).
            var onFactoryThread = _requestFactoryThread is not null || request.CreateFactoryOnRequestThread;
            try
            {
                // The ARM64 thread rule: the factory, its builder and the processor
                // are all made on the one factory thread, which keeps the factory
                // across requests (one model read per load, not one per chunk).
                await using var processor = onFactoryThread
                    ? await RequestFactoryThread().RunAsync(factory =>
                        {
                            WhisperProcessorBuilder builder;
                            try
                            {
                                builder = factory.CreateBuilder();
                            }
                            catch (Exception exception)
                            {
                                throw new RequestFactoryFault(exception);
                            }
                            return Configure(builder);
                        }).ConfigureAwait(false)
                    : Configure(_factory!.CreateBuilder());
                await using var stream = new FileStream(
                    Path.GetFullPath(request.AudioPath),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                // Whisper.net reads only a 16 kHz WAV, so a PCM WAV at any other
                // rate (a 22,050 Hz Local API upload) is resampled here first
                // (issue #1007). Every other file goes to Whisper.net as before.
                var resampled = await Task.Run(
                    () => WaveResampler.TryReadAs16kMono(stream, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (resampled is null) stream.Position = 0;
                await foreach (var _ in (resampled is null
                        ? processor.ProcessAsync(stream, cancellationToken)
                        : processor.ProcessAsync(resampled, cancellationToken))
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
            catch (RequestFactoryFault fault)
            {
                return LocalWhisperResult.Failed(
                    ClassifyRequestFactoryFault(fault.InnerException!, _verifiedOnLoad),
                    "The Whisper model could not be loaded by the selected runtime.",
                    _runtime);
            }
            catch (Exception)
            {
                if (onFactoryThread && _requestFactoryThread is { } thread)
                {
                    // The old code never reused a factory after a failed file.
                    await thread.ResetAsync().ConfigureAwait(false);
                }
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

    /// <summary>
    /// A fault making the ARM64 rule's factory (or its builder) during a request.
    /// When the load verified the model, the file already read fine, so a later
    /// fault (out of memory, a native error) is the runtime's: calling it a broken
    /// model file would make the app mark a good model broken. Without a verified
    /// load this is the model's first read, so the load's own split applies.
    /// </summary>
    internal static LocalWhisperErrorCode ClassifyRequestFactoryFault(Exception exception, bool verifiedOnLoad) =>
        verifiedOnLoad
            ? LocalWhisperErrorCode.RuntimeUnavailable
            : ClassifyLoadFault(exception, runtimeLoaded: true);

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

    /// <summary>
    /// The ARM64 rule's load: the native library first (so a fault there is the
    /// runtime's), then the load's one factory, made and verified on its own
    /// thread. That thread keeps it for every request until the next load.
    /// </summary>
    private async Task LoadOnFactoryThreadAsync(
        string modelPath,
        WhisperFactoryOptions factoryOptions,
        bool verify,
        Action runtimeLoaded,
        CancellationToken cancellationToken)
    {
        await Task.Run(() => { _ = WhisperFactory.GetRuntimeInfo(); }, cancellationToken).ConfigureAwait(false);
        runtimeLoaded();
        var thread = NewFactoryThread(modelPath, factoryOptions);
        try
        {
            await thread.RunAsync(factory =>
            {
                if (verify) _ = factory.CreateBuilder();
                return true;
            }).ConfigureAwait(false);
        }
        catch
        {
            thread.Dispose();
            throw;
        }
        _requestFactoryThread = thread;
    }

    /// <summary>
    /// The ARM64 thread rule's factory thread for the loaded model. A load with
    /// <see cref="LocalWhisperLoadOptions.FactoryOnOwnThread"/> made it already;
    /// otherwise the first request that asks for the rule makes it here and drops
    /// the load's factory, so the model is held once. Linux and x64 never start it.
    /// </summary>
    private FactoryThread<WhisperFactory> RequestFactoryThread()
    {
        if (_requestFactoryThread is { } existing) return existing;
        _factory?.Dispose();
        _factory = null;
        return _requestFactoryThread = NewFactoryThread(_modelPath!, _factoryOptions!.Value);
    }

    private FactoryThread<WhisperFactory> NewFactoryThread(string modelPath, WhisperFactoryOptions factoryOptions) =>
        new(
            () =>
            {
                try
                {
                    Interlocked.Increment(ref _requestFactoriesBuilt);
                    return WhisperFactory.FromPath(modelPath, factoryOptions);
                }
                catch (Exception exception)
                {
                    throw new RequestFactoryFault(exception);
                }
            },
            "Whisper factory (ARM64 thread rule)");

    /// <summary>Test seam: how many factories the ARM64 rule has built (or tried to) in this service's life.</summary>
    internal int RequestFactoriesBuilt => Volatile.Read(ref _requestFactoriesBuilt);

    /// <summary>Test seam: how many model factories this service holds now (the load's and the ARM64 thread's).</summary>
    internal int FactoriesHeld => (_factory is null ? 0 : 1) + (_requestFactoryThread?.HoldsFactory == true ? 1 : 0);

    /// <summary>A fault in making the ARM64 rule's factory or its builder: a load fault, not a transcription one.</summary>
    private sealed class RequestFactoryFault(Exception inner) : Exception(inner.Message, inner);

    private Task DisposeFactoryAsync()
    {
        _requestFactoryThread?.Dispose();
        _requestFactoryThread = null;
        _factory?.Dispose();
        _factory = null;
        _runtime = null;
        _modelPath = null;
        _factoryOptions = null;
        _verifiedOnLoad = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns an integer PCM WAV at any sample rate but 16 kHz into the 16 kHz mono
/// samples Whisper.net needs. Whisper.net's own reader refuses every other rate
/// ("Only 16KHz sample rate is supported."), and the service reported that as a
/// bare "Local Whisper transcription failed." (issue #1007).
/// </summary>
internal static class WaveResampler
{
    internal const int TargetRate = 16_000;

    // Windowed-sinc kernel: zero crossings each side, and table points per crossing.
    private const int ZeroCrossings = 8;
    private const int TableResolution = 512;
    private static readonly float[] Kernel = BuildKernel();

    /// <summary>
    /// The file's audio at 16 kHz mono, or null when Whisper.net should read the
    /// file itself, as it always did: a 16 kHz WAV, a file that is not a RIFF
    /// WAVE, or a format Whisper.net refuses at any rate (float, 12-bit). Channels
    /// are averaged, as Whisper.net does. Reads from the stream's current position.
    /// </summary>
    internal static float[]? TryReadAs16kMono(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (!TryReadFormat(reader, out var channels, out var sampleRate, out var bitsPerSample, out var dataBytes))
            return null;
        if (sampleRate == TargetRate) return null;
        var frameSize = channels * (bitsPerSample / 8);
        // A streamed WAV can declare more data than it holds; read what is there.
        if (stream.CanSeek) dataBytes = Math.Min(dataBytes, Math.Max(0, stream.Length - stream.Position));
        var frames = dataBytes / frameSize;
        var source = new float[4096];
        var output = new float[(int)Math.Ceiling(frames * (double)TargetRate / sampleRate)];
        var resampler = new Streaming(sampleRate, frames, output);
        var raw = new byte[frameSize * 4096];
        long read = 0;
        while (read < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(4096, frames - read);
            var got = ReadFull(stream, raw, want * frameSize) / frameSize;
            if (got == 0) break;
            for (var frame = 0; frame < got; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                    sum += Sample(raw, (frame * channels + channel) * (bitsPerSample / 8), bitsPerSample);
                source[frame] = sum / channels;
            }
            resampler.Push(source.AsSpan(0, got));
            read += got;
        }
        return resampler.Finish(read);
    }

    /// <summary>Resamples a whole buffer at once (the test seam for the filter).</summary>
    internal static float[] Resample(ReadOnlySpan<float> source, int sampleRate)
    {
        var output = new float[(int)Math.Ceiling(source.Length * (double)TargetRate / sampleRate)];
        var resampler = new Streaming(sampleRate, source.Length, output);
        resampler.Push(source);
        return resampler.Finish(source.Length);
    }

    private static bool TryReadFormat(
        BinaryReader reader,
        out int channels,
        out int sampleRate,
        out int bitsPerSample,
        out long dataBytes)
    {
        channels = sampleRate = bitsPerSample = 0;
        dataBytes = 0;
        try
        {
            if (new string(reader.ReadChars(4)) != "RIFF") return false;
            _ = reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") return false;
            var haveFormat = false;
            while (true)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                if (id == "data")
                {
                    if (!haveFormat) return false;
                    dataBytes = size;
                    return true;
                }
                if (id == "fmt " && size >= 16)
                {
                    var format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = (int)Math.Min(reader.ReadUInt32(), int.MaxValue);
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                    var rest = size - 16;
                    // WAVE_FORMAT_EXTENSIBLE carries the real format in its
                    // sub-format GUID; only PCM's is read here, as in Whisper.net.
                    if (format == 0xFFFE && rest >= 24)
                    {
                        _ = reader.ReadBytes(8);
                        format = reader.ReadUInt16();
                        if (!reader.ReadBytes(14).AsSpan().SequenceEqual(PcmSubFormatTail)) return false;
                        rest -= 24;
                    }
                    if (format != 1 || channels == 0 || sampleRate is < 1_000 or > 768_000
                        || bitsPerSample is not (8 or 16 or 24 or 32))
                        return false;
                    haveFormat = true;
                    Skip(reader, rest + (size & 1));
                    continue;
                }
                Skip(reader, size + (size & 1));
            }
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    // KSDATAFORMAT_SUBTYPE_PCM after its first two bytes: 0000-0010-8000-00aa00389b71.
    private static readonly byte[] PcmSubFormatTail =
        [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private static void Skip(BinaryReader reader, long bytes)
    {
        var stream = reader.BaseStream;
        if (stream.CanSeek)
        {
            if (stream.Position + bytes > stream.Length) throw new EndOfStreamException();
            stream.Seek(bytes, SeekOrigin.Current);
            return;
        }
        for (; bytes > 0; bytes -= 4096)
            if (reader.ReadBytes((int)Math.Min(bytes, 4096)).Length == 0) throw new EndOfStreamException();
    }

    private static int ReadFull(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var got = stream.Read(buffer, total, count - total);
            if (got == 0) break;
            total += got;
        }
        return total;
    }

    private static float Sample(byte[] raw, int offset, int bitsPerSample) => bitsPerSample switch
    {
        8 => (raw[offset] - 128) / 128f,
        16 => BitConverter.ToInt16(raw, offset) / 32768f,
        24 => ((raw[offset] | raw[offset + 1] << 8 | raw[offset + 2] << 16) << 8 >> 8) / 8388608f,
        _ => BitConverter.ToInt32(raw, offset) / 2147483648f,
    };

    private static float[] BuildKernel()
    {
        var table = new float[ZeroCrossings * TableResolution + 2];
        for (var i = 0; i < table.Length; i++)
        {
            var x = (double)i / TableResolution;
            if (x >= ZeroCrossings) break;
            var sinc = i == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            // Blackman window over [-ZeroCrossings, ZeroCrossings].
            var w = 0.42 + 0.5 * Math.Cos(Math.PI * x / ZeroCrossings) + 0.08 * Math.Cos(2 * Math.PI * x / ZeroCrossings);
            table[i] = (float)(sinc * w);
        }
        return table;
    }

    /// <summary>
    /// A windowed-sinc resampler fed in blocks. Going down, the kernel is
    /// stretched to the 16 kHz Nyquist, so speech above 8 kHz does not alias
    /// back into the band Whisper hears. Each output is normalised by its own
    /// weight sum, so the first and last few milliseconds keep their level.
    /// </summary>
    private sealed class Streaming(int sampleRate, long totalFrames, float[] output)
    {
        private readonly double _step = (double)sampleRate / TargetRate;
        // Filter units per source sample: below 1 when going down (low-pass at 8 kHz).
        private readonly double _scale = Math.Min(1.0, (double)TargetRate / sampleRate);
        private readonly double _reach = ZeroCrossings / Math.Min(1.0, (double)TargetRate / sampleRate);
        private float[] _buffer = new float[4096];
        private long _bufferStart;
        private int _bufferCount;
        private int _next;

        public void Push(ReadOnlySpan<float> samples)
        {
            if (_bufferCount + samples.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _bufferCount + samples.Length));
            samples.CopyTo(_buffer.AsSpan(_bufferCount));
            _bufferCount += samples.Length;
            Emit(final: false);
        }

        public float[] Finish(long framesRead)
        {
            Emit(final: true);
            // A file shorter than its header said ends where its audio ends.
            var produced = (int)Math.Min(_next, Math.Ceiling(framesRead / _step));
            return produced == output.Length ? output : output[..produced];
        }

        private void Emit(bool final)
        {
            var available = _bufferStart + _bufferCount;
            while (_next < output.Length)
            {
                var center = _next * _step;
                var last = (long)Math.Floor(center + _reach);
                if (!final && last >= available && available < totalFrames) break;
                var first = Math.Max(_bufferStart, (long)Math.Ceiling(center - _reach));
                last = Math.Min(last, available - 1);
                double sum = 0, weights = 0;
                for (var index = first; index <= last; index++)
                {
                    var position = Math.Abs(index - center) * _scale * TableResolution;
                    var slot = (int)position;
                    if (slot >= ZeroCrossings * TableResolution) continue;
                    var fraction = (float)(position - slot);
                    var weight = Kernel[slot] + (Kernel[slot + 1] - Kernel[slot]) * fraction;
                    sum += weight * _buffer[index - _bufferStart];
                    weights += weight;
                }
                output[_next++] = weights > 0 ? (float)(sum / weights) : 0f;
            }
            // Keep only the samples the next output can still reach.
            var keepFrom = Math.Min(available, Math.Max(_bufferStart, (long)Math.Ceiling(_next * _step - _reach)));
            var drop = (int)(keepFrom - _bufferStart);
            if (drop > 0)
            {
                Array.Copy(_buffer, drop, _buffer, 0, _bufferCount - drop);
                _bufferCount -= drop;
                _bufferStart = keepFrom;
            }
        }
    }
}
