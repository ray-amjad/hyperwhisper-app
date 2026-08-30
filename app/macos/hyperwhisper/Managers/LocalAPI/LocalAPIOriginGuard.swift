//
//  LocalAPIOriginGuard.swift
//  hyperwhisper
//
//  Host / Origin validation for the Local API loopback server. Defends the
//  whole surface — including the unauthenticated `GET /health` route — against
//  DNS-rebinding attacks. A malicious web page can rebind `attacker.com` to
//  `127.0.0.1` and then read responses cross-origin; the one thing it CANNOT
//  forge is the `Host` header, which the browser still sets to `attacker.com`.
//  By requiring `Host` to be exactly `127.0.0.1:<port>` (or `localhost:<port>`)
//  and rejecting any cross-site `Origin` / `Sec-Fetch-Site`, rebound requests
//  are dropped before they reach a handler. See issue #730.
//
//  THE DECISION LIVES IN RUST (issue #289). The 108 lines of string logic this
//  file used to hold are now `shared-core-rs/crates/hw-localapi/src/origin.rs`,
//  ported branch by branch and pinned by a decision-vector table derived from
//  the Swift original. macOS was the ONLY platform that shipped this guard;
//  Windows and Linux now run the same code, on every route, rather than a
//  second and third transliteration of it.
//
//  What remains here is the FlyingFox adapter: pull three header values out of
//  an `HTTPRequest` and hand them across. `LocalAPIOriginGuardTests` asserts
//  the adapter against the same vectors the Rust crate uses.
//

import Foundation
import FlyingFox

enum LocalAPIOriginGuard {

    /// Returns `true` iff the request is safe to dispatch — i.e. it really came
    /// from a loopback client and is not a cross-origin browser request that
    /// reached us via DNS rebinding. Applied to EVERY route before dispatch.
    ///
    /// `port` is the port we are actually bound to, not the configured
    /// preference: a fallback bind lands somewhere else, and the `Host` header
    /// names where the client really connected. Passing 0 (not bound yet)
    /// denies, which is the check `LocalAPIServer.guarded` used to make itself.
    static func isAllowed(_ request: HTTPRequest, port: UInt16) -> Bool {
        localApiOriginDecisionIsAllowed(decision: decision(request, port: port))
    }

    /// The full decision, including which check rejected the request. Used for
    /// the log line — "someone is probing us" and "the MCP wrapper is sending
    /// the wrong Host" are different problems and read the same as a bare
    /// `false`.
    static func decision(_ request: HTTPRequest, port: UInt16) -> HwLocalApiOriginDecision {
        localApiCheckOrigin(
            headers: HwLocalApiOriginHeaders(
                host: headerValue(request, "Host"),
                origin: headerValue(request, "Origin"),
                secFetchSite: headerValue(request, "Sec-Fetch-Site")
            ),
            port: port
        )
    }

    /// `HTTPHeader` hashes and compares case-insensitively (RFC 7230 §3.2), so
    /// a single keyed lookup already matches any header-name casing the client
    /// sent.
    private static func headerValue(_ request: HTTPRequest, _ name: String) -> String? {
        request.headers[HTTPHeader(name)]
    }
}
