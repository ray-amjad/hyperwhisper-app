using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyperWhisper.LocalApi;

public sealed record PortableLocalApiOptions(
    string Token,
    int Port = 51671,
    long MaxRequestBytes = 52_428_800,
    int MaxUploadBytes = 50_331_648,
    int MaxTextCharacters = 131_072,
    IReadOnlyList<string>? AllowedFileRoots = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token)) throw new ArgumentException("A bearer token is required.", nameof(Token));
        if (Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (MaxRequestBytes <= 0 || MaxUploadBytes <= 0 || MaxUploadBytes > MaxRequestBytes) throw new ArgumentOutOfRangeException(nameof(MaxUploadBytes));
        if (MaxTextCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(MaxTextCharacters));
    }
}

public static class PortableLocalApi
{
    public static WebApplication Build(string[] args, PortableLocalApiOptions options, ILocalApiBackend backend, Action<WebApplicationBuilder>? configure = null)
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = options.MaxRequestBytes;
            server.Listen(IPAddress.Loopback, options.Port);
        });
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = options.MaxRequestBytes;
            form.ValueLengthLimit = 16_384;
            form.MemoryBufferThreshold = options.MaxUploadBytes;
        });
        builder.Services.AddSingleton(backend);
        configure?.Invoke(builder);
        var app = builder.Build();
        Map(app, options);
        return app;
    }

    public static void Map(WebApplication app, PortableLocalApiOptions options)
    {
        app.MapGet("/health", async (HttpContext context, ILocalApiBackend backend, CancellationToken ct) =>
        {
            var health = await backend.GetHealthAsync(ct);
            return Results.Ok(new { ok = true, app_version = health.AppVersion, api_version = 1, port = context.Connection.LocalPort, pid = Environment.ProcessId, health.Providers, post_processing_providers = health.PostProcessingProviders, local_models = health.LocalModels });
        });

        // DNS-rebind guard — runs before EVERY route, including the
        // unauthenticated /health, and before the bearer check. This head had
        // no such guard until issue #289; this is macOS's, shared through
        // hw-localapi rather than transliterated a third time. The guard runs
        // first on purpose: checking the token first would tell an
        // unauthenticated rebound page whether its guess was right.
        app.Use(async (context, next) =>
        {
            if (!LocalApiOriginGuard.IsAllowed(context, LocalApiOriginGuard.ResolvePort(context, options)))
            {
                await LocalApiSharedFailure.WriteForbiddenOriginAsync(context);
                return;
            }
            await next(context);
        });

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
            // The header parse moved into the shared core with the compare
            // (issue #289): this head used to match the `Bearer ` prefix here
            // and hash only the remainder, which is one of the three ways the
            // platforms disagreed.
            if (!LocalApiTokenStore.Authorize(context.Request.Headers.Authorization.ToString(), options.Token))
            {
                await LocalApiSharedFailure.WriteUnauthorizedAsync(context);
                return;
            }
            try
            {
                await next(context);
            }
            catch (ArgumentException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request contains invalid or conflicting values.")));
            }
            catch (BadHttpRequestException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request body is invalid.")));
            }
            catch (JsonException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON.")));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) throw;
                // `CANCELLED` was never in the closed set, and 408 was never a
                // status the docs allowed for a business outcome (issue #289).
                // `TIMEOUT` is the documented code for "ran out of time".
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.Timeout, "The request was cancelled.")));
            }
            catch (InvalidOperationException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.EngineUnavailable, "The requested application capability is unavailable.")));
            }
        });

        app.MapGet("/models", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, models = await b.GetModelsAsync(ct) }));
        app.MapGet("/modes", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, modes = await b.GetModesAsync(ct) }));
        app.MapGet("/modes/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.GetModeAsync(id, ct) is { } mode ? Results.Ok(new { ok = true, mode }) : Failure(200, LocalApiErrorCodes.ModeNotFound, "Mode not found."));
        app.MapPost("/modes", async (HttpContext context, ILocalApiBackend b, CancellationToken ct) =>
        {
            var body = await ReadJsonObject(context, ct);
            return body is null ? Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON.") : Results.Ok(new { ok = true, mode = await b.CreateModeAsync(body.Value, ct) });
        });
        app.MapPatch("/modes/{id}", async (string id, HttpContext context, ILocalApiBackend b, CancellationToken ct) =>
        {
            var body = await ReadJsonObject(context, ct);
            if (body is null) return Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON.");
            return await b.PatchModeAsync(id, body.Value, ct) is { } mode ? Results.Ok(new { ok = true, mode }) : Failure(200, LocalApiErrorCodes.ModeNotFound, "Mode not found.");
        });
        app.MapDelete("/modes/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.DeleteModeAsync(id, ct) ? Results.Ok(new { ok = true }) : Failure(200, LocalApiErrorCodes.ModeNotFound, "Mode not found."));
        app.MapPost("/recording/toggle", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, recording = await b.ToggleRecordingAsync(ct) }));
        app.MapPost("/recording/cancel", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, recording = await b.CancelRecordingAsync(ct) }));
        app.MapPost("/post-process", async (HttpContext context, ILocalApiBackend b, CancellationToken ct) =>
        {
            PostProcessRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<PostProcessRequest>(cancellationToken: ct); }
            catch (JsonException) { return Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON."); }
            if (request is null) return Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is required.");
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > options.MaxTextCharacters)
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "Post-processing text is empty or exceeds the configured limit.");
            var hasMode = !string.IsNullOrWhiteSpace(request.ModeId);
            var hasPreset = !string.IsNullOrWhiteSpace(request.Preset);
            var hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt);
            if (hasPreset && hasPrompt)
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "'preset' and 'prompt' are mutually exclusive.");
            if (!hasMode && !hasPreset && !hasPrompt)
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "Provide at least one of 'mode_id', 'preset', or 'prompt'.");
            var result = await b.PostProcessAsync(request, ct);
            return Results.Ok(new
            {
                ok = true,
                text = result.Text,
                provider = result.Provider,
                model = result.Model,
                preset = result.Preset,
                latency_ms = result.LatencyMs,
            });
        });
        app.MapPost("/transcribe", Transcribe);
        app.MapGet("/recordings", ListRecordings);
        app.MapGet("/recordings/search", ListRecordings);
        app.MapGet("/recordings/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.GetRecordingAsync(id, ct) is { } recording ? Results.Ok(new { ok = true, recording }) : Failure(200, LocalApiErrorCodes.ModeNotFound, "Recording not found."));

        async Task<IResult> Transcribe(HttpContext context, ILocalApiBackend backend, CancellationToken ct)
        {
            if (context.Request.ContentLength > options.MaxRequestBytes) return Failure(200, LocalApiErrorCodes.InvalidRequest, "Request exceeds the configured limit.");
            if (!context.Request.HasFormContentType)
                return await TranscribeJson(context, backend, ct).ConfigureAwait(false);
            try
            {
                var form = await context.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("audio");
                if (file is null || file.Length == 0) return Failure(400, LocalApiErrorCodes.InvalidRequest, "A non-empty 'audio' file is required.");
                if (file.Length > options.MaxUploadBytes) return Failure(200, LocalApiErrorCodes.InvalidRequest, "Audio exceeds the configured upload limit.");
                var name = file.FileName;
                if (name != Path.GetFileName(name)
                    || name.Contains("..", StringComparison.Ordinal)
                    || name.IndexOfAny(['/', '\\']) >= 0)
                    return Failure(400, LocalApiErrorCodes.FileNotAllowed, "Audio filename must not contain a path.");
                await using var input = file.OpenReadStream();
                using var output = new MemoryStream((int)file.Length);
                await input.CopyToAsync(output, ct);
                var result = await backend.TranscribeAsync(new(
                    name,
                    file.ContentType,
                    output.ToArray(),
                    form["mode_id"],
                    form["engine"],
                    form["model"],
                    form["language"],
                    TimestampGranularities: form["timestamp_granularities"]
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .ToArray()), ct);
                return TranscriptionSuccess(result);
            }
            catch (InvalidDataException) { return Failure(200, LocalApiErrorCodes.InvalidRequest, "Request exceeds the configured limit."); }
        }

        async Task<IResult> TranscribeJson(HttpContext context, ILocalApiBackend backend, CancellationToken ct)
        {
            if (context.Request.ContentType is null
                || !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return Failure(400, LocalApiErrorCodes.InvalidRequest,
                    "application/json with 'file' or 'audio_base64' is required.");
            TranscribeJsonRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<TranscribeJsonRequest>(cancellationToken: ct); }
            catch (JsonException) { return Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON."); }
            if (request is null) return Failure(400, LocalApiErrorCodes.InvalidRequest, "The request body is required.");
            if (string.IsNullOrWhiteSpace(request.ModeId)
                && string.IsNullOrWhiteSpace(request.Engine)
                && string.IsNullOrWhiteSpace(request.Provider))
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "Provide 'mode_id' or 'engine'.");
            var hasFile = !string.IsNullOrWhiteSpace(request.File);
            var hasBase64 = !string.IsNullOrWhiteSpace(request.AudioBase64);
            if (hasFile == hasBase64)
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "Pass exactly one of 'file' or 'audio_base64'.");

            byte[] content;
            string fileName;
            string contentType;
            if (hasBase64)
            {
                var encoded = request.AudioBase64!.Trim();
                if (encoded.Length > ((long)options.MaxUploadBytes + 2) / 3 * 4)
                    return Failure(200, LocalApiErrorCodes.InvalidRequest, "Audio exceeds the configured upload limit.");
                try { content = Convert.FromBase64String(encoded); }
                catch (FormatException) { return Failure(400, LocalApiErrorCodes.InvalidRequest, "'audio_base64' is not valid base64."); }
                if (content.Length == 0)
                    return Failure(400, LocalApiErrorCodes.InvalidRequest, "Audio must not be empty.");
                if (content.Length > options.MaxUploadBytes)
                    return Failure(200, LocalApiErrorCodes.InvalidRequest, "Audio exceeds the configured upload limit.");
                contentType = NormalizeMime(request.MimeType);
                fileName = "local-api-upload" + ExtensionForMime(contentType);
            }
            else
            {
                var opened = await ReadAllowedFileAsync(request.File!, options, ct).ConfigureAwait(false);
                if (opened.Error is not null) return opened.Error;
                content = opened.Content!;
                fileName = opened.FileName!;
                contentType = string.IsNullOrWhiteSpace(request.MimeType)
                    ? MimeForExtension(Path.GetExtension(fileName)) : NormalizeMime(request.MimeType);
            }

            var result = await backend.TranscribeAsync(new(
                fileName, contentType, content, request.ModeId,
                request.Engine ?? request.Provider, request.Model, request.Language,
                request.ApplicationContext, request.TimestampGranularities), ct).ConfigureAwait(false);
            return TranscriptionSuccess(result);
        }

        async Task<IResult> ListRecordings(HttpContext context, ILocalApiBackend backend, CancellationToken ct)
        {
            var limit = int.TryParse(context.Request.Query["limit"], CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 500) : 50;
            _ = DateTime.TryParse(context.Request.Query["since"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since);
            _ = DateTime.TryParse(context.Request.Query["until"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until);
            var rows = await backend.GetRecordingsAsync(new(context.Request.Query["q"], since == default ? null : since, until == default ? null : until, limit), ct);
            return Results.Ok(new { ok = true, total = rows.Count, returned = rows.Count, recordings = rows });
        }

        static async Task<JsonElement?> ReadJsonObject(HttpContext context, CancellationToken ct)
        {
            try
            {
                var body = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                return body.ValueKind == JsonValueKind.Object ? body : null;
            }
            catch (JsonException) { return null; }
        }

        static IResult TranscriptionSuccess(TranscriptionResult result)
        {
            var response = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["text"] = result.Text,
                ["engine"] = result.Engine,
                ["model"] = result.Model,
                ["language"] = result.Language,
                ["timings"] = new { load_ms = result.LoadMs, decode_ms = result.DecodeMs },
                ["latency_ms"] = result.LatencyMs,
            };
            if (result.RawText is not null) response["raw_text"] = result.RawText;
            if (result.Segments is not null) response["segments"] = result.Segments;
            if (result.Words is not null) response["words"] = result.Words;
            return Results.Ok(response);
        }
    }

    private sealed record TranscribeJsonRequest(
        string? File,
        [property: JsonPropertyName("audio_base64")] string? AudioBase64,
        [property: JsonPropertyName("mime_type")] string? MimeType,
        [property: JsonPropertyName("mode_id")] string? ModeId,
        string? Engine,
        [property: JsonPropertyName("timestamp_granularities")] string[]? TimestampGranularities,
        string? Provider,
        string? Model,
        string? Language,
        LocalApiApplicationContext? ApplicationContext);

    private sealed record AllowedFileRead(byte[]? Content, string? FileName, IResult? Error);

    private static async Task<AllowedFileRead> ReadAllowedFileAsync(
        string requestedPath, PortableLocalApiOptions options, CancellationToken cancellationToken)
    {
        string path;
        try { path = Path.GetFullPath(requestedPath.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(null, null, Failure(400, LocalApiErrorCodes.InvalidRequest, "The 'file' field is not a valid path.")); }
        if (!Path.IsPathFullyQualified(requestedPath.Trim()) || !IsAllowedPath(path, options.AllowedFileRoots))
            return new(null, null, Failure(400, LocalApiErrorCodes.FileNotAllowed,
                "The 'file' path is outside HyperWhisper's private audio folders."));
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length <= 0)
                return new(null, null, Failure(400, LocalApiErrorCodes.InvalidRequest, "Audio must not be empty."));
            if (input.Length > options.MaxUploadBytes)
                return new(null, null, Failure(200, LocalApiErrorCodes.InvalidRequest, "Audio exceeds the configured upload limit."));
            using var output = new MemoryStream((int)input.Length);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return new(output.ToArray(), Path.GetFileName(path), null);
        }
        catch (FileNotFoundException)
        { return new(null, null, Failure(400, LocalApiErrorCodes.FileNotAllowed, "The 'file' path is unavailable.")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(null, null, Failure(400, LocalApiErrorCodes.FileNotAllowed, "The 'file' path is unavailable.")); }
    }

    private static bool IsAllowedPath(string path, IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0) return false;
        try
        {
            foreach (var rawRoot in roots)
            {
                if (string.IsNullOrWhiteSpace(rawRoot)) continue;
                var root = Path.GetFullPath(rawRoot);
                var relative = Path.GetRelativePath(root, path);
                if (relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathFullyQualified(relative)) continue;
                var cursor = root;
                foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    cursor = Path.Combine(cursor, component);
                    if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) return false;
                }
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return false;
    }

    private static string NormalizeMime(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "audio/mpeg",
        "audio/mp4" or "audio/x-m4a" => "audio/mp4",
        "audio/flac" or "audio/x-flac" => "audio/flac",
        "audio/ogg" => "audio/ogg",
        "audio/webm" => "audio/webm",
        _ => "audio/wav",
    };
    private static string ExtensionForMime(string mime) => mime switch
    { "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/flac" => ".flac", "audio/ogg" => ".ogg", "audio/webm" => ".webm", _ => ".wav" };
    private static string MimeForExtension(string extension) => extension.ToLowerInvariant() switch
    { ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".webm" => "audio/webm", _ => "audio/wav" };

    private static IResult Failure(int status, string code, string message) => Results.Json(new LocalApiFailure(new(code, message)), statusCode: status);
}

public static class LocalApiBindFallback
{
    public static async Task<int> BindAsync(int preferredPort, Func<int, CancellationToken, Task> start, CancellationToken cancellationToken = default)
    {
        try { await start(preferredPort, cancellationToken); return preferredPort; }
        catch (Exception ex) when (preferredPort != 0 && IsBindFailure(ex)) { await start(0, cancellationToken); return 0; }
    }

    internal static bool IsBindFailure(Exception ex) => ex is SocketException
        || ex.InnerException is SocketException
        || ex.InnerException is not null && IsBindFailure(ex.InnerException)
        || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
}
