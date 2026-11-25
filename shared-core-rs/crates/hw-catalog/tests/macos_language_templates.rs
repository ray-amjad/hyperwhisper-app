//! Every code in a macOS `STTLanguageTemplates.codes([...])` list must be a
//! row of this crate's language table.
//!
//! `STTLanguageTemplates.codes` resolves its list through
//! `LanguageData.languages(for:)`, which calls `assertionFailure` on a code the
//! core does not know. Those lists are built once, inside the lazy
//! `STTCapabilities.registry`, so ONE unknown code crashes the macOS test host
//! at the first capability lookup and every later test dies with it (PR #3121:
//! Odia `or` in the Voxtral Small Transcribe 3 list). No Xcode runs on Linux,
//! so this pins the same rule from here.

use std::path::Path;

const STT_CAPABILITIES: &str = "../../../app/macos/hyperwhisper/Models/STTCapabilities.swift";

/// The quoted strings inside each `codes([` ... `]` block, `//` comments removed.
fn template_codes(source: &str) -> Vec<(usize, String)> {
    let mut found = Vec::new();
    let mut rest = source;
    let mut offset = 0;
    while let Some(start) = rest.find("codes([") {
        let body_start = start + "codes([".len();
        let Some(len) = rest[body_start..].find(']') else {
            break;
        };
        let body = &rest[body_start..body_start + len];
        let line = source[..offset + start].lines().count() + 1;
        for raw in body.lines() {
            let code_part = raw.split("//").next().unwrap_or("");
            let mut parts = code_part.split('"');
            parts.next();
            while let (Some(code), Some(_)) = (parts.next(), parts.next()) {
                found.push((line, code.to_string()));
            }
        }
        offset += body_start + len;
        rest = &source[offset..];
    }
    found
}

#[test]
fn every_macos_template_code_is_in_the_language_table() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join(STT_CAPABILITIES);
    let source =
        std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let codes = template_codes(&source);
    assert!(
        codes.len() > 100,
        "found only {} codes - the extractor no longer matches the file",
        codes.len()
    );

    let unknown: Vec<String> = codes
        .iter()
        .filter(|(_, code)| hw_catalog::info(code).is_none())
        .map(|(line, code)| format!("{code} (block at line {line})"))
        .collect();
    assert!(
        unknown.is_empty(),
        "STTCapabilities.swift lists codes the core language table does not know, which \
         trips LanguageData's assertionFailure and crashes the macOS test host: {unknown:?}"
    );
}
