//! Conformance-vector tests for the canonical first-run mode seed (#285).
//!
//! `shared-conformance/mode-seed-vectors.json` is the cross-platform source of
//! truth for the ONE mode a brand-new install creates. Swift and C# run the same
//! vectors through their own UniFFI bindings:
//!
//! - `app/macos/hyperwhisperTests/ModeSeedConformanceVectorTests.swift`
//! - `app/shared-dotnet/HyperWhisper.ModeDefaults.Tests/Program.cs`
//!
//! Before #285 each head defined its own seed and they had already diverged in
//! four ways at once: macOS created ONE mode (`"Default"`, `language "en"`,
//! `postProcessingProvider "hyperwhisper"`, a hardcoded `claudeHaiku`) while
//! Windows and Linux/portable created SIX (`"Hyper"` plus five others, with the
//! post-processing model hardcoded to `"anthropic:claude-haiku-4-5"` and a
//! hand-rolled `JsonDocument` catalog parse). There is now ONE definition, so
//! these vectors pin its answer and every head proves it reads that same answer
//! across the FFI boundary.
//!
//! The region set is not arbitrary: `GB, AU, NF, CA, US, JP, ZZ, "", "  ",
//! " gb ", null` is exactly the set
//! `app/shared-dotnet/HyperWhisper.ModeDefaults.Tests/Program.cs` already uses
//! for the region table (item 1 of this same issue), so the three runners line
//! up row for row. It covers each spelling variant, an unknown region, a
//! non-English region, empty, whitespace-only, mixed-case-with-padding, and the
//! nil case every host can hand us.
//!
//! Regenerate after an intended catalog or seed change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-core --test mode_seed_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. A field that changes without a matching
//! `shared-app-classification/` catalog edit or a deliberate seed edit is a
//! regression, not a refresh — and unlike the catalog vectors, a wrong value
//! here ships to every user who installs the app for the first time.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

// The crate's `[lib] name` is `hyperwhisper_core` (it drives the artifact
// name), so that — not `hw_core` — is how an integration test imports it.
use hyperwhisper_core::ffi_catalog::{mode_seed_default, ModeSeed};

const VECTORS_PATH: &str = "../../../shared-conformance/mode-seed-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

/// The regions each runner must exercise, in order. `None` is the nil region
/// every host can produce (`Locale.current.region?.identifier` returning nil,
/// `RegionInfo.CurrentRegion` throwing).
const REGIONS: &[Option<&str>] = &[
    Some("GB"),
    Some("AU"),
    Some("NF"),
    Some("CA"),
    Some("US"),
    Some("JP"),
    Some("ZZ"),
    Some(""),
    Some("  "),
    Some(" gb "),
    None,
];

// ---------------------------------------------------------------------------
// Vector shapes. Kept flat and explicit so the JSON reads as data a human can
// review in a pull request, not as a serde dump of the FFI record.
// ---------------------------------------------------------------------------

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    seeds: Vec<SeedVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct SeedVector {
    /// The ISO 3166-1 alpha-2 region passed in; `null` is the nil case.
    region: Option<String>,
    id: String,
    name: String,
    preset: String,
    language: String,
    /// macOS Core Data `mode.model`; C# `Mode.ProviderType`. NOT the C# `Model`
    /// column, which stays null.
    provider_type: String,
    cloud_provider: String,
    cloud_accuracy_tier: String,
    cloud_transcription_model: String,
    /// `i32` here; macOS narrows it to `Int16`.
    post_processing_mode: i32,
    post_processing_provider: String,
    /// `"<engineId>:<modelId>"` — the prefix is load-bearing.
    cloud_post_processing_model: String,
    english_spelling: String,
    punctuation: bool,
    capitalization: bool,
    profanity_filter: bool,
    custom_instructions: String,
    is_default: bool,
    is_system_provided: bool,
    sort_order: i32,
}

impl SeedVector {
    fn new(region: Option<&str>, seed: ModeSeed) -> Self {
        SeedVector {
            region: region.map(str::to_string),
            id: seed.id,
            name: seed.name,
            preset: seed.preset,
            language: seed.language,
            provider_type: seed.provider_type,
            cloud_provider: seed.cloud_provider,
            cloud_accuracy_tier: seed.cloud_accuracy_tier,
            cloud_transcription_model: seed.cloud_transcription_model,
            post_processing_mode: seed.post_processing_mode,
            post_processing_provider: seed.post_processing_provider,
            cloud_post_processing_model: seed.cloud_post_processing_model,
            english_spelling: seed.english_spelling,
            punctuation: seed.punctuation,
            capitalization: seed.capitalization,
            profanity_filter: seed.profanity_filter,
            custom_instructions: seed.custom_instructions,
            is_default: seed.is_default,
            is_system_provided: seed.is_system_provided,
            sort_order: seed.sort_order,
        }
    }
}

fn build_document() -> Document {
    Document {
        description: "The canonical first-run mode seed (#285). ONE mode per fresh install, on \
                      every platform, from hw-catalog's mode_seed via the UniFFI \
                      mode_seed_default(region) export. Only englishSpelling varies by region; \
                      every other field is identical in every row, which is the point. \
                      Regenerate with: cargo test -p hw-core --test mode_seed_vectors -- \
                      --ignored regenerate"
            .to_string(),
        seeds: REGIONS
            .iter()
            .map(|region| SeedVector::new(*region, mode_seed_default(region.map(str::to_string))))
            .collect(),
    }
}

#[test]
fn conformance_vectors_match_the_shared_core() {
    let raw = std::fs::read_to_string(vectors_path()).expect("mode-seed-vectors.json must exist");
    let expected: Document = serde_json::from_str(&raw).expect("mode-seed-vectors.json must parse");
    let actual = build_document();

    assert_eq!(
        actual.seeds.len(),
        expected.seeds.len(),
        "the vector region set changed — regenerate the vectors if that was intended, and \
         update the Swift and C# runners to match"
    );
    // Compare row by row so a failure names the region that drifted rather than
    // dumping the whole document.
    for (a, e) in actual.seeds.iter().zip(&expected.seeds) {
        assert_eq!(a, e, "mode-seed drift for region {:?}", e.region);
    }
}

/// Guards the vectors themselves. Every assertion below would still hold if the
/// comparison above passed vacuously against a truncated file, so these are the
/// properties that make the vectors *worth* comparing.
#[test]
fn vectors_cover_the_agreed_contract() {
    let raw = std::fs::read_to_string(vectors_path()).expect("mode-seed-vectors.json must exist");
    let doc: Document = serde_json::from_str(&raw).expect("mode-seed-vectors.json must parse");

    assert_eq!(
        doc.seeds.len(),
        REGIONS.len(),
        "expected every pinned region"
    );

    // The nil region must be exercised: it is the one every host actually hits
    // when the OS has no region set, and the one a naive port forgets.
    assert!(
        doc.seeds.iter().any(|s| s.region.is_none()),
        "no row exercises the nil region"
    );
    // All four spelling variants must appear, or the region table is untested.
    for variant in ["american", "british", "australian", "canadian"] {
        assert!(
            doc.seeds.iter().any(|s| s.english_spelling == variant),
            "no row seeds `{variant}` spelling"
        );
    }
    // `""` means "emit no spelling instruction at all" and must never be seeded.
    assert!(
        doc.seeds.iter().all(|s| !s.english_spelling.is_empty()),
        "a row seeds the empty spelling token"
    );

    // englishSpelling is the ONLY field the region may move. If a future edit
    // makes another field region-dependent, that is a product decision and this
    // is where it gets noticed.
    let first = doc.seeds.first().expect("at least one row");
    for seed in &doc.seeds {
        assert_eq!(seed.id, first.id, "the mode id must not vary by region");
        assert_eq!(
            seed.name, first.name,
            "the mode name must not vary by region"
        );
        assert_eq!(seed.preset, first.preset);
        assert_eq!(seed.language, first.language);
        assert_eq!(seed.provider_type, first.provider_type);
        assert_eq!(seed.cloud_provider, first.cloud_provider);
        assert_eq!(seed.cloud_accuracy_tier, first.cloud_accuracy_tier);
        assert_eq!(
            seed.cloud_transcription_model,
            first.cloud_transcription_model
        );
        assert_eq!(seed.post_processing_mode, first.post_processing_mode);
        assert_eq!(
            seed.post_processing_provider,
            first.post_processing_provider
        );
        assert_eq!(
            seed.cloud_post_processing_model,
            first.cloud_post_processing_model
        );
        assert_eq!(seed.punctuation, first.punctuation);
        assert_eq!(seed.capitalization, first.capitalization);
        assert_eq!(seed.profanity_filter, first.profanity_filter);
        assert_eq!(seed.custom_instructions, first.custom_instructions);
        assert_eq!(seed.is_default, first.is_default);
        assert_eq!(seed.is_system_provided, first.is_system_provided);
        assert_eq!(seed.sort_order, first.sort_order);
    }

    // The values the product decision actually settled. Pinned here as literals
    // so a head reading these vectors cannot be "conformant" with a seed that
    // quietly stopped being the agreed one.
    assert_eq!(first.id, "00000000-0000-0000-0000-000000000001");
    assert_eq!(first.name, "Hyper");
    assert_eq!(first.language, "auto");
    assert_eq!(first.post_processing_provider, "hyperwhispercloud");
    assert_eq!(first.provider_type, "cloud");
    assert!(first.is_default);
    // The `<engineId>:<modelId>` form macOS' parser needs to avoid falling back
    // to Grok.
    assert!(
        first
            .cloud_post_processing_model
            .split_once(':')
            .is_some_and(|(engine, model)| !engine.is_empty() && !model.is_empty()),
        "the seeded post-processing model lost its `<engineId>:<modelId>` form"
    );
}

/// Writes the vectors from the current shared-core answer. Ignored by default;
/// run it deliberately after an intended catalog or seed change, then read the
/// diff.
#[test]
#[ignore = "regenerates shared-conformance/mode-seed-vectors.json"]
fn regenerate() {
    let doc = build_document();
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}
