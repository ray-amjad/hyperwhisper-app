using System.Text.Json;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// What is left of this head's Local API responder since the shared server
/// writes every envelope (issue #356 item 6): the JSON options a Windows mode
/// and the discovery file are written with, and the transcription-error table.
/// </summary>
internal static class LocalApiResponder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
        // Cloud's out-of-credits 402 rides on QuotaExceeded (the app has no
        // code of its own for it), so the flag, not the code, picks the row
        // whose hint points at Settings rather than a provider billing page.
        var reason = ex.IsInsufficientCredits
            ? HwLocalApiTranscriptionFailureReason.InsufficientCredits
            : ReasonFor(ex.Code);
        var failure = HyperwhisperCoreMethods.LocalApiMapTranscriptionError(
            reason,
            new HwLocalApiTranscriptionFailureParams(
                @provider: ex.ProviderName,
                // `ex.Message`, which is what the crate's doc asks this head for
                // — NOT `GetUserMessage()` (issue #356, review round 1).
                // `GetUserMessage()` is a finished sentence written for a WPF
                // toast, and several rows interpolate `detail` MID-sentence, so
                // it doubled: `NetworkError` rendered "Network error: Network
                // error. Check your internet connection and try again.", and
                // `ProviderUnavailable` with provider "AssemblyAI" rendered
                // "AssemblyAI is unavailable: AssemblyAI is temporarily
                // unavailable. Try again later or use local transcription." —
                // the name stuttering and the advice duplicating the row's own
                // `hint`, which item 4 deliberately moved INTO `hint`.
                //
                // Nothing is lost on the generic row. Every
                // `TranscriptionErrorCode` that has an arm in
                // `GetUserMessage()` has a reason of its own in `ReasonFor`, so
                // the only codes that reach `TranscriptionFailed` are the ones
                // whose `GetUserMessage()` is the `_ => Message` arm.
                @detail: ex.Message,
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
    /// The crate ignores this slot on the other twenty-three reasons — a head
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
}
