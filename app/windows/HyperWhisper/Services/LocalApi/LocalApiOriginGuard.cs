using Microsoft.AspNetCore.Http;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// Host / Origin validation for the Local API loopback server. Defends the
/// whole surface — including the unauthenticated <c>GET /health</c> route —
/// against DNS-rebinding attacks.
///
/// A malicious web page can rebind <c>attacker.com</c> to <c>127.0.0.1</c> and
/// then read responses cross-origin; the one thing it CANNOT forge is the
/// <c>Host</c> header, which the browser still sets to <c>attacker.com</c>. By
/// requiring <c>Host</c> to be exactly <c>127.0.0.1:&lt;port&gt;</c> (or
/// <c>localhost:&lt;port&gt;</c>) and rejecting any cross-site <c>Origin</c> /
/// <c>Sec-Fetch-Site</c>, rebound requests are dropped before they reach a
/// handler.
///
/// WINDOWS HAD NO GUARD AT ALL UNTIL ISSUE #289. macOS shipped one
/// (<c>LocalAPIOriginGuard.swift</c>, issue #730) and the docs conceded it as
/// "macOS only". The decision now lives in
/// <c>shared-core-rs/crates/hw-localapi</c>, ported branch by branch from the
/// Swift original and pinned by a decision-vector table, so this file is only
/// the ASP.NET adapter: pull three header values off the request and hand them
/// across.
/// </summary>
internal static class LocalApiOriginGuard
{
    /// <summary>
    /// The decision for a request, including which check rejected it.
    /// </summary>
    /// <param name="context">The in-flight request.</param>
    /// <param name="port">
    /// The port the server is ACTUALLY bound to, not the configured
    /// preference — a fallback bind lands somewhere else and the
    /// <c>Host</c> header names where the client really connected. Passing 0
    /// (not bound yet) denies.
    /// </param>
    public static HwLocalApiOriginDecision Decide(HttpContext context, int port)
    {
        ArgumentNullException.ThrowIfNull(context);
        // `IHeaderDictionary` is case-insensitive (RFC 7230 §3.2), so one
        // keyed lookup already matches whatever casing the client sent. An
        // absent header reads as an empty `StringValues`, and absent is its own
        // branch in the guard — a missing `Host` denies where a missing
        // `Origin` does not — so map it back to null rather than "".
        return HyperwhisperCoreMethods.LocalApiCheckOrigin(
            new HwLocalApiOriginHeaders(
                HeaderOrNull(context, "Host"),
                HeaderOrNull(context, "Origin"),
                HeaderOrNull(context, "Sec-Fetch-Site")),
            // A port outside `ushort` cannot be a bound TCP port; 0 is the
            // "not bound yet" value the guard already denies.
            port is > 0 and <= ushort.MaxValue ? (ushort)port : (ushort)0);
    }

    /// <summary>
    /// Whether the request is safe to dispatch.
    /// </summary>
    public static bool IsAllowed(HttpContext context, int port) =>
        HyperwhisperCoreMethods.LocalApiOriginDecisionIsAllowed(Decide(context, port));

    private static string? HeaderOrNull(HttpContext context, string name)
    {
        var value = context.Request.Headers[name];
        return value.Count == 0 ? null : value.ToString();
    }
}
