//! One-shot fixture capture: prints `[{word, codes}, ...]` JSON for a fixed
//! word list to stdout. Run once to (re)generate `tests/fixtures/phonetic.json`:
//!
//!   cargo run --release --example capture_phonetic > ../../tests/fixtures/phonetic.json
//!
//! The word list mixes proper nouns, tech terms and varied spellings — the kind
//! of tokens the vocabulary matchers feed the encoder.
use serde::Serialize;

#[derive(Serialize)]
struct PhoneticCase {
    word: String,
    codes: Vec<String>,
}

const WORDS: &[&str] = &[
    "smith", "schwartz", "anthropic", "claude", "kubernetes", "nginx",
    "github", "naveen", "siobhan", "jose", "muller", "wojciech",
    "katherine", "stephen", "macarthur", "deepgram", "elevenlabs",
];

fn main() {
    let cases: Vec<PhoneticCase> = WORDS
        .iter()
        .map(|w| PhoneticCase {
            word: (*w).to_string(),
            codes: hyperwhisper_core::phonetic_encode((*w).to_string()),
        })
        .collect();
    println!("{}", serde_json::to_string_pretty(&cases).unwrap());
}
