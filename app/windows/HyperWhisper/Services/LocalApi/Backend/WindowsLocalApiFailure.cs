using HyperWhisper.LocalApi;
using HyperWhisper.Models;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Business and request failures the Windows backend raises, as the
/// <see cref="LocalApiFailureException"/> the shared server writes verbatim
/// (issue #356 item 6).
/// </summary>
/// <remarks>
/// The backend's only channel to the shared middleware is an exception. Every
/// envelope here is encoded by <c>hw-localapi</c>, so the code is always one of
/// the closed fourteen and an absent hint stays omitted.
/// </remarks>
internal static class WindowsLocalApiFailure
{
    /// <summary>HTTP 200 with <paramref name="code"/>, a business outcome.</summary>
    internal static LocalApiFailureException Business(string code, string message, string? hint = null)
    {
        var wire = HyperwhisperCoreMethods.LocalApiErrorCodeFromWireValue(code)
            ?? throw new ArgumentException($"'{code}' is not a Local API error code.", nameof(code));
        return From(HyperwhisperCoreMethods.LocalApiBusinessFailure(wire, message, hint));
    }

    /// <summary>HTTP 400 <c>INVALID_REQUEST</c>, a malformed request.</summary>
    internal static LocalApiFailureException BadRequest(string message, string? hint = null) =>
        From(HyperwhisperCoreMethods.LocalApiBadRequestFailure(message, hint));

    /// <summary>A failure the shared core already decided, status included.</summary>
    internal static LocalApiFailureException From(HwLocalApiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new LocalApiFailureException(
            failure.httpStatus,
            HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(failure.code),
            failure.message,
            failure.hint,
            failure.json);
    }

    /// <summary>A transcription failure, through the shared table.</summary>
    internal static LocalApiFailureException Transcription(TranscriptionException exception)
    {
        var (code, message, hint) = LocalApiResponder.MapTranscriptionException(exception);
        return Business(code, message, hint);
    }
}
