using System.Text;
using System.Text.Json;
using HyperWhisper.ModelManagement;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.Transcription;

namespace HyperWhisper.TranscriptionRouting;

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
                var response = await ReadLineAsync(_stdout!, _timeouts.Transcription, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        _gate.Wait();
        try { StopDaemonAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally { _gate.Release(); _gate.Dispose(); }
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
