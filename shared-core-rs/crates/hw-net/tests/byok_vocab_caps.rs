//! Regression tests for issue #2610: the direct (bring-your-own-key) Deepgram,
//! Soniox and ElevenLabs request builders apply each vendor's vocabulary limit,
//! the way the live path and the Cloud adapters already do.
//!
//! - Deepgram: at most 100 `keyterm` / `keywords` values (500-token keyterm
//!   limit, 400 past it).
//! - Soniox: `context` at most 200 terms and 10,000 characters, cut between
//!   terms.
//! - ElevenLabs Scribe v2: a keyterm of more than 5 words is dropped.
//!
//! The first three tests are the bug hunt's repro (bh-20261026) as filed.
use hw_net::providers::{deepgram, elevenlabs, soniox};
use hw_net::{Body, Part, TranscribeParams};

fn vocab(n: usize, len: usize) -> Vec<String> {
    (0..n)
        .map(|i| format!("{:0>width$}", format!("term{i}"), width = len))
        .collect()
}

fn deepgram_values(p: &TranscribeParams, name: &str) -> Vec<String> {
    let req = deepgram::build_transcribe_request(p).unwrap();
    let query = req.url.split_once('?').map(|(_, q)| q).unwrap_or("");
    query
        .split('&')
        .filter_map(|kv| kv.split_once('='))
        .filter(|(k, _)| *k == name)
        .map(|(_, v)| v.to_string())
        .collect()
}

fn soniox_context(p: &TranscribeParams) -> Option<String> {
    let req = soniox::build_create_request(p, "file-1").unwrap();
    let Body::Bytes { data, .. } = &req.body else {
        panic!("expected JSON body")
    };
    let json: serde_json::Value = serde_json::from_slice(data).unwrap();
    json["context"].as_str().map(str::to_string)
}

fn elevenlabs_keyterms(p: &TranscribeParams) -> Vec<String> {
    let req = elevenlabs::build_transcribe_request(p).unwrap();
    let Body::Multipart { parts, .. } = &req.body else {
        panic!("expected multipart")
    };
    parts
        .iter()
        .filter_map(|p| match p {
            Part::Field { name, value } if name == "keyterms" => Some(value.clone()),
            _ => None,
        })
        .collect()
}

/// Deepgram batch (Nova-3, explicit language): the live path, the cloud REST
/// adapter and the macOS VocabularyView copy ("only the first 100 are used")
/// all cap at 100 keyterms; the catalog records a hard 500-token keyterm limit
/// (400 if exceeded).
#[test]
fn deepgram_batch_caps_keyterms_at_100() {
    let p = TranscribeParams {
        api_key: "dg-test".into(),
        model: "nova-3-general".into(),
        language: Some("en".into()),
        vocabulary: vocab(300, 12),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let req = deepgram::build_transcribe_request(&p).unwrap();
    let n = req.url.matches("keyterm=").count();
    assert!(
        n <= 100,
        "Deepgram batch request carries {n} keyterm params (expected <= 100)"
    );
}

/// Soniox: the catalog says the whole `context` is capped at 8,000 tokens
/// (~10,000 chars); the cloud adapter caps at 200 terms.
#[test]
fn soniox_context_stays_under_vendor_cap() {
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: vocab(400, 60),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let ctx = soniox_context(&p).unwrap_or_default();
    assert!(
        ctx.chars().count() <= 10_000,
        "Soniox context is {} chars (expected <= 10,000)",
        ctx.chars().count()
    );
}

/// ElevenLabs Scribe v2: catalog maxWordsPerPhrase = 5, and the cloud adapter
/// drops terms over 5 words.
#[test]
fn elevenlabs_drops_keyterms_over_five_words() {
    let p = TranscribeParams {
        api_key: "el-test".into(),
        model: "scribe_v2".into(),
        vocabulary: vec!["alpha".into(), "one two three four five six".into()],
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let terms = elevenlabs_keyterms(&p);
    assert!(
        terms.iter().all(|t| t.split_whitespace().count() <= 5),
        "ElevenLabs keyterms include a >5-word phrase: {terms:?}"
    );
}

/// The cap keeps the FIRST 100 terms in the user's order, on both the Nova-3
/// `keyterm` and the older-model `keywords` parameter.
#[test]
fn deepgram_keeps_the_first_100_terms_in_order_for_both_params() {
    let words: Vec<String> = (0..150).map(|i| format!("w{i}")).collect();
    let keyterm = TranscribeParams {
        api_key: "dg-test".into(),
        model: "nova-3-general".into(),
        language: Some("en".into()),
        vocabulary: words.clone(),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let expected: Vec<String> = (0..100).map(|i| format!("w{i}")).collect();
    assert_eq!(deepgram_values(&keyterm, "keyterm"), expected);

    let keywords = TranscribeParams {
        model: "nova-2-general".into(),
        ..keyterm
    };
    let expected: Vec<String> = (0..100).map(|i| format!("w{i}:1.5")).collect();
    assert_eq!(deepgram_values(&keywords, "keywords"), expected);
}

/// Duplicates and blanks do not use one of the 100 slots: the cap counts the
/// terms that are sent.
#[test]
fn deepgram_duplicates_do_not_use_a_slot() {
    let mut words: Vec<String> = vec!["dup".into(), "DUP".into(), "  ".into()];
    words.extend((0..120).map(|i| format!("w{i}")));
    let p = TranscribeParams {
        api_key: "dg-test".into(),
        model: "nova-3-general".into(),
        language: Some("en".into()),
        vocabulary: words,
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let sent = deepgram_values(&p, "keyterm");
    assert_eq!(sent.len(), 100);
    assert_eq!(sent[0], "dup");
    assert_eq!(sent[99], "w98");
}

/// A vocabulary under both Soniox limits is sent whole, unchanged.
#[test]
fn soniox_small_vocabulary_is_sent_whole() {
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: vec!["Kubernetes".into(), "HyperWhisper Cloud".into()],
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    assert_eq!(
        soniox_context(&p).as_deref(),
        Some("Kubernetes, HyperWhisper Cloud")
    );
}

/// Short terms stop at 200, the Cloud adapter's term cap, well under the
/// character cap.
#[test]
fn soniox_stops_at_200_terms() {
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: (0..250).map(|i| format!("t{i}")).collect(),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let ctx = soniox_context(&p).unwrap();
    let terms: Vec<&str> = ctx.split(", ").collect();
    assert_eq!(terms.len(), 200);
    assert_eq!(terms[199], "t199");
}

/// The character cap cuts between whole terms: every sent term is one the user
/// wrote, in order, and the term that would pass 10,000 characters is the first
/// one left out.
#[test]
fn soniox_character_cap_keeps_whole_terms_in_order() {
    // 80-char terms (the sanitizer's own cap): 121 of them joined with ", " are
    // 121*80 + 120*2 = 9,920 chars; a 122nd would make 10,002.
    let words: Vec<String> = (0..140).map(|i| format!("{:x>80}", i)).collect();
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: words.clone(),
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let ctx = soniox_context(&p).unwrap();
    let sent: Vec<&str> = ctx.split(", ").collect();
    assert_eq!(sent.len(), 121);
    assert_eq!(ctx.chars().count(), 9_920);
    for (got, want) in sent.iter().zip(&words) {
        assert_eq!(got, want);
    }
}

/// Exactly 10,000 characters is allowed (the cap is inclusive).
#[test]
fn soniox_context_of_exactly_10000_chars_is_kept() {
    // 125 terms of 78 chars: 125*78 + 124*2 = 9,750 + 248 = 9,998. Swap the last
    // for an 80-char term: 9,998 + 2 = 10,000.
    let mut words: Vec<String> = (0..124).map(|i| format!("{:y>78}", i)).collect();
    words.push(format!("{:z>80}", 124));
    words.push("over".into());
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: words,
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let ctx = soniox_context(&p).unwrap();
    assert_eq!(ctx.chars().count(), 10_000);
    assert!(!ctx.ends_with("over"));
}

/// Non-ASCII terms are counted in characters, not bytes: 4-byte emoji terms that
/// fit by character count are all kept.
#[test]
fn soniox_counts_characters_not_bytes() {
    let words: Vec<String> = (0..150)
        .map(|i| format!("{}{i:03}", "😀".repeat(40)))
        .collect();
    let p = TranscribeParams {
        api_key: "sx-test".into(),
        vocabulary: words,
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let ctx = soniox_context(&p).unwrap();
    // 43 chars per term: 43n + 2(n-1) <= 10,000  =>  n <= 222, so all 150 fit;
    // in bytes (163 per term) they would not.
    assert_eq!(ctx.split(", ").count(), 150);
    assert!(ctx.len() > 10_000);
}

/// A five-word term is kept, a six-word term is dropped and does not use a slot.
#[test]
fn elevenlabs_five_words_kept_six_dropped_without_a_slot() {
    let mut words = vec![
        "one two three four five".to_string(),
        "a b c d e f".to_string(),
    ];
    words.extend((0..120).map(|i| format!("t{i}")));
    let p = TranscribeParams {
        api_key: "el-test".into(),
        model: "scribe_v2".into(),
        vocabulary: words,
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    let terms = elevenlabs_keyterms(&p);
    assert_eq!(terms.len(), 100);
    assert_eq!(terms[0], "one two three four five");
    assert!(!terms.iter().any(|t| t == "a b c d e f"));
    assert_eq!(terms[99], "t98");
}

/// Scribe v2 Medical takes keyterms too and gets the same word cap.
#[test]
fn elevenlabs_medical_drops_keyterms_over_five_words() {
    let p = TranscribeParams {
        api_key: "el-test".into(),
        model: "scribe_v2_medical".into(),
        vocabulary: vec![
            "atrial fibrillation".into(),
            "one two three four five six".into(),
        ],
        audio_path: "/tmp/a.wav".into(),
        ..Default::default()
    };
    assert_eq!(
        elevenlabs_keyterms(&p),
        vec!["atrial fibrillation".to_string()]
    );
}
