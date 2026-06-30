//! Golden parity test for the phonetic encoder.
//!
//! Fixtures live at `shared-core-rs/tests/fixtures/phonetic.json` (the shared
//! fixtures location from the plan); this driver lives in the crate because
//! Cargo integration tests must belong to a package. Each fixture is an
//! input word and the exact phonetic codes the core must produce. The values
//! lock in current behaviour so future refactors / uniffi bumps can't silently
//! drift the algorithm output the macOS + Windows vocabulary matchers depend on.

use serde::Deserialize;

#[derive(Deserialize)]
struct PhoneticCase {
    word: String,
    codes: Vec<String>,
}

#[test]
fn phonetic_encode_matches_golden_fixtures() {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../tests/fixtures/phonetic.json"
    );
    let raw = std::fs::read_to_string(path)
        .unwrap_or_else(|e| panic!("failed to read fixtures at {path}: {e}"));
    let cases: Vec<PhoneticCase> =
        serde_json::from_str(&raw).expect("fixtures must be valid JSON");

    assert!(!cases.is_empty(), "fixtures must not be empty");

    for case in &cases {
        let got = hyperwhisper_core::phonetic_encode(case.word.clone());
        assert_eq!(
            got, case.codes,
            "phonetic_encode({:?}) drifted from golden output",
            case.word
        );
    }
}
