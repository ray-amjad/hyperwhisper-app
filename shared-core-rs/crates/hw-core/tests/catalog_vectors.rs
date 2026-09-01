//! Conformance-vector tests for the shared catalog decoders.
//!
//! `shared-conformance/catalog-vectors.json` is the cross-platform source of
//! truth for three things issue #280 moved into `hw-catalog`: the polymorphic
//! catalog decoding, the Provider-dropdown vendor grouping, and the
//! picker-language folding. Swift and C# run the same vectors through their own
//! UniFFI bindings:
//!
//! - `app/macos/hyperwhisperTests/CatalogConformanceVectorTests.swift`
//! - `app/shared-dotnet/HyperWhisper.CatalogConformance.Tests/Program.cs`
//!
//! Before #280 each stack decoded the catalog JSON itself, and the copies had
//! already drifted (`deepgramNova3` offered `gu`/`th`/`zh` on Windows and not on
//! macOS). There is now ONE decoder, so these vectors pin its answer and every
//! stack proves it reads that same answer across the FFI boundary.
//!
//! Regenerate after an intended catalog or decoder change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test catalog_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. A field that changes without a matching
//! `shared-app-classification/cloud-stt-catalog.json` edit is a decoder
//! regression, not a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

// The crate's `[lib] name` is `hyperwhisper_core` (it drives the artifact
// name), so that — not `hw_core` — is how an integration test imports it.
use hyperwhisper_core::ffi_catalog::{
    cloud_stt_cloud_tier_vendor_groups, cloud_stt_default_model_id, cloud_stt_entries,
    cloud_stt_language_codes, cloud_stt_picker_language_codes, models_all_entries,
    models_language_support, HwKind, SttEntry, VocabSupport,
};

const VECTORS_PATH: &str = "../../../shared-conformance/catalog-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

// ---------------------------------------------------------------------------
// Vector shapes. Kept flat and explicit so the JSON reads as data a human can
// review in a pull request, not as a serde dump of the FFI records.
// ---------------------------------------------------------------------------

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    cloud_stt_entries: Vec<EntryVector>,
    vendor_groups: Vec<VendorGroupVector>,
    picker_language_codes: Vec<PickerVector>,
    models_entries: Vec<ModelsEntryVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct EntryVector {
    id: String,
    display_name: String,
    display_model: Option<String>,
    vendor: String,
    vendor_display_name: Option<String>,
    vendor_label: String,
    stt_provider: Option<String>,
    cloud_tier_eligible: Option<bool>,
    byok_eligible: Option<bool>,
    cloud_tier_accuracy: Option<String>,
    cloud_tier_credits_per_minute: Option<f64>,
    word_timestamps: bool,
    diarization: bool,
    streaming: bool,
    code_switching: bool,
    endpointing: bool,
    context_bias: bool,
    language_bias: bool,
    turn_timestamps: bool,
    /// `"yes"` / `"no"` / `"unverified"` / `null` when the block is absent —
    /// the tri-state the bool-or-string field decodes to.
    custom_vocabulary_supported: Option<String>,
    custom_vocabulary_field_name: Option<String>,
    /// `null` where the catalog says `"unverified"`.
    languages_count: Option<i64>,
    languages_auto_detect: Option<bool>,
    languages_code_format: Option<String>,
    /// `false` where `languages.codes` is the `"unverified"` literal.
    languages_has_codes: bool,
    /// Length of the RAW upstream code list. The codes themselves are not
    /// pinned here (736 strings across the catalog); the fold below is.
    languages_raw_code_count: usize,
    max_file_size_mb: Option<f64>,
    max_duration_minutes: Option<i64>,
    accepted_formats: Vec<String>,
    preview_status: Option<bool>,
    migrate_from: Vec<String>,
    legacy_cloud_provider_aliases: Vec<String>,
    /// `null` only when the provider lists no models; `""` is a real answer
    /// (Grok's single implicit model).
    default_model_id: Option<String>,
    models: Vec<ModelVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct ModelVector {
    id: String,
    display_name: String,
    credits_per_minute: Option<f64>,
    is_default: Option<bool>,
    preview_status: Option<bool>,
    supports_custom_vocabulary: Option<bool>,
    /// Catalog v8. Gates the HyperWhisper-Cloud live picker, and the catalog has
    /// no `enabled` gate to hide a wrong value behind, so it is pinned here for
    /// all three stacks rather than only in the Rust unit tests.
    streaming: Option<bool>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct VendorGroupVector {
    vendor_key: String,
    display_name: String,
    /// The group's entries in catalog order. Order is load-bearing: the first
    /// is the tier a fresh Provider selection lands on.
    entry_ids: Vec<String>,
    /// Every model in the group as `"<owning entry id>/<model id>"`. The owning
    /// entry is what becomes the `X-STT-Provider` header, so a merged company
    /// row (Google) still routes each model correctly.
    models: Vec<String>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct PickerVector {
    id: String,
    /// `null` when the catalog leaves the language set `"unverified"`, so the
    /// caller keeps its full picker list.
    codes: Option<Vec<String>>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct ModelsEntryVector {
    provider: String,
    id: String,
    kind: String,
    supports_custom_vocabulary: bool,
    available_via_hyper_whisper_cloud: bool,
    /// Resolved through `models_language_support`, so this pins the wildcard
    /// fallback and the "uncatalogued ⇒ every language" rule too.
    supports_all_languages: bool,
    language_codes: Vec<String>,
    voice_capabilities: Option<VoiceCapabilitiesVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct VoiceCapabilitiesVector {
    code_switching: bool,
    endpointing: bool,
    context_bias: bool,
    language_bias: bool,
    turn_timestamps: bool,
    diarization: bool,
    word_timestamps: bool,
}

// ---------------------------------------------------------------------------
// Building the vectors from the shared core
// ---------------------------------------------------------------------------

fn vocab_support_name(v: &VocabSupport) -> &'static str {
    match v {
        VocabSupport::Yes => "yes",
        VocabSupport::No => "no",
        VocabSupport::Unverified => "unverified",
    }
}

fn entry_vector(e: &SttEntry) -> EntryVector {
    EntryVector {
        id: e.id.clone(),
        display_name: e.display_name.clone(),
        display_model: e.display_model.clone(),
        vendor: e.vendor.clone(),
        vendor_display_name: e.vendor_display_name.clone(),
        vendor_label: e.vendor_label.clone(),
        stt_provider: e.stt_provider.clone(),
        cloud_tier_eligible: e.access.as_ref().map(|a| a.cloud_tier_eligible),
        byok_eligible: e.access.as_ref().map(|a| a.byok_eligible),
        cloud_tier_accuracy: e.cloud_tier.as_ref().map(|t| t.accuracy.clone()),
        cloud_tier_credits_per_minute: e.cloud_tier.as_ref().map(|t| t.credits_per_minute),
        word_timestamps: e.features.word_timestamps,
        diarization: e.features.diarization,
        streaming: e.features.streaming,
        code_switching: e.features.code_switching,
        endpointing: e.features.endpointing,
        context_bias: e.features.context_bias,
        language_bias: e.features.language_bias,
        turn_timestamps: e.features.turn_timestamps,
        custom_vocabulary_supported: e
            .custom_vocabulary
            .as_ref()
            .map(|cv| vocab_support_name(&cv.supported).to_string()),
        custom_vocabulary_field_name: e
            .custom_vocabulary
            .as_ref()
            .and_then(|cv| cv.field_name.clone()),
        languages_count: e.languages.count,
        languages_auto_detect: e.languages.auto_detect,
        languages_code_format: e.languages.code_format.clone(),
        languages_has_codes: e.languages.has_codes,
        languages_raw_code_count: cloud_stt_language_codes(e.id.clone())
            .map(|c| c.len())
            .unwrap_or(0),
        max_file_size_mb: e.max_file_size_mb,
        max_duration_minutes: e.max_duration_minutes,
        accepted_formats: e.accepted_formats.clone(),
        preview_status: e.preview_status,
        migrate_from: e.migrate_from.clone(),
        legacy_cloud_provider_aliases: e.legacy_cloud_provider_aliases.clone(),
        default_model_id: cloud_stt_default_model_id(e.id.clone()),
        models: e
            .models
            .iter()
            .map(|m| ModelVector {
                id: m.id.clone(),
                display_name: m.display_name.clone(),
                credits_per_minute: m.credits_per_minute,
                is_default: m.is_default,
                preview_status: m.preview_status,
                supports_custom_vocabulary: m.supports_custom_vocabulary,
                streaming: m.streaming,
            })
            .collect(),
    }
}

fn build_document() -> Document {
    let entries = cloud_stt_entries();
    Document {
        description: concat!(
            "Cross-platform conformance vectors for the shared catalog decoders. ",
            "Source of truth: shared-core-rs/crates/hw-catalog/src/cloud_stt.rs and models.rs, ",
            "read through shared-core-rs/crates/hw-core/src/ffi_catalog.rs. Three stacks run these ",
            "vectors through their own UniFFI binding: Rust ",
            "(shared-core-rs/crates/hw-core/tests/catalog_vectors.rs), Swift ",
            "(app/macos/hyperwhisperTests/CatalogConformanceVectorTests.swift) and C# ",
            "(app/shared-dotnet/HyperWhisper.CatalogConformance.Tests/Program.cs). ",
            "Regenerate with: cargo test -p hw-core --test catalog_vectors -- --ignored regenerate. ",
            "`languagesRawCodeCount` pins the size of the raw upstream code list rather than its ",
            "contents; `pickerLanguageCodes` pins the fold applied to it. A null `codes` means the ",
            "catalog declares the set \"unverified\", so a picker keeps its full list."
        )
        .to_string(),
        picker_language_codes: entries
            .iter()
            .map(|e| PickerVector {
                id: e.id.clone(),
                codes: cloud_stt_picker_language_codes(e.id.clone()),
            })
            .collect(),
        vendor_groups: cloud_stt_cloud_tier_vendor_groups()
            .iter()
            .map(|g| VendorGroupVector {
                vendor_key: g.vendor_key.clone(),
                display_name: g.display_name.clone(),
                entry_ids: g.entries.iter().map(|e| e.id.clone()).collect(),
                models: g
                    .entries
                    .iter()
                    .flat_map(|e| e.models.iter().map(move |m| format!("{}/{}", e.id, m.id)))
                    .collect(),
            })
            .collect(),
        models_entries: models_all_entries()
            .iter()
            .map(|e| {
                let kind = if e.kind == "text" { HwKind::Text } else { HwKind::Voice };
                let support = models_language_support(
                    e.provider.clone(),
                    kind,
                    e.id.clone(),
                );
                ModelsEntryVector {
                    provider: e.provider.clone(),
                    id: e.id.clone(),
                    kind: e.kind.clone(),
                    supports_custom_vocabulary: e.supports_custom_vocabulary,
                    available_via_hyper_whisper_cloud: e.available_via_hyper_whisper_cloud,
                    supports_all_languages: support.supports_all,
                    language_codes: support.codes,
                    voice_capabilities: e.voice_capabilities.as_ref().map(|c| VoiceCapabilitiesVector {
                        code_switching: c.code_switching,
                        endpointing: c.endpointing,
                        context_bias: c.context_bias,
                        language_bias: c.language_bias,
                        turn_timestamps: c.turn_timestamps,
                        diarization: c.diarization,
                        word_timestamps: c.word_timestamps,
                    }),
                }
            })
            .collect(),
        cloud_stt_entries: entries.iter().map(entry_vector).collect(),
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[test]
fn conformance_vectors_match_the_shared_core() {
    let raw = std::fs::read_to_string(vectors_path()).expect("catalog-vectors.json must exist");
    let expected: Document = serde_json::from_str(&raw).expect("catalog-vectors.json must parse");
    let actual = build_document();

    // Compare set by set so a failure names the domain that drifted rather than
    // dumping the whole document.
    assert_eq!(
        actual.cloud_stt_entries.len(),
        expected.cloud_stt_entries.len(),
        "cloud-STT provider count changed — regenerate the vectors if that was intended"
    );
    for (a, e) in actual.cloud_stt_entries.iter().zip(&expected.cloud_stt_entries) {
        assert_eq!(a, e, "cloudSttEntries drift on `{}`", e.id);
    }
    assert_eq!(
        actual.vendor_groups, expected.vendor_groups,
        "vendor grouping drift (order is load-bearing: it is the dropdown's order)"
    );
    for (a, e) in actual
        .picker_language_codes
        .iter()
        .zip(&expected.picker_language_codes)
    {
        assert_eq!(a, e, "picker-language fold drift on `{}`", e.id);
    }
    assert_eq!(
        actual.picker_language_codes.len(),
        expected.picker_language_codes.len()
    );
    assert_eq!(actual.models_entries, expected.models_entries, "models-catalog drift");
}

/// Guards the vectors themselves: an empty or truncated file would make the
/// comparison above pass vacuously if it ever grew a length-tolerant path.
#[test]
fn vectors_cover_every_domain() {
    let raw = std::fs::read_to_string(vectors_path()).expect("catalog-vectors.json must exist");
    let doc: Document = serde_json::from_str(&raw).expect("catalog-vectors.json must parse");
    assert!(doc.cloud_stt_entries.len() >= 10, "expected the full provider list");
    assert!(doc.vendor_groups.len() >= 9, "expected the full Provider dropdown");
    assert!(doc.models_entries.len() > 20, "expected the full models catalog");
    // At least one provider must exercise each half of every polymorphic field,
    // or the vectors would not prove the decoding at all.
    assert!(
        doc.cloud_stt_entries.iter().any(|e| !e.languages_has_codes),
        "no provider exercises the `\"unverified\"` languages.codes branch"
    );
    assert!(
        doc.cloud_stt_entries.iter().any(|e| e.languages_has_codes),
        "no provider exercises the enumerated languages.codes branch"
    );
    assert!(
        doc.cloud_stt_entries.iter().any(|e| e.max_file_size_mb.is_none()),
        "no provider exercises the `\"unverified\"` maxFileSizeMb branch"
    );
    assert!(
        doc.cloud_stt_entries.iter().any(|e| e.max_duration_minutes.is_some()),
        "no provider exercises the numeric maxDurationMinutes branch"
    );
    assert!(
        doc.cloud_stt_entries.iter().any(|e| e.languages_count.is_none()),
        "no provider exercises the `\"unverified\"` languages.count branch"
    );
    assert!(
        doc.picker_language_codes.iter().any(|p| p.codes.is_none()),
        "no provider exercises the null picker-language fold"
    );
    // The vendor grouping is only meaningful if at least one company owns more
    // than one tier — that merge is the whole point of the fold.
    assert!(
        doc.vendor_groups.iter().any(|g| g.entry_ids.len() > 1),
        "no company owns two tiers, so the vendor merge is untested"
    );
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended catalog or decoder change, then read
/// the diff.
#[test]
#[ignore = "regenerates shared-conformance/catalog-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}
