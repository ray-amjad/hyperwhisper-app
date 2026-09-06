using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// REQUEST-SIZE LIMITS FOR THE LOCAL API
///
/// The Windows head is a SEPARATE implementation from the portable .NET one
/// (<c>PortableLocalApi.cs</c>): it references neither <c>PortableLocalApi</c>
/// nor <c>LocalApiHost</c>, so none of their limits ever applied here. Until
/// this file existed the situation was:
///
/// <list type="bullet">
///   <item><description>
///     No configured body cap at all. <see cref="LocalApiServer"/>'s
///     <c>ConfigureKestrel</c> set only the two timeouts and the listener, so
///     Kestrel's framework default of 30,000,000 bytes stood — an ACCIDENTAL
///     cap, at a different number from the 52,428,800 the shared core owns and
///     tied to nothing.
///   </description></item>
///   <item><description>
///     The wrong failure shape. Each of the four JSON body reads sat in a bare
///     <c>catch</c>, which swallowed Kestrel's <see cref="Microsoft.AspNetCore.Http.BadHttpRequestException"/>
///     and answered HTTP 400 <c>"Invalid JSON body"</c>. A caller could not tell
///     "too big" from "malformed".
///   </description></item>
///   <item><description>
///     No cap on <c>audio_base64</c> — neither on the encoded string nor on the
///     decoded bytes — and none on the <c>file</c> path.
///   </description></item>
/// </list>
///
/// The numbers and the two envelopes now come from <c>hw-localapi</c>
/// (<c>crates/hw-localapi/src/limits.rs</c>), the same place macOS and the
/// portable .NET head read them from since #405. The COMPARISON stays here on
/// purpose: the crate deliberately exports no <c>exceeds_*</c> predicate,
/// because each head compares against its own configured cap. It is <c>&gt;</c>
/// and not <c>&gt;=</c> everywhere — a body of exactly the cap is accepted —
/// and the smoke suite pins that boundary, as the crate's module docs ask each
/// head to do.
///
/// EVERY SIZE REFUSAL IS HTTP 200 CARRYING <c>INVALID_REQUEST</c>. Not 413, and
/// never a <c>PAYLOAD_TOO_LARGE</c> code: the wire codes are a closed fourteen
/// shared with macOS, and a client using the macOS <c>Codable</c> decoder fails
/// to decode the WHOLE envelope on a fifteenth. The 413 that appears below is
/// Kestrel's own status on the exception it raises, which this file reads and
/// discards — it is never a status this head sends.
/// </summary>
internal static class LocalApiLimits
{
    /// <summary>
    /// The largest request body this head accepts: 52,428,800 bytes (50 MiB).
    /// </summary>
    public static long MaxRequestBytes { get; } =
        checked((long)HyperwhisperCoreMethods.LocalApiMaxRequestBytes());

    /// <summary>
    /// The largest single piece of audio this head buffers: 50,331,648 bytes
    /// (48 MiB). Applies to the decoded <c>audio_base64</c> bytes and to a
    /// <c>file</c> path.
    /// </summary>
    public static long MaxUploadBytes { get; } =
        checked((long)HyperwhisperCoreMethods.LocalApiMaxUploadBytes());

    /// <summary>
    /// The longest base64 string that can decode to <see cref="MaxUploadBytes"/>
    /// or fewer bytes: 67,108,864 characters. Checked BEFORE the decode, so a
    /// caller cannot make this head allocate the decoded buffer only to be told
    /// the decoded buffer is too big.
    /// </summary>
    public static long MaxBase64LengthForUpload { get; } =
        checked((long)HyperwhisperCoreMethods.LocalApiMaxBase64LengthForUpload());

    /// <summary>The shared "request body too large" envelope, unpacked.</summary>
    public static (string Code, string Message, string? Hint) RequestTooLarge { get; } =
        Unpack(HyperwhisperCoreMethods.LocalApiRequestTooLargeFailure());

    /// <summary>The shared "audio too large" envelope, unpacked.</summary>
    public static (string Code, string Message, string? Hint) UploadTooLarge { get; } =
        Unpack(HyperwhisperCoreMethods.LocalApiUploadTooLargeFailure());

    private static (string Code, string Message, string? Hint) Unpack(HwLocalApiFailure failure) =>
        (HyperwhisperCoreMethods.LocalApiErrorCodeWireValue(failure.code), failure.message, failure.hint);

    /// <summary>
    /// Bound the request body at the shared cap. Called from
    /// <see cref="LocalApiServer"/>'s <c>ConfigureKestrel</c>; without it
    /// Kestrel keeps its own unrelated 30,000,000-byte default.
    /// </summary>
    public static void ApplyRequestBodyLimit(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Limits.MaxRequestBodySize = MaxRequestBytes;
    }

    /// <summary>Exactly the cap is fine; one byte more is not.</summary>
    public static bool ExceedsRequestLimit(long byteCount) => byteCount > MaxRequestBytes;

    /// <inheritdoc cref="ExceedsRequestLimit"/>
    public static bool ExceedsUploadLimit(long byteCount) => byteCount > MaxUploadBytes;

    /// <inheritdoc cref="ExceedsRequestLimit"/>
    public static bool ExceedsBase64UploadLimit(long encodedLength) =>
        encodedLength > MaxBase64LengthForUpload;

    /// <summary>HTTP 200 + <c>INVALID_REQUEST</c> + the shared message.</summary>
    public static IResult RequestTooLargeResult() =>
        LocalApiResponder.Failure(RequestTooLarge.Code, RequestTooLarge.Message, RequestTooLarge.Hint);

    /// <inheritdoc cref="RequestTooLargeResult"/>
    public static IResult UploadTooLargeResult() =>
        LocalApiResponder.Failure(UploadTooLarge.Code, UploadTooLarge.Message, UploadTooLarge.Hint);

    /// <summary>
    /// Read a JSON request body, distinguishing an over-limit body from a
    /// malformed one. Every <c>POST</c>/<c>PATCH</c> route on this head goes
    /// through here.
    ///
    /// HOW KESTREL SURFACES AN OVER-LIMIT BODY. The limit is not enforced when
    /// the headers are parsed. <c>MessageBody.AddAndCheckObservedBytes</c>
    /// compares a running counter as the body is CONSUMED, so the rejection
    /// happens inside this method's <c>ReadFromJsonAsync</c> call, as a
    /// <see cref="Microsoft.AspNetCore.Http.BadHttpRequestException"/> whose <c>StatusCode</c> is 413.
    /// <c>System.Text.Json</c> lets a stream exception through unwrapped, so it
    /// arrives here as itself — which is precisely why the old bare
    /// <c>catch</c> turned it into a 400.
    ///
    /// Two belts, because "verify, do not guess" cuts both ways:
    ///
    /// <list type="number">
    ///   <item><description>
    ///     A <c>Content-Length</c> pre-check, which needs no exception at all
    ///     and mirrors <c>PortableLocalApi.cs:185</c>. It also covers the case
    ///     where a future ASP.NET version rejects the declared length before
    ///     the handler runs.
    ///   </description></item>
    ///   <item><description>
    ///     The exception, for a chunked body that declares no length. The 413
    ///     is matched anywhere in the inner-exception chain, so a wrapper added
    ///     by a future <c>ReadFromJsonAsync</c> would not silently downgrade
    ///     the answer back to a 400.
    ///   </description></item>
    /// </list>
    ///
    /// A <see cref="Microsoft.AspNetCore.Http.BadHttpRequestException"/> that is NOT the size case (a
    /// truncated body, bad chunk framing) keeps today's behaviour: HTTP 400
    /// <c>"Invalid JSON body"</c>.
    /// </summary>
    /// <param name="ctx">The in-flight request.</param>
    /// <param name="invalidJsonHint">The route's own hint for a genuine JSON error.</param>
    public static async Task<(T? Value, IResult? Failure)> ReadJsonBodyAsync<T>(
        HttpContext ctx,
        string? invalidJsonHint = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Request.ContentLength is { } declaredLength && ExceedsRequestLimit(declaredLength))
        {
            return (null, RequestTooLargeResult());
        }

        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(LocalApiResponder.JsonOptions);
            return (value, null);
        }
        catch (Exception ex) when (IsBodyTooLarge(ex))
        {
            return (null, RequestTooLargeResult());
        }
        catch
        {
            return (null, LocalApiResponder.BadRequest("Invalid JSON body", invalidJsonHint));
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/>, or anything it wraps, is Kestrel's
    /// "request body too large" rejection. Kestrel tags that one rejection with
    /// <c>StatusCode</c> 413; every other <see cref="Microsoft.AspNetCore.Http.BadHttpRequestException"/>
    /// it raises carries 400 or 431.
    /// </summary>
    public static bool IsBodyTooLarge(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // Fully qualified because TWO types carry this name and both are in
            // scope here: the obsolete
            // Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException,
            // and Microsoft.AspNetCore.Http.BadHttpRequestException. The Kestrel
            // one DERIVES from the Http one and is what Kestrel actually throws,
            // so matching the base catches both — matching the Kestrel one would
            // miss a rejection raised anywhere else in the pipeline.
            //
            // 413 is READ here, never sent. See the class summary.
            if (current is Microsoft.AspNetCore.Http.BadHttpRequestException { StatusCode: 413 }) return true;
            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Any(IsBodyTooLarge))
            {
                return true;
            }
        }
        return false;
    }
}
