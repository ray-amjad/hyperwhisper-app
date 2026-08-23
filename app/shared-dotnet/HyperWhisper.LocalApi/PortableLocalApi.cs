using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
    int MaxTextCharacters = 131_072)
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
        app.MapGet("/health", async (ILocalApiBackend backend, CancellationToken ct) =>
        {
            var health = await backend.GetHealthAsync(ct);
            return Results.Ok(new { ok = true, app_version = health.AppVersion, api_version = 1, port = options.Port, pid = Environment.ProcessId, health.Providers, post_processing_providers = health.PostProcessingProviders, local_models = health.LocalModels });
        });

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !LocalApiTokenStore.FixedTimeEquals(header[prefix.Length..], options.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new LocalApiFailure(new("UNAUTHORIZED", "A valid bearer token is required.")));
                return;
            }
            await next(context);
        });

        app.MapGet("/models", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, models = await b.GetModelsAsync(ct) }));
        app.MapGet("/modes", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, modes = await b.GetModesAsync(ct) }));
        app.MapGet("/modes/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.GetModeAsync(id, ct) is { } mode ? Results.Ok(new { ok = true, mode }) : Failure(404, LocalApiErrorCodes.ModeNotFound, "Mode not found."));
        app.MapPost("/modes", async (JsonElement body, ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, mode = await b.CreateModeAsync(body, ct) }));
        app.MapPatch("/modes/{id}", async (string id, JsonElement body, ILocalApiBackend b, CancellationToken ct) => await b.PatchModeAsync(id, body, ct) is { } mode ? Results.Ok(new { ok = true, mode }) : Failure(404, LocalApiErrorCodes.ModeNotFound, "Mode not found."));
        app.MapDelete("/modes/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.DeleteModeAsync(id, ct) ? Results.Ok(new { ok = true }) : Failure(404, LocalApiErrorCodes.ModeNotFound, "Mode not found."));
        app.MapPost("/recording/toggle", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, recording = await b.ToggleRecordingAsync(ct) }));
        app.MapPost("/recording/cancel", async (ILocalApiBackend b, CancellationToken ct) => Results.Ok(new { ok = true, recording = await b.CancelRecordingAsync(ct) }));
        app.MapPost("/post-process", async (PostProcessRequest request, ILocalApiBackend b, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > options.MaxTextCharacters)
                return Failure(400, LocalApiErrorCodes.InvalidRequest, "Post-processing text is empty or exceeds the configured limit.");
            var result = await b.PostProcessAsync(request, ct);
            return Results.Ok(new { ok = true, result.Text, result.Provider, result.Model, result.Preset, latency_ms = result.LatencyMs });
        });
        app.MapPost("/transcribe", Transcribe);
        app.MapGet("/recordings", ListRecordings);
        app.MapGet("/recordings/search", ListRecordings);
        app.MapGet("/recordings/{id}", async (string id, ILocalApiBackend b, CancellationToken ct) => await b.GetRecordingAsync(id, ct) is { } recording ? Results.Ok(new { ok = true, recording }) : Failure(404, "RECORDING_NOT_FOUND", "Recording not found."));

        async Task<IResult> Transcribe(HttpContext context, ILocalApiBackend backend, CancellationToken ct)
        {
            if (context.Request.ContentLength > options.MaxRequestBytes) return Failure(413, LocalApiErrorCodes.PayloadTooLarge, "Request exceeds the configured limit.");
            if (!context.Request.HasFormContentType) return Failure(400, LocalApiErrorCodes.InvalidRequest, "multipart/form-data with one 'audio' file is required.");
            try
            {
                var form = await context.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("audio");
                if (file is null || file.Length == 0) return Failure(400, LocalApiErrorCodes.InvalidRequest, "A non-empty 'audio' file is required.");
                if (file.Length > options.MaxUploadBytes) return Failure(413, LocalApiErrorCodes.PayloadTooLarge, "Audio exceeds the configured upload limit.");
                var name = file.FileName;
                if (name != Path.GetFileName(name)
                    || name.Contains("..", StringComparison.Ordinal)
                    || name.IndexOfAny(['/', '\\']) >= 0)
                    return Failure(400, LocalApiErrorCodes.FileNotAllowed, "Audio filename must not contain a path.");
                await using var input = file.OpenReadStream();
                using var output = new MemoryStream((int)file.Length);
                await input.CopyToAsync(output, ct);
                var result = await backend.TranscribeAsync(new(name, file.ContentType, output.ToArray(), form["mode_id"], form["engine"], form["model"], form["language"]), ct);
                return Results.Ok(new { ok = true, result.Text, result.Engine, result.Model, result.Language, timings = new { load_ms = result.LoadMs, decode_ms = result.DecodeMs }, latency_ms = result.LatencyMs });
            }
            catch (InvalidDataException) { return Failure(413, LocalApiErrorCodes.PayloadTooLarge, "Request exceeds the configured limit."); }
        }

        async Task<IResult> ListRecordings(HttpContext context, ILocalApiBackend backend, CancellationToken ct)
        {
            var limit = int.TryParse(context.Request.Query["limit"], CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 500) : 50;
            _ = DateTime.TryParse(context.Request.Query["since"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since);
            _ = DateTime.TryParse(context.Request.Query["until"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until);
            var rows = await backend.GetRecordingsAsync(new(context.Request.Query["q"], since == default ? null : since, until == default ? null : until, limit), ct);
            return Results.Ok(new { ok = true, total = rows.Count, returned = rows.Count, recordings = rows });
        }
    }

    private static IResult Failure(int status, string code, string message) => Results.Json(new LocalApiFailure(new(code, message)), statusCode: status);
}

public static class LocalApiBindFallback
{
    public static async Task<int> BindAsync(int preferredPort, Func<int, CancellationToken, Task> start, CancellationToken cancellationToken = default)
    {
        try { await start(preferredPort, cancellationToken); return preferredPort; }
        catch (Exception ex) when (preferredPort != 0 && IsBindFailure(ex)) { await start(0, cancellationToken); return 0; }
    }

    private static bool IsBindFailure(Exception ex) => ex is SocketException || ex.InnerException is SocketException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
}
