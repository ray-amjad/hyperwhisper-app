//! Fuzz the `Host` / `Origin` / `Sec-Fetch-Site` parsing, the bearer parse and
//! the token encoder over arbitrary input.
//!
//! What this proves: none of them panics. That matters more here than almost
//! anywhere else in the workspace. The release profile sets `panic = "abort"`,
//! and these three headers are chosen by whoever is talking to the loopback
//! socket — including, by construction, a rebound web page. A panic is not an
//! HTTP 500 in that world: it is the whole HyperWhisper process going away,
//! with no unwinding and no Sentry breadcrumb.
//!
//! Run:
//!   cargo +nightly fuzz run guard -- -max_total_time=300
//!
//! CI never runs this. See fuzz/Cargo.toml for why this package sits outside
//! the shared-core workspace.

#![no_main]

use libfuzzer_sys::fuzz_target;

/// Carve one fuzz input into a port and four header-shaped strings.
///
/// The first two bytes are the bound port, so the engine can drive the
/// port-equality branches (including port 0 and port 80, the two special
/// values) instead of only ever seeing one port. The rest is split on `\n`,
/// which lets a mutation move a byte between headers cheaply — the interesting
/// bugs are in how the parts interact, not in any one of them.
fn carve(data: &[u8]) -> (u16, String, String, String, String) {
    let port = match (data.first(), data.get(1)) {
        (Some(high), Some(low)) => u16::from_be_bytes([*high, *low]),
        _ => 0,
    };
    let rest = data.get(2..).unwrap_or(&[]);
    let text = String::from_utf8_lossy(rest);
    let mut parts = text.split('\n');
    let host = parts.next().unwrap_or_default().to_owned();
    let origin = parts.next().unwrap_or_default().to_owned();
    let fetch_site = parts.next().unwrap_or_default().to_owned();
    let authorization = parts.next().unwrap_or_default().to_owned();
    (port, host, origin, fetch_site, authorization)
}

fuzz_target!(|data: &[u8]| {
    let (port, host, origin, fetch_site, authorization) = carve(data);

    // Present and absent are different code paths in the guard — an absent
    // `Host` denies, an absent `Origin` is fine — so run every combination
    // rather than only the all-present one.
    for host_present in [true, false] {
        for origin_present in [true, false] {
            for fetch_present in [true, false] {
                let headers = hw_localapi::OriginHeaders {
                    host: host_present.then(|| host.clone()),
                    origin: origin_present.then(|| origin.clone()),
                    sec_fetch_site: fetch_present.then(|| fetch_site.clone()),
                };
                let decision = hw_localapi::check_origin(&headers, port);

                // The guard is a pure function: same headers, same answer. A
                // decision that depends on anything else could not be reviewed
                // against the Swift source it ports.
                assert_eq!(decision, hw_localapi::check_origin(&headers, port));

                // Two invariants that hold for every input, and that a
                // refactor of the check order could break silently:
                //
                //   * an unbound server allows nothing;
                //   * an allowed request always carried a `Host`.
                if port == 0 {
                    assert!(!decision.is_allowed(), "port 0 allowed {headers:?}");
                }
                if decision.is_allowed() {
                    assert!(headers.host.is_some(), "allowed with no Host: {headers:?}");
                }
            }
        }
    }

    // The bearer parse, over the same arbitrary text. `authorize` must be
    // false for every token that is not the expected one — the fuzzer is
    // vanishingly unlikely to produce the 43-character match, so a `true` here
    // is a real finding.
    let expected = hw_localapi::generate_token(&[0x5Au8; hw_localapi::TOKEN_ENTROPY_BYTES])
        .expect("32 bytes is the contract");
    let presented = hw_localapi::bearer_token(Some(authorization.as_str()));
    if hw_localapi::authorize(Some(authorization.as_str()), &expected) {
        assert_eq!(
            presented,
            Some(expected.as_str()),
            "authorized a header that does not carry the token: {authorization:?}"
        );
    }
    // An empty stored credential authorizes nothing, whatever is presented.
    assert!(!hw_localapi::authorize(Some(authorization.as_str()), ""));

    // The encoder, over arbitrary bytes. It must never emit a character
    // outside the URL alphabet — a `+`, `/` or `=` in a token breaks the
    // header or the URL a wrapper builds from it.
    let encoded = hw_localapi::base64url_encode(data);
    assert!(
        encoded
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_')),
        "encoder escaped the URL alphabet: {encoded:?}"
    );

    // And `generate_token` accepts exactly 32 bytes, never more or fewer.
    let token = hw_localapi::generate_token(data);
    assert_eq!(
        token.is_ok(),
        data.len() == hw_localapi::TOKEN_ENTROPY_BYTES,
        "generate_token disagreed with its own length contract"
    );
    if let Ok(token) = token {
        assert_eq!(token.len(), hw_localapi::TOKEN_LENGTH);
        assert!(hw_localapi::is_well_formed_token(&token));
    }
});
