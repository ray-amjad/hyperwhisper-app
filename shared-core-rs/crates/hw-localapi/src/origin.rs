//! The DNS-rebind guard — a line-for-line port of the macOS
//! `LocalAPIOriginGuard.swift`, which was the only platform that shipped one
//! (issue #289).
//!
//! # The attack it stops
//!
//! A malicious page rebinds `attacker.com` to `127.0.0.1` and then reads the
//! Local API's responses cross-origin. The one thing the page cannot forge is
//! the `Host` header: the browser still sends `Host: attacker.com`. Requiring
//! `Host` to be exactly `127.0.0.1:<bound port>` or `localhost:<bound port>`,
//! and rejecting cross-site `Origin` / `Sec-Fetch-Site`, drops the rebound
//! request before it reaches a handler. See issue #730 for the original report.
//!
//! # Ported exactly, with the Swift quirks kept
//!
//! Three Swift behaviours look like bugs and are preserved on purpose, because
//! macOS ships them today and this crate must not change that platform's wire
//! behaviour:
//!
//! 1. **A `Host` with an unparsable port falls back to the port-80 rule.**
//!    `splitHostPort` returns `(name, UInt16(portString))`, and a `nil` port is
//!    indistinguishable from "no colon at all". So `localhost:abc` is treated
//!    as `localhost` with no port, and is allowed only when the server is bound
//!    to port 80 — which it never is. It denies either way; only the *reason*
//!    differs.
//! 2. **An empty `Sec-Fetch-Site` denies.** Swift's `if let` tests for presence,
//!    not for content, so a header sent as `Sec-Fetch-Site:` normalizes to `""`
//!    and matches neither `same-origin` nor `none`.
//! 3. **An empty `Origin` is skipped, but an empty `Host` denies.** `Origin` is
//!    guarded with `!origin.isEmpty`; `Host` has its own `!host.isEmpty` that
//!    denies instead.
//!
//! # The one deliberate deviation: percent-encoding in `Origin`
//!
//! Swift reads the host through `URL(string:)?.host`, and Foundation
//! percent-decodes that property. `http://%6cocalhost:51671` would therefore
//! decode to `localhost` and be **allowed** on macOS. [`parse_origin_authority`]
//! does not decode, so that shape is **denied** here.
//!
//! This narrows the guard, never widens it. No browser emits a percent-encoded
//! host in an `Origin` header — the HTML standard serializes an origin from the
//! already-canonical host — so the only requests the change can reject are ones
//! no legitimate client sends. It is called out in the decision-vector table
//! below (`ORIGIN_PERCENT_ENCODED_HOST`) so the difference is visible rather
//! than implied.

/// The hostnames the `Host` header and the `Origin` URL may name. The bound
/// port is checked separately. Anything else — a rebound `attacker.com`, a LAN
/// IP, `0.0.0.0`, an IPv6 literal — is rejected.
///
/// Mirrors `LocalAPIOriginGuard.allowedHosts`.
const ALLOWED_HOSTS: [&str; 2] = ["127.0.0.1", "localhost"];

/// The three request headers the guard reads. Everything else about the request
/// — method, path, body — is irrelevant to it.
///
/// Header lookup is the caller's job and must be case-insensitive (RFC 7230
/// §3.2). Every head already has a case-insensitive header map: FlyingFox's
/// `HTTPHeader`, ASP.NET Core's `IHeaderDictionary`.
#[derive(Debug, Default, Clone)]
pub struct OriginHeaders {
    /// `Host`. Absent is a denial — HTTP/1.1 requires the header.
    pub host: Option<String>,
    /// `Origin`. Absent or empty is fine; a non-loopback value is a denial.
    pub origin: Option<String>,
    /// `Sec-Fetch-Site`. Browsers attach it to `fetch()`/XHR; curl and the MCP
    /// wrapper omit it, so absent is fine.
    pub sec_fetch_site: Option<String>,
}

/// Why the guard let a request through, or why it did not.
///
/// The reasons exist so the decision-vector table can assert on the *path*
/// taken and not only on the allow/deny bit — two different denials that both
/// report "denied" would hide a port that got the order of the checks wrong.
/// Every head collapses this to [`OriginDecision::is_allowed`]; the wire
/// response is the same 403 whichever denial fired, so no reason ever reaches a
/// client.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OriginDecision {
    /// Safe to dispatch.
    Allow,
    /// The server is not bound yet, so no `Host` value can be verified against
    /// a port. `LocalAPIServer.guarded` does this check itself
    /// (`guard port > 0`) before calling the guard; it lives inside the guard
    /// here so all three heads inherit it.
    DeniedPortUnknown,
    /// No `Host` header, or one that is empty after trimming.
    DeniedMissingHost,
    /// The `Host` header does not name loopback on the bound port.
    DeniedHost,
    /// `Sec-Fetch-Site` was present and was neither `same-origin` nor `none`.
    DeniedFetchSite,
    /// `Origin` was present and non-empty and did not name loopback on the
    /// bound port.
    DeniedOrigin,
}

impl OriginDecision {
    /// True only for [`OriginDecision::Allow`]. This is the bit the servers act
    /// on; a false means the request never reaches a handler and gets the
    /// [`crate::forbidden_origin`] response.
    #[must_use]
    pub fn is_allowed(self) -> bool {
        matches!(self, OriginDecision::Allow)
    }
}

/// Decide whether a request is safe to dispatch.
///
/// Applied to EVERY route, including the unauthenticated `GET /health`, before
/// the bearer check and before any dispatch. `port` is the port the server is
/// actually bound to, not the configured preference — a fallback bind lands
/// somewhere else and the `Host` header must name where the client really
/// connected.
///
/// Mirrors `LocalAPIOriginGuard.isAllowed(_:port:)`.
#[must_use]
pub fn check_origin(headers: &OriginHeaders, port: u16) -> OriginDecision {
    if port == 0 {
        return OriginDecision::DeniedPortUnknown;
    }

    // 1. Host header must name loopback on our bound port. A rebound page
    //    carries `Host: attacker.com[:port]`, which never matches.
    let host = match headers.host.as_deref() {
        Some(value) => trim_swift_whitespace(value),
        None => return OriginDecision::DeniedMissingHost,
    };
    if host.is_empty() {
        return OriginDecision::DeniedMissingHost;
    }
    if !host_matches_loopback(host, port) {
        return OriginDecision::DeniedHost;
    }

    // 2. Reject any cross-site fetch metadata. Same-origin and direct
    //    navigations send `same-origin` / `none`; cross-site rebinding sends
    //    `cross-site` (or `same-site`). Non-browser clients omit the header
    //    entirely, so absence is allowed.
    if let Some(fetch_site) = headers.sec_fetch_site.as_deref() {
        let normalized = trim_swift_whitespace(fetch_site).to_lowercase();
        if normalized != "same-origin" && normalized != "none" {
            return OriginDecision::DeniedFetchSite;
        }
    }

    // 3. If an `Origin` is present it must point at loopback on our port. A
    //    rebound page's Origin is `http://attacker.com[:port]`.
    if let Some(origin) = headers.origin.as_deref() {
        if !origin.is_empty() && !origin_is_loopback(origin, port) {
            return OriginDecision::DeniedOrigin;
        }
    }

    OriginDecision::Allow
}

// ---------------------------------------------------------------------------
// Host
// ---------------------------------------------------------------------------

/// Match a `Host` header value (e.g. `127.0.0.1:39201`) against the loopback
/// allowlist on the bound port.
///
/// Mirrors `LocalAPIOriginGuard.hostMatchesLoopback(_:port:)`, including the
/// bare-host branch: a `Host` with no port implies port 80, which the loopback
/// server is never bound to, so in practice that branch always denies.
fn host_matches_loopback(host: &str, port: u16) -> bool {
    let (name, host_port) = split_host_port(host);
    if !is_allowed_host(name) {
        return false;
    }
    match host_port {
        Some(value) => value == port,
        None => port == 80,
    }
}

/// Split a `host[:port]` string on its LAST colon.
///
/// IPv6 literals are not handled, and that is correct rather than an oversight:
/// the server binds IPv4 loopback only, so `[::1]:51671` splits into a name of
/// `[::1]` which is not in the allowlist. Mirrors
/// `LocalAPIOriginGuard.splitHostPort`.
fn split_host_port(host: &str) -> (&str, Option<u16>) {
    match host.rfind(':') {
        Some(colon) => {
            let name = host.get(..colon).unwrap_or("");
            let port = host.get(colon.saturating_add(1)..).unwrap_or("");
            (name, parse_swift_uint16(port))
        }
        None => (host, None),
    }
}

/// Case-insensitive membership in [`ALLOWED_HOSTS`].
///
/// Swift's `lowercased()` is a full Unicode case mapping and the allowlist is
/// pure ASCII, so `to_lowercase` matches it for every input that can pass.
fn is_allowed_host(name: &str) -> bool {
    let lowered = name.to_lowercase();
    ALLOWED_HOSTS.iter().any(|allowed| *allowed == lowered)
}

// ---------------------------------------------------------------------------
// Origin
// ---------------------------------------------------------------------------

/// Whether an `Origin` header names loopback on the bound port.
///
/// Mirrors `LocalAPIOriginGuard.isURLLoopback(_:port:)`, whose port rule is
/// `UInt16(exactly: url.port) == port` for an explicit port and `port == 80`
/// for none (the `http` scheme default).
fn origin_is_loopback(origin: &str, port: u16) -> bool {
    let Some((host, origin_port)) = parse_origin_authority(origin) else {
        return false;
    };
    if !is_allowed_host(host) {
        return false;
    }
    match origin_port {
        Some(value) => value == port,
        None => port == 80,
    }
}

/// Pull `(host, port)` out of an absolute URL, or `None` when the string is not
/// one.
///
/// This stands in for `URL(string:)` and is deliberately stricter than
/// Foundation on one point — it does not percent-decode the host. See the
/// module docs for why that narrowing is safe.
///
/// The shape accepted is RFC 3986's: `scheme "://" [userinfo "@"] host
/// [":" port] [ "/" | "?" | "#" ... ]`. Anything without a scheme and an
/// authority — `null`, a bare `127.0.0.1:51671`, a relative path — yields
/// `None`, exactly as `URL(string:)?.host` yields `nil` for them.
fn parse_origin_authority(origin: &str) -> Option<(&str, Option<u16>)> {
    let (scheme, remainder) = origin.split_once("://")?;
    if !is_valid_scheme(scheme) {
        return None;
    }

    // The authority runs to the first `/`, `?` or `#`. A browser-serialized
    // origin has none of them, but `URL(string:)` accepts a full URL here and
    // so must this.
    let authority = remainder
        .find(['/', '?', '#'])
        .and_then(|end| remainder.get(..end))
        .unwrap_or(remainder);

    // Userinfo, if any, is everything before the LAST `@` — a password may
    // contain `@`, a host may not.
    let host_port = match authority.rfind('@') {
        Some(at) => authority.get(at.saturating_add(1)..).unwrap_or(""),
        None => authority,
    };
    if host_port.is_empty() {
        return None;
    }

    // An IPv6 literal is bracketed, and its colons are not port separators.
    // Neither `::1` nor anything else bracketed is in the allowlist, so this
    // branch only exists to keep the port split from misreading them.
    if let Some(rest) = host_port.strip_prefix('[') {
        let close = rest.find(']')?;
        let host = rest.get(..close).unwrap_or("");
        let after = rest.get(close.saturating_add(1)..).unwrap_or("");
        return Some((host, parse_url_port(after.strip_prefix(':'))?));
    }

    match host_port.rsplit_once(':') {
        Some((host, port)) => Some((host, parse_url_port(Some(port))?)),
        None => Some((host_port, None)),
    }
}

/// A URL port component: absent or empty means "scheme default", digits mean a
/// port, and anything else means the whole URL is invalid.
///
/// The outer `Option` distinguishes "not a URL at all" (RFC 3986 says the port
/// is `*DIGIT`, so `:abc` makes `URL(string:)` return `nil`) from the inner
/// `None`, which is a URL with no explicit port. That is the split Swift gets
/// for free from Foundation and this port has to make explicitly.
fn parse_url_port(port: Option<&str>) -> Option<Option<u16>> {
    match port {
        None => Some(None),
        // `http://localhost:` — RFC 3986 permits an empty port and Foundation
        // reports `url.port == nil`, i.e. the scheme default.
        Some("") => Some(None),
        Some(digits) => {
            if !digits.bytes().all(|byte| byte.is_ascii_digit()) {
                return None;
            }
            // A port that does not fit a `u16` is still a valid URL; Swift's
            // `UInt16(exactly: url.port)` is then `nil`, which never equals the
            // bound port. `Some(None)` reproduces that: no explicit port, so
            // the port-80 rule applies and denies.
            Some(digits.parse::<u16>().ok())
        }
    }
}

/// RFC 3986 §3.1: `ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )`.
fn is_valid_scheme(scheme: &str) -> bool {
    let mut bytes = scheme.bytes();
    match bytes.next() {
        Some(first) if first.is_ascii_alphabetic() => {}
        _ => return false,
    }
    bytes.all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'+' | b'-' | b'.'))
}

// ---------------------------------------------------------------------------
// Swift primitives
// ---------------------------------------------------------------------------

/// Trim the characters Swift's `trimmingCharacters(in: .whitespaces)` trims:
/// the horizontal tab plus every scalar in Unicode general category `Zs`.
///
/// Notably NOT newline, carriage return or form feed — those are in
/// `.newlines`, a different set. A `Host` header value of `"localhost:51671\n"`
/// therefore keeps its newline on macOS and fails to match, and it fails to
/// match here for the same reason.
fn trim_swift_whitespace(value: &str) -> &str {
    value.trim_matches(is_swift_whitespace)
}

/// The `.whitespaces` set: `U+0009` plus general category `Zs`.
fn is_swift_whitespace(character: char) -> bool {
    matches!(
        character,
        '\u{0009}' | '\u{0020}' | '\u{00A0}' | '\u{1680}' | '\u{2000}'
            ..='\u{200A}' | '\u{202F}' | '\u{205F}' | '\u{3000}'
    )
}

/// Swift's `UInt16(_ text: String)`: an optional leading `+` or `-`, then one or
/// more ASCII digits, and the value must fit.
///
/// The sign matters. `UInt16("+80")` is `80` in Swift, and `UInt16("-0")` is
/// `0`; a plain `str::parse::<u16>` accepts `+80` but rejects `-0`. Both shapes
/// only ever reach here from a `Host` header, and both deny in the end — `-0`
/// yields port 0, which the server is never bound to — but the port is faithful
/// rather than approximately faithful.
fn parse_swift_uint16(text: &str) -> Option<u16> {
    let (negative, digits) = match text.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, text.strip_prefix('+').unwrap_or(text)),
    };
    if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    let value = digits.parse::<u32>().ok()?;
    if negative {
        // Only `-0` fits an unsigned type.
        return if value == 0 { Some(0) } else { None };
    }
    u16::try_from(value).ok()
}

#[cfg(test)]
mod tests {
    use super::{check_origin, OriginDecision, OriginHeaders};

    /// The port every vector runs against, and the one shape the server really
    /// binds: an ephemeral high port, never 80.
    const PORT: u16 = 51671;

    /// One row of the decision-vector table.
    struct Vector {
        /// The name is the documentation. It says which Swift branch the row
        /// pins, so a future edit that moves a check knows what it broke.
        name: &'static str,
        host: Option<&'static str>,
        origin: Option<&'static str>,
        sec_fetch_site: Option<&'static str>,
        port: u16,
        expected: OriginDecision,
    }

    /// The decision vectors, derived from `LocalAPIOriginGuard.swift` branch by
    /// branch. Every `guard`/`if` in the Swift source has at least one row that
    /// takes it and one that does not.
    ///
    /// This table IS the contract. A head that stops agreeing with it has
    /// re-introduced the drift issue #289 exists to close.
    const VECTORS: &[Vector] = &[
        // --- the happy paths every real client takes ---
        Vector {
            name: "CURL_LOOPBACK_IP",
            host: Some("127.0.0.1:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "CURL_LOCALHOST",
            host: Some("localhost:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "HOST_CASE_INSENSITIVE",
            host: Some("LocalHost:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "HOST_SURROUNDING_SPACES_TRIMMED",
            host: Some("  127.0.0.1:51671\t"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "SAME_ORIGIN_BROWSER_FETCH",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1:51671"),
            sec_fetch_site: Some("same-origin"),
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "DIRECT_NAVIGATION_SENDS_NONE",
            host: Some("localhost:51671"),
            origin: None,
            sec_fetch_site: Some("None"),
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "FETCH_SITE_TRIMMED_AND_LOWERCASED",
            host: Some("localhost:51671"),
            origin: None,
            sec_fetch_site: Some(" SAME-ORIGIN "),
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "EMPTY_ORIGIN_IS_SKIPPED",
            host: Some("127.0.0.1:51671"),
            origin: Some(""),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "ORIGIN_WITH_TRAILING_SLASH",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1:51671/"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "ORIGIN_HTTPS_ON_THE_BOUND_PORT",
            host: Some("localhost:51671"),
            origin: Some("https://localhost:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        // --- the attack the guard exists for ---
        Vector {
            name: "REBOUND_HOST_IS_THE_ATTACK",
            host: Some("attacker.com:51671"),
            origin: Some("http://attacker.com:51671"),
            sec_fetch_site: Some("cross-site"),
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "REBOUND_HOST_ALONE",
            host: Some("attacker.com"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        // --- Host branch ---
        Vector {
            name: "NO_HOST_HEADER",
            host: None,
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedMissingHost,
        },
        Vector {
            name: "EMPTY_HOST_HEADER",
            host: Some(""),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedMissingHost,
        },
        Vector {
            name: "WHITESPACE_ONLY_HOST_HEADER",
            host: Some("   "),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedMissingHost,
        },
        Vector {
            name: "HOST_ON_THE_WRONG_PORT",
            host: Some("127.0.0.1:51672"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_WITHOUT_A_PORT_IMPLIES_80",
            host: Some("127.0.0.1"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_WITHOUT_A_PORT_WHEN_BOUND_TO_80",
            host: Some("127.0.0.1"),
            origin: None,
            sec_fetch_site: None,
            port: 80,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "HOST_LEADING_ZERO_PORT_PARSES",
            host: Some("localhost:051671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "HOST_UNPARSABLE_PORT_FALLS_BACK_TO_80",
            host: Some("localhost:abc"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_EMPTY_PORT_FALLS_BACK_TO_80",
            host: Some("localhost:"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_PORT_OVERFLOWS_UINT16",
            host: Some("localhost:99999"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_IS_ONLY_A_PORT",
            host: Some(":51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_IPV6_LOOPBACK_IS_NOT_ALLOWLISTED",
            host: Some("[::1]:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_LAN_IP",
            host: Some("192.168.1.10:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_WILDCARD_ADDRESS",
            host: Some("0.0.0.0:51671"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        Vector {
            name: "HOST_NEWLINE_IS_NOT_TRIMMED",
            host: Some("localhost:51671\n"),
            origin: None,
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        // --- Sec-Fetch-Site branch ---
        Vector {
            name: "FETCH_SITE_CROSS_SITE",
            host: Some("127.0.0.1:51671"),
            origin: None,
            sec_fetch_site: Some("cross-site"),
            port: PORT,
            expected: OriginDecision::DeniedFetchSite,
        },
        Vector {
            name: "FETCH_SITE_SAME_SITE_IS_ALSO_REJECTED",
            host: Some("127.0.0.1:51671"),
            origin: None,
            sec_fetch_site: Some("same-site"),
            port: PORT,
            expected: OriginDecision::DeniedFetchSite,
        },
        Vector {
            name: "FETCH_SITE_PRESENT_BUT_EMPTY",
            host: Some("127.0.0.1:51671"),
            origin: None,
            sec_fetch_site: Some(""),
            port: PORT,
            expected: OriginDecision::DeniedFetchSite,
        },
        Vector {
            name: "HOST_IS_CHECKED_BEFORE_FETCH_SITE",
            host: Some("attacker.com:51671"),
            origin: None,
            sec_fetch_site: Some("same-origin"),
            port: PORT,
            expected: OriginDecision::DeniedHost,
        },
        // --- Origin branch ---
        Vector {
            name: "ORIGIN_REBOUND",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://attacker.com:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_ON_THE_WRONG_PORT",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1:51672"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_WITHOUT_A_PORT_IMPLIES_80",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_NULL_IS_NOT_A_URL",
            host: Some("127.0.0.1:51671"),
            origin: Some("null"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_WITH_NO_SCHEME",
            host: Some("127.0.0.1:51671"),
            origin: Some("127.0.0.1:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_FILE_SCHEME",
            host: Some("127.0.0.1:51671"),
            origin: Some("file:///etc/passwd"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_USERINFO_IS_STRIPPED",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://user:pass@127.0.0.1:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::Allow,
        },
        Vector {
            name: "ORIGIN_USERINFO_CANNOT_SMUGGLE_A_HOST",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1:51671@attacker.com"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_NON_NUMERIC_PORT_IS_NOT_A_URL",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://127.0.0.1:abc"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_IPV6_LOOPBACK_IS_NOT_ALLOWLISTED",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://[::1]:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            // The one deliberate deviation from Foundation. macOS decodes the
            // escape and allows this; the crate does not decode and denies.
            name: "ORIGIN_PERCENT_ENCODED_HOST",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://%6cocalhost:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_TRAILING_DOT_IS_A_DIFFERENT_HOST",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://localhost.:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "ORIGIN_SUBDOMAIN_OF_LOCALHOST",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://evil.localhost:51671"),
            sec_fetch_site: None,
            port: PORT,
            expected: OriginDecision::DeniedOrigin,
        },
        Vector {
            name: "FETCH_SITE_IS_CHECKED_BEFORE_ORIGIN",
            host: Some("127.0.0.1:51671"),
            origin: Some("http://attacker.com"),
            sec_fetch_site: Some("cross-site"),
            port: PORT,
            expected: OriginDecision::DeniedFetchSite,
        },
        // --- the bind-race branch ---
        Vector {
            name: "PORT_ZERO_MEANS_NOT_BOUND_YET",
            host: Some("127.0.0.1:51671"),
            origin: None,
            sec_fetch_site: None,
            port: 0,
            expected: OriginDecision::DeniedPortUnknown,
        },
    ];

    #[test]
    fn the_decision_vectors_hold() {
        for vector in VECTORS {
            let headers = OriginHeaders {
                host: vector.host.map(str::to_owned),
                origin: vector.origin.map(str::to_owned),
                sec_fetch_site: vector.sec_fetch_site.map(str::to_owned),
            };
            assert_eq!(
                check_origin(&headers, vector.port),
                vector.expected,
                "vector {} disagrees",
                vector.name
            );
        }
    }

    /// Every reason is reachable. A denial reason nothing produces is a check
    /// that has become unreachable — which is how a guard silently stops
    /// guarding.
    #[test]
    fn every_decision_reason_is_covered() {
        for reason in [
            OriginDecision::Allow,
            OriginDecision::DeniedPortUnknown,
            OriginDecision::DeniedMissingHost,
            OriginDecision::DeniedHost,
            OriginDecision::DeniedFetchSite,
            OriginDecision::DeniedOrigin,
        ] {
            assert!(
                VECTORS.iter().any(|vector| vector.expected == reason),
                "no vector produces {reason:?}"
            );
        }
    }

    /// `is_allowed` is the only bit a head reads, so it must agree with the
    /// table on every row.
    #[test]
    fn only_allow_is_allowed() {
        for vector in VECTORS {
            let headers = OriginHeaders {
                host: vector.host.map(str::to_owned),
                origin: vector.origin.map(str::to_owned),
                sec_fetch_site: vector.sec_fetch_site.map(str::to_owned),
            };
            assert_eq!(
                check_origin(&headers, vector.port).is_allowed(),
                vector.expected == OriginDecision::Allow,
                "vector {} disagrees on is_allowed",
                vector.name
            );
        }
    }

    /// Arbitrary bytes in every header, at every port, must return a decision
    /// rather than panic. The fuzz target covers this far more thoroughly; this
    /// keeps a cheap version in the suite CI actually runs.
    #[test]
    fn odd_inputs_decide_rather_than_panic() {
        let odd = [
            "",
            ":",
            "::",
            ":::",
            "@",
            "[",
            "]",
            "[]",
            "[:",
            "://",
            "a://",
            "http://",
            "http://@",
            "http://:",
            "http://[",
            "http://[]",
            "http://[]:",
            "%",
            "%6",
            "\u{0}",
            "\u{feff}",
            "localhost:\u{0}",
            "localhost:1\u{0}",
            "ＬＯＣＡＬＨＯＳＴ:51671",
            "localhost:+51671",
            "localhost:-0",
            "localhost:-1",
            "localhost: 51671",
            "\u{3000}localhost:51671\u{3000}",
        ];
        for host in odd {
            for origin in odd {
                for port in [0u16, 80, PORT, u16::MAX] {
                    let headers = OriginHeaders {
                        host: Some(host.to_owned()),
                        origin: Some(origin.to_owned()),
                        sec_fetch_site: Some(origin.to_owned()),
                    };
                    let _ = check_origin(&headers, port);
                }
            }
        }
    }

    /// Two of the odd shapes above are worth pinning rather than only
    /// surviving: a full-width host must not case-fold into the allowlist, and
    /// Swift's signed-integer parse must accept `+`.
    #[test]
    fn the_swift_integer_parse_quirks_are_preserved() {
        let allow_on = |host: &str, port: u16| {
            check_origin(
                &OriginHeaders {
                    host: Some(host.to_owned()),
                    ..OriginHeaders::default()
                },
                port,
            )
        };
        assert_eq!(allow_on("localhost:+51671", PORT), OriginDecision::Allow);
        // `-0` parses to port 0, which no server is ever bound to.
        assert_eq!(
            allow_on("localhost:-0", 0),
            OriginDecision::DeniedPortUnknown
        );
        assert_eq!(allow_on("localhost:-1", PORT), OriginDecision::DeniedHost);
        // Full-width letters are not ASCII `localhost` after lowercasing.
        assert_eq!(
            allow_on("ＬＯＣＡＬＨＯＳＴ:51671", PORT),
            OriginDecision::DeniedHost
        );
    }
}
