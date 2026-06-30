using System.Text.Json;
using HyperWhisper.Models;
using Microsoft.AspNetCore.Http;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Shapes successful and failure JSON responses for the Local API. Mirrors
/// the macOS LocalAPIResponder so wire shapes stay 1:1 across platforms.
/// </summary>
internal static class LocalApiResponder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>200 JSON with the given payload.</summary>
    public static IResult Ok<T>(T payload)
    {
        return Results.Json(payload, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: 200);
    }

    /// <summary>
    /// `{ok:false, error:{...}}` business-failure envelope returned with HTTP
    /// 200 by design — MCP wrappers can't surface error text from an empty 500.
    /// </summary>
    public static IResult Failure(string code, string message, string? hint = null)
    {
        var envelope = new ApiFailureEnvelope
        {
            Error = new ApiError { Code = code, Message = message, Hint = hint }
        };
        return Results.Json(envelope, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: 200);
    }

    /// <summary>Genuine protocol failure (malformed JSON, bad path, etc.).</summary>
    public static IResult BadRequest(string message, string? hint = null)
    {
        var envelope = new ApiFailureEnvelope
        {
            Error = new ApiError { Code = LocalApiErrorCode.InvalidRequest, Message = message, Hint = hint }
        };
        return Results.Json(envelope, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: 400);
    }

    /// <summary>
    /// Translate a <see cref="TranscriptionException"/> into the wire-level
    /// (code, message, hint) tuple the Failure responder expects. Shared by
    /// `/transcribe` (Phase 2) and `/post-process` (Phase 3).
    /// </summary>
    public static (string code, string message, string? hint) MapTranscriptionException(TranscriptionException ex)
    {
        var message = ex.GetUserMessage();
        return ex.Code switch
        {
            TranscriptionErrorCode.ModelNotLoaded =>
                (LocalApiErrorCode.ModelNotInstalled, message,
                 "Open HyperWhisper and download the model you want to use before calling /transcribe."),

            TranscriptionErrorCode.OnnxModelFileMissing =>
                (LocalApiErrorCode.ModelNotInstalled, message,
                 "Re-download the ONNX model from the Model Library."),

            TranscriptionErrorCode.ApiKeyMissing =>
                (LocalApiErrorCode.MissingApiKey, message,
                 "Add the API key in the Model Library API keys manager."),

            TranscriptionErrorCode.Unauthorized =>
                (LocalApiErrorCode.MissingApiKey, message,
                 "Check that the API key in the Model Library API keys manager is valid."),

            TranscriptionErrorCode.AudioFileNotFound =>
                (LocalApiErrorCode.FileNotFound, message, null),

            TranscriptionErrorCode.UnsupportedFormat =>
                (LocalApiErrorCode.AudioDecodeFailed, message,
                 "Supported input formats: wav, mp3, m4a."),

            TranscriptionErrorCode.FileTooLarge =>
                (LocalApiErrorCode.InvalidRequest, message, null),

            TranscriptionErrorCode.InvalidRequest =>
                (LocalApiErrorCode.InvalidRequest, message, null),

            TranscriptionErrorCode.RateLimited =>
                (LocalApiErrorCode.RateLimited, message,
                 ex.RetryAfterSeconds is int s ? $"Retry after {s} seconds." : null),

            TranscriptionErrorCode.QuotaExceeded =>
                (LocalApiErrorCode.RateLimited, message,
                 "Add credits in the provider's billing page."),

            TranscriptionErrorCode.NetworkError =>
                (LocalApiErrorCode.EngineUnavailable, message, null),

            TranscriptionErrorCode.ProviderUnavailable =>
                (LocalApiErrorCode.EngineUnavailable, message, null),

            TranscriptionErrorCode.DaemonStartFailed =>
                (LocalApiErrorCode.EngineUnavailable, message, null),

            TranscriptionErrorCode.DaemonCrashed =>
                (LocalApiErrorCode.EngineUnavailable, message, null),

            TranscriptionErrorCode.DaemonTimeout =>
                (LocalApiErrorCode.EngineUnavailable, message, null),

            TranscriptionErrorCode.Cancelled =>
                (LocalApiErrorCode.Timeout, message, null),

            _ =>
                (LocalApiErrorCode.TranscriptionFailed, message, null)
        };
    }

    /// <summary>Missing or invalid bearer token.</summary>
    public static IResult Unauthorized()
    {
        var envelope = new ApiFailureEnvelope
        {
            Error = new ApiError
            {
                Code = LocalApiErrorCode.InvalidRequest,
                Message = "Missing or invalid bearer token",
                Hint = @"Send Authorization: Bearer <token>; the token lives in %LOCALAPPDATA%\HyperWhisper\local-api.json."
            }
        };
        return Results.Json(envelope, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: 401);
    }
}
