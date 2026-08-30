using Microsoft.AspNetCore.Http;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.LocalApi;

/// <summary>
/// The two failures the shared core encodes for us, written verbatim
/// (issue #289).
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

    private static async Task WriteAsync(HttpContext context, HwLocalApiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = failure.httpStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(failure.json);
    }
}
