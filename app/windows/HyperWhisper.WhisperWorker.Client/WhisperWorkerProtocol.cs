using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HyperWhisper.WhisperWorker;

/// <summary>
/// The error codes a worker reply carries, plus the ones the client raises
/// itself. Only <see cref="ModelFile"/> means "the model file is broken"; the
/// app marks a model broken on that code alone (#1598).
/// </summary>
public static class WhisperWorkerErrorCodes
{
    /// <summary>The model file was read and could not be loaded. A new download fixes it.</summary>
    public const string ModelFile = "model_file";

    /// <summary>The native Whisper runtime could not load. A new download cannot fix it.</summary>
    public const string Runtime = "runtime";

    /// <summary>A missing model or audio file, or a request the worker cannot use.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>A transcribe arrived before any successful load.</summary>
    public const string NotLoaded = "not_loaded";

    /// <summary>Whisper failed while decoding.</summary>
    public const string Transcription = "transcription";

    /// <summary>The request was cancelled by a <c>cancel</c> command.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The worker could not parse the command line.</summary>
    public const string Protocol = "protocol";

    /// <summary>Client side: the worker exited while a request was running, twice.</summary>
    public const string Crashed = "crashed";

    /// <summary>Client side: the worker exe could not be started.</summary>
    public const string StartFailed = "start_failed";

    /// <summary>Client side: the worker did not say ready in time.</summary>
    public const string Timeout = "timeout";
}

/// <summary>The decoding switches the app sends with each transcribe.</summary>
/// <param name="Prompt">
/// The Whisper initial prompt (the vocabulary words, #1564), or null for none.
/// Optional on the wire: a line without it, from an older app, decodes with no prompt.
/// </param>
public sealed record WhisperWorkerTranscribeOptions(
    bool SingleSegment,
    bool MaxLastTextTokensZero,
    int? Threads,
    string? Prompt = null);

/// <summary>A command the app sends to the worker, one JSON object per line.</summary>
public abstract record WhisperWorkerCommand;

public sealed record WhisperWorkerLoadCommand(
    long Id,
    string Model,
    int GpuDevice,
    bool FlashAttention,
    string? GpuName) : WhisperWorkerCommand;

public sealed record WhisperWorkerTranscribeCommand(
    long Id,
    string Wav,
    string? Language,
    WhisperWorkerTranscribeOptions Options) : WhisperWorkerCommand;

public sealed record WhisperWorkerCancelCommand(long Id, long Target) : WhisperWorkerCommand;

public sealed record WhisperWorkerShutdownCommand : WhisperWorkerCommand;

/// <summary>A line the worker writes to the app.</summary>
public abstract record WhisperWorkerMessage;

/// <summary>The first line of a worker: it is up and reads commands.</summary>
public sealed record WhisperWorkerReady(string? Runtime, int Pid) : WhisperWorkerMessage;

/// <summary>
/// The reply to one command. On success the fields of that command's reply are
/// set; on failure <see cref="Code"/> and <see cref="Message"/> are.
/// </summary>
public sealed record WhisperWorkerReply(
    long Id,
    bool Ok,
    string? Code = null,
    string? Message = null,
    string? Runtime = null,
    string? Gpu = null,
    string? Info = null,
    string? Text = null,
    string? RawText = null,
    string? Language = null,
    int SegmentCount = 0) : WhisperWorkerMessage;

/// <summary>
/// Builds and parses the JSON lines both sides exchange over stdio. One object
/// per line, UTF-8, no BOM. Every builder escapes with the default encoder, so
/// no line can carry a raw newline.
/// </summary>
public static class WhisperWorkerProtocol
{
    // ---- app → worker ----------------------------------------------------

    public static string BuildCommand(WhisperWorkerCommand command) => Write(writer =>
    {
        switch (command)
        {
            case WhisperWorkerLoadCommand load:
                writer.WriteNumber("id", load.Id);
                writer.WriteString("cmd", "load");
                writer.WriteString("model", load.Model);
                writer.WriteNumber("gpuDevice", load.GpuDevice);
                writer.WriteBoolean("flashAttention", load.FlashAttention);
                if (load.GpuName is not null) writer.WriteString("gpuName", load.GpuName);
                break;
            case WhisperWorkerTranscribeCommand transcribe:
                writer.WriteNumber("id", transcribe.Id);
                writer.WriteString("cmd", "transcribe");
                writer.WriteString("wav", transcribe.Wav);
                if (transcribe.Language is null) writer.WriteNull("language");
                else writer.WriteString("language", transcribe.Language);
                writer.WriteStartObject("options");
                writer.WriteBoolean("singleSegment", transcribe.Options.SingleSegment);
                writer.WriteBoolean("maxLastTextTokensZero", transcribe.Options.MaxLastTextTokensZero);
                if (transcribe.Options.Threads is { } threads) writer.WriteNumber("threads", threads);
                if (transcribe.Options.Prompt is { } prompt) writer.WriteString("prompt", prompt);
                writer.WriteEndObject();
                break;
            case WhisperWorkerCancelCommand cancel:
                writer.WriteNumber("id", cancel.Id);
                writer.WriteString("cmd", "cancel");
                writer.WriteNumber("target", cancel.Target);
                break;
            case WhisperWorkerShutdownCommand:
                writer.WriteString("cmd", "shutdown");
                break;
            default:
                throw new ArgumentException($"Unknown command {command.GetType().Name}", nameof(command));
        }
    });

    /// <summary>
    /// Parses one command line. On failure <paramref name="error"/> says why and
    /// <paramref name="id"/> carries the request id when the line had one, so the
    /// worker can still answer that request.
    /// </summary>
    public static bool TryParseCommand(
        string? line,
        out WhisperWorkerCommand? command,
        out long? id,
        out string? error)
    {
        command = null;
        id = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "empty line";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "a command must be a JSON object";
                return false;
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var parsedId))
            {
                id = parsedId;
            }

            var cmd = GetString(root, "cmd");
            switch (cmd)
            {
                case "shutdown":
                    command = new WhisperWorkerShutdownCommand();
                    return true;
                case "load":
                {
                    var model = GetString(root, "model");
                    if (id is null || string.IsNullOrEmpty(model))
                    {
                        error = "load needs an id and a model";
                        return false;
                    }
                    command = new WhisperWorkerLoadCommand(
                        id.Value,
                        model,
                        GetInt(root, "gpuDevice") ?? 0,
                        GetBool(root, "flashAttention") ?? true,
                        GetString(root, "gpuName"));
                    return true;
                }
                case "transcribe":
                {
                    var wav = GetString(root, "wav");
                    if (id is null || string.IsNullOrEmpty(wav))
                    {
                        error = "transcribe needs an id and a wav";
                        return false;
                    }
                    bool singleSegment = true;
                    bool maxLastTextTokensZero = false;
                    int? threads = null;
                    string? prompt = null;
                    if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
                    {
                        singleSegment = GetBool(options, "singleSegment") ?? true;
                        maxLastTextTokensZero = GetBool(options, "maxLastTextTokensZero") ?? false;
                        threads = GetInt(options, "threads");
                        prompt = GetString(options, "prompt");
                    }
                    command = new WhisperWorkerTranscribeCommand(
                        id.Value,
                        wav,
                        GetString(root, "language"),
                        new WhisperWorkerTranscribeOptions(singleSegment, maxLastTextTokensZero, threads, prompt));
                    return true;
                }
                case "cancel":
                {
                    var target = GetLong(root, "target");
                    if (target is null)
                    {
                        error = "cancel needs a target";
                        return false;
                    }
                    command = new WhisperWorkerCancelCommand(id ?? 0, target.Value);
                    return true;
                }
                default:
                    error = cmd is null ? "the command has no cmd" : $"unknown cmd '{cmd}'";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "the line is not JSON";
            return false;
        }
    }

    // ---- worker → app ----------------------------------------------------

    public static string BuildReady(string? runtime, int pid) => Write(writer =>
    {
        writer.WriteString("status", "ready");
        if (runtime is null) writer.WriteNull("runtime");
        else writer.WriteString("runtime", runtime);
        writer.WriteNumber("pid", pid);
    });

    public static string BuildLoadOk(long id, string runtime, string? gpu, string? info) => Write(writer =>
    {
        writer.WriteNumber("id", id);
        writer.WriteBoolean("ok", true);
        writer.WriteString("runtime", runtime);
        if (gpu is null) writer.WriteNull("gpu");
        else writer.WriteString("gpu", gpu);
        if (info is not null) writer.WriteString("info", info);
    });

    public static string BuildTranscribeOk(long id, string text, string rawText, string? language, int segmentCount) =>
        Write(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteBoolean("ok", true);
            writer.WriteString("text", text);
            writer.WriteString("rawText", rawText);
            if (language is null) writer.WriteNull("language");
            else writer.WriteString("language", language);
            writer.WriteNumber("segmentCount", segmentCount);
        });

    /// <summary>An error reply. A line with no id (it could not be parsed) gets id 0.</summary>
    public static string BuildError(long? id, string code, string message) => Write(writer =>
    {
        writer.WriteNumber("id", id ?? 0);
        writer.WriteBoolean("ok", false);
        writer.WriteString("code", code);
        writer.WriteString("message", message);
    });

    /// <summary>
    /// Parses one worker line. A line that is not a protocol object (a native
    /// library printing to stdout, say) returns false, and the caller logs it.
    /// </summary>
    public static bool TryParseMessage(string? line, out WhisperWorkerMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (GetString(root, "status") == "ready")
            {
                message = new WhisperWorkerReady(GetString(root, "runtime"), GetInt(root, "pid") ?? 0);
                return true;
            }

            var id = GetLong(root, "id");
            var ok = GetBool(root, "ok");
            if (id is null || ok is null)
            {
                return false;
            }

            message = new WhisperWorkerReply(
                id.Value,
                ok.Value,
                Code: GetString(root, "code"),
                Message: GetString(root, "message"),
                Runtime: GetString(root, "runtime"),
                Gpu: GetString(root, "gpu"),
                Info: GetString(root, "info"),
                Text: GetString(root, "text"),
                RawText: GetString(root, "rawText"),
                Language: GetString(root, "language"),
                SegmentCount: GetInt(root, "segmentCount") ?? 0);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;
}
