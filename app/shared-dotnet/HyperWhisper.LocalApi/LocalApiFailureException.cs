using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

/// <summary>
/// A failure the shared core already decided, on its way out of a backend call
/// (issue #356).
///
/// THE PROBLEM THIS SOLVES. <see cref="ILocalApiBackend"/> returns values, so
/// the only channel a backend has to the middleware is an exception, and the
/// middleware in <c>PortableLocalApi</c> maps CLR exception TYPES onto wire
/// codes: <see cref="ArgumentException"/> becomes HTTP 400
/// <c>INVALID_REQUEST</c> and <see cref="InvalidOperationException"/> becomes
/// HTTP 200 <c>ENGINE_UNAVAILABLE</c>. That table has exactly two outcomes, so
/// a backend that wanted any of the other twelve codes could not ask for one —
/// which is why <c>MODE_NAME_TAKEN</c> was declared on this head and never
/// emitted, and why a duplicate mode name answered 400 <c>INVALID_REQUEST</c>
/// where macOS and Windows answer 200 <c>MODE_NAME_TAKEN</c>.
///
/// This carries the whole envelope <c>hw-localapi</c> produced — status, code,
/// message, hint, and the encoded JSON — so the bytes this head puts on the
/// wire are the bytes the crate wrote, exactly as
/// <see cref="LocalApiSharedFailure"/> already does for the origin and bearer
/// rejections.
///
/// IT DERIVES FROM <see cref="Exception"/>, NOT FROM
/// <see cref="InvalidOperationException"/>. If it derived from one of the types
/// the middleware already catches, catch ORDER would silently decide the
/// outcome, and a reordering during an unrelated edit would quietly collapse
/// every code back onto <c>ENGINE_UNAVAILABLE</c>.
/// </summary>
public sealed class LocalApiFailureException : Exception
{
    /// <summary>The status to send: 200 for a business failure, 400 for a malformed request.</summary>
    public int HttpStatus { get; }

    /// <summary>The wire code, always one of the closed fourteen.</summary>
    public string Code { get; }

    /// <summary>What to do about it, when the crate had something to say.</summary>
    public string? Hint { get; }

    /// <summary>
    /// The whole envelope as the crate encoded it:
    /// <c>{"ok":false,"error":{"code":…,"message":…[,"hint":…]}}</c>. Written
    /// verbatim so a hint that is absent stays OMITTED rather than serialised
    /// as <c>null</c>.
    /// </summary>
    public string Json { get; }

    public LocalApiFailureException(int httpStatus, string code, string message, string? hint, string json)
        : base(message)
    {
        HttpStatus = httpStatus;
        Code = code;
        Hint = hint;
        Json = json;
    }

    /// <summary>
    /// Wrap a <c>hw-localapi</c> failure. The binding's records are
    /// <c>internal</c>, so this factory is the seam that keeps them off the
    /// public surface of the server — the same trick
    /// <see cref="LocalApiSharedFailure"/> uses.
    /// </summary>
    internal static LocalApiFailureException From(HwLocalApiFailure failure) =>
        new(failure.httpStatus,
            HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(failure.code),
            failure.message,
            failure.hint,
            failure.json);
}
