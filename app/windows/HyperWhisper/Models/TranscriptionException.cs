using HyperWhisper.Localization;
using HyperWhisper.Services.Transcription;

// TRANSCRIPTION EXCEPTION
// Custom exception type for transcription errors with categorized error codes.
// Enables specific error handling and user-friendly error messages.
//
// ERROR HANDLING STRATEGY:
// - API key issues: Prompt user to check the Model Library API keys manager
// - Quota/rate limits: Show provider-specific guidance
// - Network errors: Suggest retry
// - File issues: Show specific file error

namespace HyperWhisper.Models;

/// <summary>
/// Categorized error codes for transcription failures.
/// Used to determine appropriate user feedback and recovery actions.
/// </summary>
public enum TranscriptionErrorCode
{
    /// <summary>Unknown or unclassified error.</summary>
    Unknown = 0,

    /// <summary>Audio file not found at specified path.</summary>
    AudioFileNotFound = 1,

    /// <summary>API key not configured for the cloud provider.</summary>
    ApiKeyMissing = 2,

    /// <summary>API key is invalid or revoked (HTTP 401/403).</summary>
    Unauthorized = 3,

    /// <summary>Account quota exceeded - need to add credits (HTTP 429 with quota message).</summary>
    QuotaExceeded = 4,

    /// <summary>Rate limited - temporary, retry after delay (HTTP 429).</summary>
    RateLimited = 5,

    /// <summary>Invalid request parameters (HTTP 400/422).</summary>
    InvalidRequest = 6,

    /// <summary>Network connectivity error.</summary>
    NetworkError = 7,

    /// <summary>Cloud provider is unavailable (HTTP 5xx).</summary>
    ProviderUnavailable = 8,

    /// <summary>Audio file exceeds provider's size limit (e.g., 25MB for OpenAI).</summary>
    FileTooLarge = 9,

    /// <summary>Audio format not supported by provider.</summary>
    UnsupportedFormat = 10,

    /// <summary>Local model not loaded.</summary>
    ModelNotLoaded = 11,

    /// <summary>Operation was cancelled.</summary>
    Cancelled = 12,

    /// <summary>ONNX model file(s) missing from the expected directory.</summary>
    OnnxModelFileMissing = 13,

    /// <summary>Parakeet daemon process failed to start.</summary>
    DaemonStartFailed = 14,

    /// <summary>Parakeet daemon process crashed during transcription.</summary>
    DaemonCrashed = 15,

    /// <summary>Parakeet daemon did not respond within the timeout period.</summary>
    DaemonTimeout = 16,

    /// <summary>Provider explicitly reported that no speech was detected in the audio.</summary>
    NoSpeechDetected = 17
}

/// <summary>
/// Exception thrown when transcription fails.
/// Contains error code for categorized handling and provider name for context.
/// </summary>
public class TranscriptionException : Exception
{
    /// <summary>Categorized error code for determining recovery action.</summary>
    public TranscriptionErrorCode Code { get; }

    /// <summary>Name of the provider that failed (e.g., "OpenAI", "Whisper Base").</summary>
    public string? ProviderName { get; }

    /// <summary>HTTP status code if applicable (for cloud errors).</summary>
    public int? HttpStatusCode { get; }

    /// <summary>Retry-After header value in seconds (for rate limiting).</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Provider-specific diagnostics captured during the failed attempt.</summary>
    public TranscriptionProviderDiagnostics? ProviderDiagnostics { get; }

    public TranscriptionException(
        TranscriptionErrorCode code,
        string message,
        string? providerName = null,
        Exception? innerException = null,
        TranscriptionProviderDiagnostics? providerDiagnostics = null)
        : base(message, innerException)
    {
        Code = code;
        ProviderName = providerName;
        ProviderDiagnostics = providerDiagnostics;
    }

    public TranscriptionException(
        TranscriptionErrorCode code,
        string message,
        string? providerName,
        int? httpStatusCode,
        int? retryAfterSeconds = null,
        Exception? innerException = null,
        TranscriptionProviderDiagnostics? providerDiagnostics = null)
        : base(message, innerException)
    {
        Code = code;
        ProviderName = providerName;
        HttpStatusCode = httpStatusCode;
        RetryAfterSeconds = retryAfterSeconds;
        ProviderDiagnostics = providerDiagnostics;
    }

    /// <summary>
    /// Gets a user-friendly error message with recovery guidance.
    /// </summary>
    public string GetUserMessage() => Code switch
    {
        TranscriptionErrorCode.ApiKeyMissing =>
            $"No API key configured for {ProviderName ?? "cloud provider"}. Add it in the Model Library API keys manager.",

        TranscriptionErrorCode.Unauthorized =>
            $"Invalid {ProviderName ?? "API"} key. Please check it in the Model Library API keys manager.",

        TranscriptionErrorCode.QuotaExceeded =>
            $"{ProviderName ?? "Provider"} quota exceeded. Add credits at the provider's billing page.",

        TranscriptionErrorCode.RateLimited =>
            RetryAfterSeconds.HasValue
                ? $"Rate limited. Please wait {RetryAfterSeconds} seconds and try again."
                : "Rate limited. Please wait a moment and try again.",

        TranscriptionErrorCode.NetworkError =>
            "Network error. Check your internet connection and try again.",

        TranscriptionErrorCode.ProviderUnavailable =>
            $"{ProviderName ?? "Cloud provider"} is temporarily unavailable. Try again later or use local transcription.",

        TranscriptionErrorCode.FileTooLarge =>
            "Audio file is too large. Try a shorter recording or use local transcription.",

        TranscriptionErrorCode.UnsupportedFormat =>
            "Audio format not supported. Try recording in a different format.",

        TranscriptionErrorCode.AudioFileNotFound =>
            "Audio file not found. Please try recording again.",

        TranscriptionErrorCode.ModelNotLoaded =>
            "Model not loaded. Please select and load a model first.",

        TranscriptionErrorCode.Cancelled =>
            "Transcription was cancelled.",

        TranscriptionErrorCode.OnnxModelFileMissing =>
            "ONNX model files are missing. Please re-download the model.",

        TranscriptionErrorCode.DaemonStartFailed =>
            "Failed to start the Parakeet transcription engine. Please restart the app.",

        TranscriptionErrorCode.DaemonCrashed =>
            "The Parakeet transcription engine crashed. Please try again.",

        TranscriptionErrorCode.DaemonTimeout =>
            "The Parakeet transcription engine timed out. Please try a shorter recording.",

        TranscriptionErrorCode.NoSpeechDetected =>
            Loc.S("errors.noSpeechDetected"),

        _ => Message
    };
}
