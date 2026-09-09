using HyperWhisper.PortableApplication.Transcription;
using Microsoft.AspNetCore.Http;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

/// <summary>
/// The failures the shared core encodes for us, written verbatim
/// (issues #289 and #356).
///
/// The status AND the JSON both come from Rust. That is the point of the
/// envelope half of the issue: this head returned 404/413/503/408 for business
/// outcomes the docs mandate 200 for, and it did so because every platform
/// decided the status itself. For the guard rejection and the missing-token
/// rejection the three heads now put the same bytes on the wire.
///
/// The binding's records are `internal`, so this adapter keeps them off the
/// public surface of the server.
/// </summary>
internal static class LocalApiSharedFailure
{
    /// <summary>HTTP 403, `INVALID_REQUEST` — Host/Origin not permitted.</summary>
    internal static Task WriteForbiddenOriginAsync(HttpContext context) =>
        WriteAsync(context, HyperwhisperCoreMethods.LocalApiForbiddenOriginFailure());

    /// <summary>HTTP 401, `INVALID_REQUEST` — missing or invalid bearer token.</summary>
    internal static Task WriteUnauthorizedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"hyperwhisper\"";
        return WriteAsync(context, HyperwhisperCoreMethods.LocalApiUnauthorizedFailure(null));
    }

    /// <summary>
    /// The `(code, message, hint)` for a transcription failure, from the one
    /// shared table (issue #356 item 4).
    /// </summary>
    /// <remarks>
    /// WHAT THIS REPLACES. Every failing transcription on this head used to
    /// leave the backend as <c>new InvalidOperationException(message)</c>, and
    /// the middleware's <c>catch (InvalidOperationException)</c> bound no
    /// variable — so all four <see cref="PortableTranscriptionErrorCode"/>
    /// values AND every message <c>TranscriptionWorkflow</c> produced collapsed
    /// into one code and one fixed string. A caller whose audio failed to
    /// transcribe was told the app has no capability; a cancelled caller was
    /// told the same.
    ///
    /// The four codes map onto four of the crate's reasons, which is the whole
    /// of this head's contribution to the union: <c>InvalidRequest</c> →
    /// <c>INVALID_REQUEST</c>, <c>BackendUnavailable</c> →
    /// <c>ENGINE_UNAVAILABLE</c>, <c>TranscriptionFailed</c> →
    /// <c>TRANSCRIPTION_FAILED</c>, <c>Cancelled</c> → <c>TIMEOUT</c>. The last
    /// follows the middleware's own note beside its
    /// <see cref="OperationCanceledException"/> arm: <c>CANCELLED</c> was never
    /// in the closed fourteen and <c>TIMEOUT</c> is the documented code for
    /// running out of time.
    ///
    /// The failure's own message travels as the crate's <c>detail</c> slot, so
    /// the workflow's text reaches the wire instead of being discarded. The
    /// hint is the table's on every one of these four rows — none of them is
    /// one of the three that name a product surface — so this head passes no
    /// hint in.
    /// </remarks>
    internal static LocalApiFailureException TranscriptionFailure(PortableTranscriptionFailure? failure) =>
        LocalApiFailureException.From(HyperwhisperCoreMethods.LocalApiMapTranscriptionError(
            ReasonFor(failure?.Code),
            new HwLocalApiTranscriptionFailureParams(
                @provider: null,
                @detail: failure?.Message,
                @model: null,
                @limitBytes: null,
                @httpStatus: null,
                @retryAfterSeconds: null,
                @hint: null)));

    /// <summary>
    /// A <see cref="PortableTranscriptionErrorCode"/> as one of the crate's
    /// reasons. A result that is not a success but carries no failure — which
    /// no <c>TranscriptionWorkflow</c> path produces today — is the generic
    /// row, not a crash.
    /// </summary>
    private static HwLocalApiTranscriptionFailureReason ReasonFor(PortableTranscriptionErrorCode? code) => code switch
    {
        PortableTranscriptionErrorCode.InvalidRequest => HwLocalApiTranscriptionFailureReason.InvalidRequest,
        PortableTranscriptionErrorCode.BackendUnavailable => HwLocalApiTranscriptionFailureReason.EngineUnavailable,
        PortableTranscriptionErrorCode.Cancelled => HwLocalApiTranscriptionFailureReason.Cancelled,
        _ => HwLocalApiTranscriptionFailureReason.TranscriptionFailed,
    };

    private static async Task WriteAsync(HttpContext context, HwLocalApiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = failure.httpStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(failure.json);
    }
}
