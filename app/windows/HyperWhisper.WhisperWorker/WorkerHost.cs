using System.Threading.Channels;
using HyperWhisper.LocalInference;
using Whisper.net.LibraryLoader;

namespace HyperWhisper.WhisperWorker;

/// <summary>
/// The worker's command loop. One reader takes lines off stdin and acts on
/// <c>cancel</c> and <c>shutdown</c> at once; one consumer runs <c>load</c> and
/// <c>transcribe</c> in order, so Whisper only ever sees one request. stdin EOF
/// ends the worker like <c>shutdown</c> does, so it dies with the app.
/// </summary>
internal sealed class WorkerHost
{
    private readonly ILocalWhisperService _service;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly IReadOnlyList<RuntimeLibrary> _runtimeOrder;
    private readonly bool _requestThreadFactory;
    private readonly Func<string?> _loadedRuntime;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<long> _cancelledBeforeStart = [];
    private long _currentId;
    private CancellationTokenSource? _current;
    private volatile bool _stopping;

    /// <param name="runtimeOrder">The Whisper.net runtime order, set before the first factory.</param>
    /// <param name="requestThreadFactory">
    /// The ARM64 thread rule (it used to live in the app's TranscriptionService):
    /// the load makes its one factory on a dedicated thread, and each
    /// transcription's builder runs on that thread too (one model read and one
    /// model copy per load).
    /// </param>
    /// <param name="log">
    /// The diagnostics writer. The stdin reader and the consumer both log, so it
    /// must be thread-safe (Program passes a <see cref="TextWriter.Synchronized"/> one).
    /// </param>
    /// <param name="loadedRuntime">The protocol name of the native runtime that loaded.</param>
    public WorkerHost(
        ILocalWhisperService service,
        TextWriter output,
        TextWriter log,
        IReadOnlyList<RuntimeLibrary> runtimeOrder,
        bool requestThreadFactory,
        Func<string?> loadedRuntime)
    {
        _service = service;
        _output = output;
        _log = log;
        _runtimeOrder = runtimeOrder;
        _requestThreadFactory = requestThreadFactory;
        _loadedRuntime = loadedRuntime;
    }

    /// <summary>How long the exit waits for a cancelled request to unwind.</summary>
    public TimeSpan ExitDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<int> RunAsync(TextReader input, int processId)
    {
        var work = Channel.CreateUnbounded<WhisperWorkerCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        await WriteAsync(WhisperWorkerProtocol.BuildReady(_loadedRuntime(), processId)).ConfigureAwait(false);
        var consumer = Task.Run(() => ConsumeAsync(work.Reader));

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    line = null;
                }

                if (line is null)
                {
                    Log("stdin closed; exiting");
                    break;
                }

                if (!WhisperWorkerProtocol.TryParseCommand(line, out var command, out var id, out var error))
                {
                    Log($"Rejected a command: {error}");
                    await WriteAsync(WhisperWorkerProtocol.BuildError(id, WhisperWorkerErrorCodes.Protocol, error ?? "bad command"))
                        .ConfigureAwait(false);
                    continue;
                }

                if (command is WhisperWorkerShutdownCommand)
                {
                    Log("shutdown requested; exiting");
                    break;
                }

                if (command is WhisperWorkerCancelCommand cancel)
                {
                    Cancel(cancel.Target);
                    continue;
                }

                work.Writer.TryWrite(command!);
            }
        }
        finally
        {
            _stopping = true;
            work.Writer.TryComplete();
            lock (_sync)
            {
                _current?.Cancel();
            }
        }

        try
        {
            await consumer.WaitAsync(ExitDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log("the running request did not stop in time; exiting without it");
            return 0;
        }

        try
        {
            await _service.DisposeAsync().AsTask().WaitAsync(ExitDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log($"dispose failed: {exception.GetType().Name}");
        }
        return 0;
    }

    private void Cancel(long target)
    {
        lock (_sync)
        {
            if (_current is not null && _currentId == target)
            {
                Log($"cancelling request {target}");
                _current.Cancel();
            }
            else
            {
                // Not started yet (or already answered): drop it if it starts.
                _cancelledBeforeStart.Add(target);
            }
        }
    }

    private async Task ConsumeAsync(ChannelReader<WhisperWorkerCommand> reader)
    {
        await foreach (var command in reader.ReadAllAsync().ConfigureAwait(false))
        {
            var id = command switch
            {
                WhisperWorkerLoadCommand load => load.Id,
                WhisperWorkerTranscribeCommand transcribe => transcribe.Id,
                _ => 0,
            };

            using var cts = new CancellationTokenSource();
            lock (_sync)
            {
                _currentId = id;
                _current = cts;
                if (_cancelledBeforeStart.Remove(id) || _stopping) cts.Cancel();
            }

            string reply;
            try
            {
                if (cts.IsCancellationRequested)
                {
                    reply = WhisperWorkerProtocol.BuildError(id, WhisperWorkerErrorCodes.Cancelled, "The request was cancelled.");
                }
                else
                {
                    reply = command switch
                    {
                        WhisperWorkerLoadCommand load => await LoadAsync(load, cts.Token).ConfigureAwait(false),
                        WhisperWorkerTranscribeCommand transcribe => await TranscribeAsync(transcribe, cts.Token).ConfigureAwait(false),
                        _ => WhisperWorkerProtocol.BuildError(id, WhisperWorkerErrorCodes.Protocol, "unsupported command"),
                    };
                }
            }
            catch (Exception exception)
            {
                Log($"request {id} failed: {exception}");
                reply = WhisperWorkerProtocol.BuildError(id, WhisperWorkerErrorCodes.Transcription, exception.Message);
            }
            finally
            {
                lock (_sync)
                {
                    _current = null;
                    _currentId = 0;
                }
            }

            try
            {
                await WriteAsync(reply).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log($"could not write the reply to request {id}: {exception.GetType().Name}");
            }
        }
    }

    private async Task<string> LoadAsync(WhisperWorkerLoadCommand load, CancellationToken cancellationToken)
    {
        Log($"load {Path.GetFileName(load.Model)} (gpuDevice {load.GpuDevice}, flashAttention {load.FlashAttention})");
        var started = System.Diagnostics.Stopwatch.StartNew();
        // Backend Vulkan only sets UseGpu, as the app always did; the order below
        // decides which native runtime loads, falling back to the CPU.
        var result = await _service.LoadAsync(
            new LocalWhisperLoadOptions(
                load.Model,
                LocalWhisperBackend.Vulkan,
                GpuDevice: load.GpuDevice,
                AllowCpuFallback: true,
                RuntimeOrder: _runtimeOrder,
                UseFlashAttention: load.FlashAttention,
                VerifyModelOnLoad: true,
                FactoryOnOwnThread: _requestThreadFactory),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var code = CodeFor(result.Failure!.Code);
            Log($"load failed after {started.ElapsedMilliseconds} ms: {code}");
            return WhisperWorkerProtocol.BuildError(load.Id, code, result.Failure.Message);
        }

        var runtime = _loadedRuntime() ?? "unknown";
        Log($"loaded in {started.ElapsedMilliseconds} ms; runtime {result.Runtime}");
        return WhisperWorkerProtocol.BuildLoadOk(
            load.Id,
            runtime,
            runtime == "vulkan" ? load.GpuName : null,
            result.Runtime);
    }

    private async Task<string> TranscribeAsync(WhisperWorkerTranscribeCommand transcribe, CancellationToken cancellationToken)
    {
        if (!_service.IsLoaded)
        {
            return WhisperWorkerProtocol.BuildError(transcribe.Id, WhisperWorkerErrorCodes.NotLoaded, "No Whisper model is loaded.");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await _service.TranscribeAsync(
            new LocalWhisperRequest(
                transcribe.Wav,
                Language: transcribe.Language,
                ThreadCount: transcribe.Options.Threads,
                SingleSegment: transcribe.Options.SingleSegment,
                IncludeWordTimestamps: false,
                MaxLastTextTokensZero: transcribe.Options.MaxLastTextTokensZero,
                CreateFactoryOnRequestThread: _requestThreadFactory,
                Prompt: transcribe.Options.Prompt),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var code = CodeFor(result.Failure!.Code);
            Log($"transcribe {transcribe.Id} failed after {started.ElapsedMilliseconds} ms: {code}");
            return WhisperWorkerProtocol.BuildError(transcribe.Id, code, result.Failure.Message);
        }

        // Sizes only, never words: the app's log ships in Export Diagnostics (#1645).
        Log($"transcribe {transcribe.Id}: {result.SegmentCount} segments, {result.Text!.Length} chars in {started.ElapsedMilliseconds} ms");
        return WhisperWorkerProtocol.BuildTranscribeOk(
            transcribe.Id,
            result.Text,
            result.UnstrippedText ?? result.Text,
            result.DetectedLanguage,
            result.SegmentCount);
    }

    internal static string CodeFor(LocalWhisperErrorCode code) => code switch
    {
        LocalWhisperErrorCode.ModelLoadFailed => WhisperWorkerErrorCodes.ModelFile,
        LocalWhisperErrorCode.RuntimeUnavailable => WhisperWorkerErrorCodes.Runtime,
        LocalWhisperErrorCode.InvalidRequest => WhisperWorkerErrorCodes.InvalidRequest,
        LocalWhisperErrorCode.Cancelled => WhisperWorkerErrorCodes.Cancelled,
        _ => WhisperWorkerErrorCodes.Transcription,
    };

    private async Task WriteAsync(string line)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(line).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Log(string message)
    {
        try
        {
            _log.WriteLine(message);
            _log.Flush();
        }
        catch
        {
            // stderr is gone with the app; nothing to tell.
        }
    }
}
