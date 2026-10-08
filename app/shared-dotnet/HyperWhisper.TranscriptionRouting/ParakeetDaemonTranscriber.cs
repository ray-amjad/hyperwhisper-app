using System.Text;
using System.Text.Json;
using HyperWhisper.ModelManagement;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.TranscriptionRouting;

/// <param name="Transcription">
/// The response-timeout FLOOR for one whole-file request: the whole budget for a short or
/// unreadable file. The real wait grows with the audio length (#1569); see
/// <see cref="ParakeetDaemonTranscriber.ComputeResponseTimeout"/>.
/// </param>
public sealed record ParakeetDaemonTimeouts(
    TimeSpan Startup,
    TimeSpan Transcription,
    TimeSpan Shutdown)
{
    public static ParakeetDaemonTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(3));
}

/// <summary>Serialized JSON-lines client for the packaged Parakeet daemon.</summary>
public sealed class ParakeetDaemonTranscriber : IRecordedAudioTranscriber, IDisposable
{
    private readonly INativeRuntimeLocator _runtime;
    private readonly IChildProcessLauncher _launcher;
    private readonly string _modelsRoot;
    private readonly string _vadModelPath;
    private readonly ParakeetDaemonTimeouts _timeouts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Cancelled by Dispose once the in-flight response has had the floor (#1569): the
    // scaled wait can be hours, and Dispose blocks its (UI) thread on the gate.
    private readonly CancellationTokenSource _disposeCts = new();
    private IChildProcess? _process;
    private StreamReader? _stdout;
    private StreamWriter? _stdin;
    private Task? _stderrDrain;
    private string? _loadedModel;
    private string? _loadedLanguage;
    private string? _provider;
    private bool _disposed;

    public ParakeetDaemonTranscriber(
        INativeRuntimeLocator runtime,
        IChildProcessLauncher launcher,
        string modelsDirectory,
        string? vadModelPath = null,
        ParakeetDaemonTimeouts? timeouts = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _modelsRoot = Path.GetFullPath(Path.Combine(
            modelsDirectory ?? throw new ArgumentNullException(nameof(modelsDirectory)), "Parakeet"));
        _vadModelPath = Path.GetFullPath(vadModelPath ?? Path.Combine(
            AppContext.BaseDirectory, "parakeet-engine", "silero_vad.onnx"));
        _timeouts = timeouts ?? ParakeetDaemonTimeouts.Default;
    }

    /// <summary>
    /// The hard ceiling on one request's response timeout. It only guards against a
    /// nonsense duration from a broken header; no real input reaches it before several
    /// hours of audio.
    /// </summary>
    internal static readonly TimeSpan MaxResponseTimeout = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to wait for the daemon's one reply to an <c>audio_path</c> request (#1569,
    /// the Linux side of Windows #1562).
    ///
    /// The daemon answers once, for the whole file, so the wait must grow with the audio:
    /// <c>floor + audioSeconds * floor / 30</c>. With the default 180 s floor that is 6 s of
    /// wait per audio second, so a 600 s file gets 3,780 s. A short clip keeps the floor.
    /// An unknown, zero, negative or non-finite duration falls back to the floor alone,
    /// and the 24 h <see cref="MaxResponseTimeout"/> bounds a nonsense header.
    /// </summary>
    internal static TimeSpan ComputeResponseTimeout(TimeSpan floor, double? audioSeconds)
    {
        if (audioSeconds is not { } seconds || !double.IsFinite(seconds) || seconds <= 0)
            return floor;
        if (floor >= MaxResponseTimeout) return floor;
        var scaledSeconds = floor.TotalSeconds + seconds * floor.TotalSeconds / 30.0;
        return scaledSeconds >= MaxResponseTimeout.TotalSeconds
            ? MaxResponseTimeout
            : TimeSpan.FromSeconds(scaledSeconds);
    }

    public TranscriptionBackendCapability Capability
    {
        get
        {
            var executable = _runtime.FindExecutable("parakeet-engine");
            return executable.IsSuccess
                ? new(true, "Parakeet (packaged daemon)")
                : new(false, "Parakeet", "The packaged Parakeet daemon is unavailable.");
        }
    }

    public Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? language,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PortableTranscriptionResult.Failed(
            PortableTranscriptionErrorCode.InvalidRequest,
            "Parakeet requires a selected mode and model.",
            "Parakeet"));

    public async Task<PortableTranscriptionResult> TranscribeAsync(
        string audioPath,
        TranscriptionWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(audioPath))
            return Failure(PortableTranscriptionErrorCode.InvalidRequest, "The audio file does not exist.");
        var mode = request.SelectedMode;
        var modelId = mode?.LocalParakeetModel?.Trim();
        var model = PortableModelCatalog.Parakeet.FirstOrDefault(item =>
            string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return Failure(PortableTranscriptionErrorCode.InvalidRequest, "Choose a supported Parakeet model.");

        var modelDirectory = Path.GetFullPath(Path.Combine(_modelsRoot, model.StorageName));
        if (!modelDirectory.StartsWith(_modelsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !HasCompleteModel(model, modelDirectory))
            return Failure(PortableTranscriptionErrorCode.BackendUnavailable, "The selected Parakeet model is not downloaded.");

        var language = NormalizeLanguage(request.Language ?? mode?.Language);
        // Read before the gate: only the header is parsed, and an unreadable or non-WAV
        // file gives null, which keeps the fixed floor.
        var responseTimeout = ComputeResponseTimeout(
            _timeouts.Transcription, WaveFileDuration.TryReadSeconds(audioPath));
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return Failure(PortableTranscriptionErrorCode.Cancelled, "Parakeet transcription was cancelled."); }

        try
        {
            try
            {
                if (!IsReusable(model.Id, language))
                {
                    await StopDaemonAsync(CancellationToken.None).ConfigureAwait(false);
                    var started = await StartDaemonAsync(model, modelDirectory, language, cancellationToken).ConfigureAwait(false);
                    if (started is not null) return started;
                }

                var payload = JsonSerializer.Serialize(new { audio_path = Path.GetFullPath(audioPath) });
                await _stdin!.WriteLineAsync(payload).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                string? response;
                using (var responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token))
                    response = await ReadLineAsync(_stdout!, responseTimeout, responseCts.Token).ConfigureAwait(false);
                if (response is null)
                {
                    await StopDaemonAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure(PortableTranscriptionErrorCode.TranscriptionFailed, "The Parakeet daemon stopped unexpectedly.");
                }

                using var document = JsonDocument.Parse(response);
                if (document.RootElement.TryGetProperty("text", out var text)
                    && !string.IsNullOrWhiteSpace(text.GetString()))
                    return PortableTranscriptionResult.Success(
                        text.GetString()!.Trim(),
                        $"Parakeet {model.Id} ({_provider ?? "cpu"})");
                return Failure(PortableTranscriptionErrorCode.TranscriptionFailed,
                    document.RootElement.TryGetProperty("error", out _)
                        ? "The Parakeet daemon could not transcribe the audio."
                        : "The Parakeet daemon returned no speech.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                || _disposeCts.IsCancellationRequested)
            {
                await StopDaemonAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                return Failure(PortableTranscriptionErrorCode.Cancelled, "Parakeet transcription was cancelled.");
            }
            catch (TimeoutException)
            {
                await StopDaemonAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                return Failure(PortableTranscriptionErrorCode.TranscriptionFailed, "The Parakeet daemon timed out.");
            }
            catch (JsonException)
            {
                await StopDaemonAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                return Failure(PortableTranscriptionErrorCode.TranscriptionFailed, "The Parakeet daemon returned an invalid response.");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                await StopDaemonAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                return Failure(PortableTranscriptionErrorCode.TranscriptionFailed, "The Parakeet daemon connection failed.");
            }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // As on main, an in-flight request gets up to the floor to finish; past that it is
        // cancelled, so teardown never waits out a long file's scaled budget (#1569, as
        // Windows #1562 keeps its teardown wait at the engine floor).
        _disposeCts.CancelAfter(_timeouts.Transcription);
        _gate.Wait();
        try { StopDaemonAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { _gate.Release(); _gate.Dispose(); _disposeCts.Dispose(); }
    }

    private async Task<PortableTranscriptionResult?> StartDaemonAsync(
        ManagedModel model,
        string modelDirectory,
        string language,
        CancellationToken cancellationToken)
    {
        var executable = _runtime.FindExecutable("parakeet-engine");
        if (executable.IsFailure)
            return Failure(PortableTranscriptionErrorCode.BackendUnavailable, "The packaged Parakeet daemon is unavailable.");

        var arguments = new List<string>
        {
            "--model", modelDirectory,
            // The packaged .NET daemon derives both language hints and TDT
            // no-space joining from --language.
            "--language", language,
        };
        if (File.Exists(_vadModelPath))
        {
            arguments.Add("--vad-model");
            arguments.Add(_vadModelPath);
        }
        arguments.Add("--engine");
        arguments.Add(EngineFor(model.Id));
        var started = _launcher.Start(new ChildProcessStartRequest
        {
            ExecutablePath = executable.Value!,
            WorkingDirectory = Path.GetDirectoryName(executable.Value!)!,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (started.IsFailure)
            return Failure(PortableTranscriptionErrorCode.BackendUnavailable, "The Parakeet daemon could not be started.");

        _process = started.Value!;
        _stdin = new StreamWriter(_process.StandardInput!, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _stdout = new StreamReader(_process.StandardOutput!, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _stderrDrain = _process.StandardError!.CopyToAsync(Stream.Null);

        var ready = await ReadLineAsync(_stdout, StartupTimeout(model.Id), cancellationToken).ConfigureAwait(false);
        if (ready is null)
        {
            await StopDaemonAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure(PortableTranscriptionErrorCode.BackendUnavailable, "The Parakeet daemon exited during startup.");
        }
        using var document = JsonDocument.Parse(ready);
        if (!document.RootElement.TryGetProperty("status", out var status)
            || status.GetString() != "ready")
        {
            await StopDaemonAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure(PortableTranscriptionErrorCode.BackendUnavailable, "The Parakeet daemon failed to initialize.");
        }
        _provider = document.RootElement.TryGetProperty("provider", out var provider)
            ? provider.GetString() : "cpu";
        _loadedModel = model.Id;
        _loadedLanguage = language;
        return null;
    }

    private bool IsReusable(string model, string language) => _process is { HasExited: false }
        && string.Equals(_loadedModel, model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase);

    private async Task StopDaemonAsync(CancellationToken cancellationToken, bool force = false)
    {
        var process = _process;
        _process = null;
        _loadedModel = null;
        _loadedLanguage = null;
        _provider = null;
        if (process is null) return;
        try
        {
            if (force && !process.HasExited)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else if (!process.HasExited && _stdin is not null)
            {
                await _stdin.WriteLineAsync("{\"command\":\"quit\"}").ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shutdown.CancelAfter(_timeouts.Shutdown);
                try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false); }
            }
        }
        catch { try { await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false); } catch { } }
        finally
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            _stdin = null;
            _stdout = null;
            await process.DisposeAsync().ConfigureAwait(false);
            if (_stderrDrain is not null) { try { await _stderrDrain.ConfigureAwait(false); } catch { } }
            _stderrDrain = null;
        }
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try { return await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(); }
    }

    private TimeSpan StartupTimeout(string model) => model switch
    {
        "qwen3-asr-0.6b" => _timeouts.Startup,
        "nemotron-3.5-ml-560ms" => Min(_timeouts.Startup, TimeSpan.FromSeconds(45)),
        _ => Min(_timeouts.Startup, TimeSpan.FromSeconds(30)),
    };

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private static string EngineFor(string model) => model switch
    {
        "qwen3-asr-0.6b" => "qwen3",
        "nemotron-3.5-ml-560ms" => "nemotron_ml",
        _ => "nemo_transducer",
    };
    private static bool HasCompleteModel(ManagedModel model, string directory)
    {
        if (!Directory.Exists(directory)) return false;
        if (model.Layout == ManagedModelLayout.FixedFiles)
            return model.Artifacts.Count > 0 && model.Artifacts.All(artifact =>
            {
                var path = Path.Combine(directory, artifact.RelativePath);
                return File.Exists(path) && new FileInfo(path).Length > 0;
            });
        if (model.Layout != ManagedModelLayout.HuggingFaceTree) return false;
        bool Has(string prefix) => Directory.EnumerateFiles(
            directory, prefix + "*.onnx", SearchOption.TopDirectoryOnly).Any(path => new FileInfo(path).Length > 0);
        var tokenizer = Path.Combine(directory, "tokenizer");
        return Has("conv_frontend") && Has("encoder") && Has("decoder")
            && Directory.Exists(tokenizer)
            && Directory.EnumerateFiles(tokenizer, "*", SearchOption.AllDirectories)
                .Any(path => new FileInfo(path).Length > 0);
    }
    private static string NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto" : language.Trim().ToLowerInvariant();
    private PortableTranscriptionResult Failure(PortableTranscriptionErrorCode code, string message) =>
        PortableTranscriptionResult.Failed(code, message,
            _loadedModel is null ? "Parakeet" : $"Parakeet {_loadedModel} ({_provider ?? "cpu"})");
}
