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

import Foundation
import FlyingFox

enum LocalAPIOriginGuard {

    /// Hostnames we accept in the `Host` header. The bound port is appended at
    /// check time. Anything else (a rebound `attacker.com`, a LAN IP, `0.0.0.0`)
    /// is rejected.
    private static let allowedHosts: Set<String> = ["127.0.0.1", "localhost"]

    /// Returns `true` iff the request is safe to dispatch — i.e. it really came
    /// from a loopback client and is not a cross-origin browser request that
    /// reached us via DNS rebinding. Applied to EVERY route before dispatch.
    static func isAllowed(_ request: HTTPRequest, port: UInt16) -> Bool {
        // 1. Host header must name loopback on our bound port. A rebound page
        //    carries `Host: attacker.com[:port]`, which never matches.
        guard isHostAllowed(request, port: port) else { return false }

        // 2. Reject any cross-site fetch metadata. Browsers attach
        //    `Sec-Fetch-Site` to fetch()/XHR; same-origin and direct
        //    navigations send `same-origin` / `none`, cross-site rebinding
        //    sends `cross-site` (or `same-site`). Non-browser clients (curl,
        //    the MCP wrapper) omit the header entirely, so absence is allowed.
        if let fetchSite = headerValue(request, "Sec-Fetch-Site") {
            let normalized = fetchSite.trimmingCharacters(in: .whitespaces).lowercased()
            guard normalized == "same-origin" || normalized == "none" else { return false }
        }

        // 3. If an `Origin` is present it must point at loopback on our port.
        //    A rebound page's Origin is `http://attacker.com[:port]`.
        if let origin = headerValue(request, "Origin"), !origin.isEmpty {
            guard isURLLoopback(origin, port: port) else { return false }
        }

        return true
    }

    // MARK: - Host

    private static func isHostAllowed(_ request: HTTPRequest, port: UInt16) -> Bool {
        guard let host = headerValue(request, "Host")?
            .trimmingCharacters(in: .whitespaces), !host.isEmpty else {
            // No Host header at all — HTTP/1.1 requires one; treat its absence
            // as suspicious and reject.
            return false
        }
        return hostMatchesLoopback(host, port: port)
    }

    /// Match `host` (a `Host` header value, e.g. `127.0.0.1:39201`) against the
    /// loopback allowlist on the given port. Accepts the bare host when the
    /// port is the default HTTP port, though in practice the loopback server is
    /// never on 80.
    private static func hostMatchesLoopback(_ host: String, port: UInt16) -> Bool {
        let (name, hostPort) = splitHostPort(host)
        guard allowedHosts.contains(name.lowercased()) else { return false }
        if let hostPort {
            return hostPort == port
        }
        // No explicit port in the Host header → implies port 80. Only allow if
        // we are actually bound there (we are not, normally).
        return port == 80
    }

    // MARK: - Origin URL

    private static func isURLLoopback(_ urlString: String, port: UInt16) -> Bool {
        guard let url = URL(string: urlString), let host = url.host else { return false }
        guard allowedHosts.contains(host.lowercased()) else { return false }
        if let urlPort = url.port {
            return UInt16(exactly: urlPort) == port
        }
        // Origin without an explicit port → scheme default (80 for http).
        return port == 80
    }

    // MARK: - Helpers

    /// Split a `host[:port]` string. IPv6 literals are not used by this server
    /// (we bind IPv4 loopback only), so a simple last-colon split is correct.
    private static func splitHostPort(_ host: String) -> (host: String, port: UInt16?) {
        guard let colon = host.lastIndex(of: ":") else { return (host, nil) }
        let name = String(host[host.startIndex..<colon])
        let portString = String(host[host.index(after: colon)...])
        return (name, UInt16(portString))
    }

    /// `HTTPHeader` hashes and compares case-insensitively (RFC 7230 §3.2), so
    /// a single keyed lookup already matches any header-name casing the client
    /// sent.
    private static func headerValue(_ request: HTTPRequest, _ name: String) -> String? {
        request.headers[HTTPHeader(name)]
    }
}
