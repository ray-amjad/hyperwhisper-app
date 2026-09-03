using System.Text.Json;
using HyperWhisper.Models;
using Microsoft.AspNetCore.Http;
using uniffi.hyperwhisper_core;

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
    /// <summary>
    /// A failure <c>hw-localapi</c> already decided — status, code, message and
    /// hint all come from the crate (issue #356).
    /// </summary>
    /// <remarks>
    /// The crate also encodes the whole envelope as JSON, but this head writes
    /// its own <see cref="ApiFailureEnvelope"/> so the response goes out under
    /// the same <see cref="JsonOptions"/> as every other response on this
    /// server. The parts that must not drift — which of the closed fourteen,
    /// which status, and the exact wording — are the crate's.
    /// </remarks>
    public static IResult Shared(HwLocalApiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var code = HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(failure.code);
        return failure.httpStatus == 400
            ? BadRequestWithCode(code, failure.message, failure.hint)
            : Failure(code, failure.message, failure.hint);
    }

    private static IResult BadRequestWithCode(string code, string message, string? hint)
    {
        var envelope = new ApiFailureEnvelope
        {
            Error = new ApiError { Code = code, Message = message, Hint = hint }
        };
        return Results.Json(envelope, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: 400);
    }

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
    /// <remarks>
    /// THE TABLE IS THE SHARED CORE'S (issue #356 item 4). This method now does
    /// one job: turn a <see cref="TranscriptionErrorCode"/> into one of the
    /// crate's reasons, fill the interpolation slots off the exception, and
    /// hand this head's own API-key hint to the three rows whose hint has to
    /// name a product surface. The code, the message and every other hint come
    /// back from <c>hw-localapi</c>, so they cannot drift from macOS's and the
    /// portable head's again.
    ///
    /// Every row is still a business failure at HTTP 200, which is what
    /// <see cref="Failure"/> writes and what this method's callers already did.
    ///
    /// TWO CODES CHANGE ON THIS HEAD, deliberately. There was no arm for
    /// <see cref="TranscriptionErrorCode.CloudAccountRequired"/> or
    /// <see cref="TranscriptionErrorCode.NoSpeechDetected"/>, so both fell to
    /// the `_` arm and answered <c>TRANSCRIPTION_FAILED</c>; macOS answers
    /// <c>MISSING_API_KEY</c> for the first. That is exactly the divergence
    /// item 4 exists to close, so this head follows macOS.
    ///
    /// The messages change on almost every row, because they used to come from
    /// <see cref="TranscriptionException.GetUserMessage"/> — which is written
    /// for a WPF toast, mixes the fix into the sentence, and is *localized* for
    /// <see cref="TranscriptionErrorCode.NoSpeechDetected"/>. The Local API
    /// wire is not localized and it carries the fix in a separate `hint` field.
    /// </remarks>
    public static (string code, string message, string? hint) MapTranscriptionException(TranscriptionException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var reason = ReasonFor(ex.Code);
        var failure = HyperwhisperCoreMethods.LocalApiMapTranscriptionError(
            reason,
            new HwLocalApiTranscriptionFailureParams(
                @provider: ex.ProviderName,
                // `GetUserMessage()` for a code with no arm of its own is just
                // `Message`, and the generic row is the only one that renders
                // the detail as the whole message — so the fallback text this
                // head has always sent survives.
                @detail: ex.GetUserMessage(),
                @model: null,
                @limitBytes: null,
                // Filled because this head has it. No reason this head can
                // reach reads it today — the crate renders it only for
                // `ProviderServerError`, which is macOS's `.serverError` and
                // has no `TranscriptionErrorCode` equivalent — so this is the
                // slot being kept honest, not a rendered value.
                @httpStatus: ex.HttpStatusCode is int status and >= 0 and <= ushort.MaxValue ? (ushort)status : null,
                @retryAfterSeconds: ex.RetryAfterSeconds is int seconds and >= 0 ? (uint)seconds : null,
                @hint: PlatformHint(reason)));
        return (HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(failure.code), failure.message, failure.hint);
    }

    /// <summary>
    /// This head's <see cref="TranscriptionErrorCode"/> as one of the crate's
    /// reasons.
    /// </summary>
    /// <remarks>
    /// <see cref="TranscriptionErrorCode.Unknown"/> and anything added later
    /// land on the generic row, which is where the old `_` arm put them.
    /// </remarks>
    private static HwLocalApiTranscriptionFailureReason ReasonFor(TranscriptionErrorCode code) => code switch
    {
        TranscriptionErrorCode.ModelNotLoaded => HwLocalApiTranscriptionFailureReason.ModelNotInstalled,
        TranscriptionErrorCode.OnnxModelFileMissing => HwLocalApiTranscriptionFailureReason.ModelFilesMissing,
        TranscriptionErrorCode.ApiKeyMissing => HwLocalApiTranscriptionFailureReason.ApiKeyMissing,
        TranscriptionErrorCode.Unauthorized => HwLocalApiTranscriptionFailureReason.ApiKeyInvalid,
        TranscriptionErrorCode.CloudAccountRequired => HwLocalApiTranscriptionFailureReason.CloudAccountRequired,
        TranscriptionErrorCode.AudioFileNotFound => HwLocalApiTranscriptionFailureReason.AudioFileNotFound,
        TranscriptionErrorCode.UnsupportedFormat => HwLocalApiTranscriptionFailureReason.AudioDecodeFailed,
        TranscriptionErrorCode.FileTooLarge => HwLocalApiTranscriptionFailureReason.AudioFileTooLarge,
        TranscriptionErrorCode.InvalidRequest => HwLocalApiTranscriptionFailureReason.InvalidRequest,
        TranscriptionErrorCode.RateLimited => HwLocalApiTranscriptionFailureReason.RateLimited,
        TranscriptionErrorCode.QuotaExceeded => HwLocalApiTranscriptionFailureReason.QuotaExceeded,
        TranscriptionErrorCode.NetworkError => HwLocalApiTranscriptionFailureReason.NetworkUnavailable,
        TranscriptionErrorCode.ProviderUnavailable => HwLocalApiTranscriptionFailureReason.EngineUnavailable,
        TranscriptionErrorCode.DaemonStartFailed => HwLocalApiTranscriptionFailureReason.EngineStartFailed,
        TranscriptionErrorCode.DaemonCrashed => HwLocalApiTranscriptionFailureReason.EngineCrashed,
        TranscriptionErrorCode.DaemonTimeout => HwLocalApiTranscriptionFailureReason.EngineTimeout,
        TranscriptionErrorCode.NoSpeechDetected => HwLocalApiTranscriptionFailureReason.NoSpeechDetected,
        TranscriptionErrorCode.Cancelled => HwLocalApiTranscriptionFailureReason.Cancelled,
        _ => HwLocalApiTranscriptionFailureReason.TranscriptionFailed,
    };

    /// <summary>
    /// The hint for the three rows the crate deliberately leaves to the head,
    /// because they have to name a product surface: this app puts BYOK keys in
    /// the Model Library's API keys manager and the HyperWhisper Cloud account
    /// key in Settings → HyperWhisper Cloud, where macOS says
    /// `Settings → API Keys`.
    /// </summary>
    /// <remarks>
    /// The crate ignores this slot on the other twenty-two reasons — a head
    /// that could override any hint would put the wording back where item 4
    /// found it — so returning <c>null</c> for them is the honest answer and
    /// not a lost hint.
    /// </remarks>
    private static string? PlatformHint(HwLocalApiTranscriptionFailureReason reason) => reason switch
    {
        HwLocalApiTranscriptionFailureReason.ApiKeyMissing =>
            "Add the API key in the Model Library API keys manager.",
        HwLocalApiTranscriptionFailureReason.ApiKeyInvalid =>
            "Check that the API key in the Model Library API keys manager is valid.",
        // NOT the API keys manager: the account key is entered on the Cloud
        // settings page, which is the same split `MainViewModel` makes when it
        // routes this error's toast (`settingsSection: "Cloud"`).
        HwLocalApiTranscriptionFailureReason.CloudAccountRequired =>
            "Add your account key in Settings → HyperWhisper Cloud.",
        _ => null,
    };

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
