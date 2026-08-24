using System.Text;
using System.Text.Json;
using HyperWhisper.LiveStreaming;
using HyperWhisper.ModelManagement;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.SharedCore;

namespace HyperWhisper.TranscriptionRouting;

/// <summary>
/// Portable JSON-lines client for local live Parakeet and Nemotron. Each live
/// session owns a daemon so a batch request cannot mutate its decoder state.
/// </summary>
public sealed class ParakeetDaemonLiveTranscriber : ILiveTranscriber
{
    private const int MaximumResponseCharacters = 512 * 1024 + 4096;
    private readonly INativeRuntimeLocator _runtime;
    private readonly IChildProcessLauncher _launcher;
    private readonly string _modelsRoot;
    private readonly string _vadModelPath;
    private readonly ILiveTranscriptSink? _transcripts;
    private readonly ParakeetDaemonTimeouts _timeouts;

    public ParakeetDaemonLiveTranscriber(
        INativeRuntimeLocator runtime,
        IChildProcessLauncher launcher,
        string modelsDirectory,
        ILiveTranscriptSink? transcripts = null,
        string? vadModelPath = null,
        ParakeetDaemonTimeouts? timeouts = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _modelsRoot = Path.GetFullPath(Path.Combine(
            modelsDirectory ?? throw new ArgumentNullException(nameof(modelsDirectory)), "Parakeet"));
        _transcripts = transcripts;
        _vadModelPath = Path.GetFullPath(vadModelPath ?? Path.Combine(
            AppContext.BaseDirectory, "parakeet-engine", "silero_vad.onnx"));
        _timeouts = timeouts ?? ParakeetDaemonTimeouts.Default;
    }

    public async Task<LiveTranscriptionResult> TranscribeAsync(
        LiveTranscriptionConfig config,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(audio);
        if (config.Provider is not (LiveTranscriptionProvider.ParakeetLocal or LiveTranscriptionProvider.NemotronLocal))
            return Failed(config.Provider, LiveTranscriptionFailureCode.InvalidRequest,
                "The local daemon cannot run the selected live provider.");

        var model = ResolveModel(config);
        if (model is null)
            return Failed(config.Provider, LiveTranscriptionFailureCode.InvalidRequest,
                "Choose a supported local live transcription model.");
        var directory = Path.GetFullPath(Path.Combine(_modelsRoot, model.StorageName));
        if (!directory.StartsWith(_modelsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !HasCompleteModel(model, directory))
            return Failed(config.Provider, LiveTranscriptionFailureCode.ProviderUnavailable,
                "The selected local live transcription model is not downloaded.");

        var executable = _runtime.FindExecutable("parakeet-engine");
        if (executable.IsFailure)
            return Failed(config.Provider, LiveTranscriptionFailureCode.ProviderUnavailable,
                "The packaged local transcription daemon is unavailable.");

        var arguments = new List<string>
        {
            "--model", directory,
            "--language", NormalizeLanguage(config.Language),
            "--engine", EngineFor(model.Id),
        };
        if (File.Exists(_vadModelPath))
        {
            arguments.Add("--vad-model");
            arguments.Add(_vadModelPath);
        }
        var launched = _launcher.Start(new ChildProcessStartRequest
        {
            ExecutablePath = executable.Value!,
            WorkingDirectory = Path.GetDirectoryName(executable.Value!)!,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (launched.IsFailure)
            return Failed(config.Provider, LiveTranscriptionFailureCode.ProviderUnavailable,
                "The local transcription daemon could not be started.");

        await using var process = launched.Value!;
        using var input = new StreamWriter(process.StandardInput!, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
        using var output = new StreamReader(process.StandardOutput!, new UTF8Encoding(false), false, leaveOpen: true);
        var stderr = process.StandardError!.CopyToAsync(Stream.Null);
        var chunks = 0;
        var messages = 0;
        try
        {
            var ready = await ReadResponseAsync(output, _timeouts.Startup, cancellationToken).ConfigureAwait(false);
            using (var document = JsonDocument.Parse(ready))
            {
                if (!HasString(document.RootElement, "status", "ready"))
                    return Failed(config.Provider, LiveTranscriptionFailureCode.ProviderUnavailable,
                        "The local transcription model could not be initialized.");
            }

            await SendAsync(input, new { command = "start" }, cancellationToken).ConfigureAwait(false);
            using (var started = JsonDocument.Parse(await ReadResponseAsync(
                output, _timeouts.Startup, cancellationToken).ConfigureAwait(false)))
            {
                messages++;
                if (!HasString(started.RootElement, "type", "started"))
                    return Failed(config.Provider, LiveTranscriptionFailureCode.Protocol,
                        "The local transcription daemon rejected the live session.", chunks, messages);
            }

            await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.IsEmpty) continue;
                if (chunk.Length > 128 * 1024 || (chunk.Length & 1) != 0)
                    return Failed(config.Provider, LiveTranscriptionFailureCode.BufferLimit,
                        "A local live PCM16 audio chunk was invalid or exceeded 128 KiB.", chunks, messages);
                await SendAsync(input, new { command = "audio", audio = Convert.ToBase64String(chunk.Span) },
                    cancellationToken).ConfigureAwait(false);
                using var response = JsonDocument.Parse(await ReadResponseAsync(
                    output, _timeouts.Transcription, cancellationToken).ConfigureAwait(false));
                chunks++;
                messages++;
                if (response.RootElement.TryGetProperty("error", out _))
                    return Failed(config.Provider, LiveTranscriptionFailureCode.Protocol,
                        "The local transcription daemon could not process live audio.", chunks, messages);
                Publish(response.RootElement);
            }

            await SendAsync(input, new { command = "finish" }, cancellationToken).ConfigureAwait(false);
            using var finished = JsonDocument.Parse(await ReadResponseAsync(
                output, _timeouts.Transcription, cancellationToken).ConfigureAwait(false));
            messages++;
            if (finished.RootElement.TryGetProperty("error", out _))
                return Failed(config.Provider, LiveTranscriptionFailureCode.Protocol,
                    "The local transcription daemon could not finish the live session.", chunks, messages);
            Publish(finished.RootElement);
            var transcript = GetText(finished.RootElement, "text");
            return transcript.Length == 0
                ? Failed(config.Provider, LiveTranscriptionFailureCode.NoSpeech,
                    "No speech was detected in the local live session.", chunks, messages)
                : new LiveTranscriptionResult(transcript, null, chunks, messages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelAsync(input, output).ConfigureAwait(false);
            return Failed(config.Provider, LiveTranscriptionFailureCode.Cancelled,
                "Local live transcription was cancelled.", chunks, messages);
        }
        catch (TimeoutException)
        {
            return Failed(config.Provider, LiveTranscriptionFailureCode.Timeout,
                "The local transcription daemon timed out.", chunks, messages);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return Failed(config.Provider, LiveTranscriptionFailureCode.Protocol,
                "The local transcription daemon connection failed.", chunks, messages);
        }
        finally
        {
            if (!process.HasExited) try { await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await stderr.ConfigureAwait(false); } catch { }
        }
    }

    private void Publish(JsonElement response)
    {
        var committed = GetText(response, "committed");
        if (committed.Length > 0) TryPublish(new LiveTranscriptUpdate(committed, true));
        var text = GetText(response, "text");
        if (text.Length > 0 && !HasTrue(response, "is_final")) TryPublish(new LiveTranscriptUpdate(text, false));
    }

    private void TryPublish(LiveTranscriptUpdate update)
    {
        try { _transcripts?.OnTranscript(update); } catch { }
    }

    private static async Task SendAsync(StreamWriter writer, object value, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadResponseAsync(
        StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string? line;
        try { line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException(); }
        if (line is null) throw new IOException("The local daemon closed its output.");
        if (line.Length > MaximumResponseCharacters) throw new InvalidDataException("The local daemon response was too large.");
        return line;
    }

    private static async Task TryCancelAsync(StreamWriter input, StreamReader output)
    {
        try
        {
            await SendAsync(input, new { command = "cancel" }, CancellationToken.None).ConfigureAwait(false);
            _ = await ReadResponseAsync(output, TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private static ManagedModel? ResolveModel(LiveTranscriptionConfig config)
    {
        var expected = config.Provider == LiveTranscriptionProvider.NemotronLocal
            ? "nemotron-3.5-ml-560ms"
            : config.Model?.Trim();
        if (config.Provider == LiveTranscriptionProvider.NemotronLocal
            && !string.IsNullOrWhiteSpace(config.Model)
            && !string.Equals(config.Model.Trim(), expected, StringComparison.OrdinalIgnoreCase)) return null;
        return PortableModelCatalog.Parakeet.FirstOrDefault(model =>
            string.Equals(model.Id, expected, StringComparison.OrdinalIgnoreCase)
            && (config.Provider != LiveTranscriptionProvider.ParakeetLocal
                || model.Id.StartsWith("parakeet-", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasCompleteModel(ManagedModel model, string directory) =>
        Directory.Exists(directory) && model.Artifacts.Count > 0 && model.Artifacts.All(artifact =>
        {
            var path = Path.GetFullPath(Path.Combine(directory, artifact.RelativePath));
            return path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(path) && new FileInfo(path).Length > 0;
        });

    private static string NormalizeLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
    private static string EngineFor(string model) => model == "nemotron-3.5-ml-560ms"
        ? "nemotron_ml" : "nemo_transducer";
    private static string GetText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()?.Trim() ?? "" : "";
    private static bool HasString(JsonElement value, string property, string expected) =>
        string.Equals(GetText(value, property), expected, StringComparison.Ordinal);
    private static bool HasTrue(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.True;
    private static LiveTranscriptionResult Failed(
        LiveTranscriptionProvider provider, LiveTranscriptionFailureCode code, string message,
        int chunks = 0, int messages = 0) =>
        new(null, new LiveTranscriptionFailure(code, message, provider), chunks, messages);
}
