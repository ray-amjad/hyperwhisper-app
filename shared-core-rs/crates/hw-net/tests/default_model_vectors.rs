//! Conformance-vector tests for `(cloud provider -> default transcription model id)`.
//!
//! `shared-conformance/default-model-vectors.json` is the cross-platform source
//! of truth for the model a BYOK request carries when the mode's model column is
//! blank. Three other heads replay the same file through their OWN resolver:
//!
//! - `app/shared-dotnet/HyperWhisper.TranscriptionRouting.Tests` →
//!   `ModeAwareTranscriptionRouter.DefaultCloudModelId`
//! - `app/windows/HyperWhisper.SmokeTests` → `CloudTranscriptionModels.GetDefault`
//! - `app/macos/hyperwhisperTests/DefaultModelConformanceVectorTests.swift` →
//!   `CloudTranscriptionModels.defaultModel(for:)`
//!
//! Issue #580: before this file the four heads kept four tables and nothing
//! compared them, so OpenAI resolved to `gpt-4o-transcribe` on the portable head
//! and `whisper-1` on the other three. The #566 test could not have caught it —
//! its guard and its assertion called the same function.
//!
//! This module deliberately asserts each provider module's `default_model()`
//! against the LITERAL in the vector file, never against
//! `hw_catalog`'s answer. Reading the catalog on both sides would restore the
//! tautology.
//!
//! Regenerate after an intended catalog change:
//!
//! ```sh
//! cd shared-core-rs
//! cargo test -p hw-net --test default_model_vectors -- --ignored regenerate
//! ```
//!
//! Then read the diff. A row that moves without a matching
//! `shared-app-classification/cloud-stt-catalog.json` edit is a regression, not
//! a refresh.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use hw_net::providers::{
    assemblyai, deepgram, elevenlabs, gemini, gemini_transcribe, groq, meta, mistral, openai,
    soniox,
};

const VECTORS_PATH: &str = "../../../shared-conformance/default-model-vectors.json";

fn vectors_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH)
}

#[derive(Serialize, Deserialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct Document {
    description: String,
    not_in_catalog: String,
    /// Why `creditsPerMinute` travels with the id: these entries double as
    /// HyperWhisper Cloud accuracy tiers, so a default flip is a billing change
    /// as well as a capability one.
    cloud_tier_note: String,
    providers: Vec<ProviderVector>,
}

#[derive(Serialize, Deserialize, PartialEq, Debug, Clone)]
#[serde(rename_all = "camelCase")]
struct ProviderVector {
    catalog_entry_id: String,
    provider_identifier: String,
    default_model_id: String,
    credits_per_minute: f64,
    /// Whether `hw-net` builds this provider's own request body with a `model`
    /// field, and therefore resolves a default of its own. False for Grok (the
    /// endpoint takes no `model` parameter) and for Azure MAI (proxy-routed; the
    /// caller supplies `routed_model`). Those rows are still pinned because the
    /// .NET and Swift heads DO answer them.
    byok_request_builder: bool,
}

fn load() -> Document {
    let json = std::fs::read_to_string(vectors_path()).expect("vectors file must exist");
    serde_json::from_str(&json).expect("vectors file must parse")
}

/// The `hw-net` request builders that resolve a default of their own, keyed by
/// the catalog entry they read. Written out one by one on purpose: a provider
/// added to the catalog without a builder entry fails
/// [`every_byok_builder_row_has_a_rust_resolver`] rather than being skipped.
fn rust_resolvers() -> Vec<(&'static str, &'static str)> {
    vec![
        (assemblyai::CATALOG_ENTRY_ID, assemblyai::default_model()),
        (deepgram::CATALOG_ENTRY_ID, deepgram::default_model()),
        (elevenlabs::CATALOG_ENTRY_ID, elevenlabs::default_model()),
        (gemini::CATALOG_ENTRY_ID, gemini::default_model()),
        (
            gemini_transcribe::CATALOG_ENTRY_ID,
            gemini_transcribe::default_model(),
        ),
        (groq::CATALOG_ENTRY_ID, groq::default_model()),
        (meta::CATALOG_ENTRY_ID, meta::default_model()),
        (mistral::CATALOG_ENTRY_ID, mistral::default_model()),
        (openai::CATALOG_ENTRY_ID, openai::default_model()),
        (soniox::CATALOG_ENTRY_ID, soniox::default_model()),
    ]
}

#[test]
fn rust_builders_match_the_vectors() {
    let expected = load();
    for (entry_id, resolved) in rust_resolvers() {
        let want = expected
            .providers
            .iter()
            .find(|p| p.catalog_entry_id == entry_id)
            .unwrap_or_else(|| panic!("no vector row for `{entry_id}`"));
        assert_eq!(
            resolved, want.default_model_id,
            "`{entry_id}` default model drift in hw-net"
        );
    }
}

#[test]
fn every_byok_builder_row_has_a_rust_resolver() {
    let expected = load();
    let resolvers = rust_resolvers();
    for want in &expected.providers {
        let has_resolver = resolvers
            .iter()
            .any(|(entry_id, _)| *entry_id == want.catalog_entry_id);
        assert_eq!(
            has_resolver, want.byok_request_builder,
            "`{}`: byokRequestBuilder disagrees with whether hw-net has a resolver",
            want.catalog_entry_id
        );
    }
    assert_eq!(
        resolvers.len(),
        expected
            .providers
            .iter()
            .filter(|p| p.byok_request_builder)
            .count(),
        "a hw-net resolver exists for a provider with no vector row"
    );
}

/// The vectors must describe the WHOLE catalog, not a subset somebody stopped
/// updating. A new provider entry is a new row here, on every head.
#[test]
fn vectors_cover_every_catalog_provider() {
    let expected = load();
    let catalog = hw_catalog::CloudSttCatalog::embedded().expect("catalog parses");
    let catalog_ids: Vec<&str> = catalog.providers().iter().map(|e| e.id.as_str()).collect();
    let vector_ids: Vec<&str> = expected
        .providers
        .iter()
        .map(|p| p.catalog_entry_id.as_str())
        .collect();
    assert_eq!(
        catalog_ids, vector_ids,
        "default-model vectors must list every catalog provider, in catalog order"
    );
}

/// Guards the vectors themselves: an empty file would make the comparisons
/// above pass vacuously.
#[test]
fn vectors_are_not_empty() {
    let expected = load();
    assert!(expected.providers.len() >= 12, "vectors look truncated");
    assert!(!expected.description.is_empty());
}

/// The catalog is what the heads read, so a row that no longer matches it is a
/// stale vector rather than a head bug. Kept separate from
/// [`rust_builders_match_the_vectors`], which is the non-tautological half.
#[test]
fn vectors_match_the_catalog() {
    let expected = load();
    let catalog = hw_catalog::CloudSttCatalog::embedded().expect("catalog parses");
    for want in &expected.providers {
        assert_eq!(
            catalog.default_model_id(&want.catalog_entry_id),
            Some(want.default_model_id.as_str()),
            "`{}` catalog default drifted from the vectors",
            want.catalog_entry_id
        );
        assert_eq!(
            catalog.credits_per_minute_for_model(&want.catalog_entry_id, &want.default_model_id),
            want.credits_per_minute,
            "`{}` credits/min drifted from the vectors",
            want.catalog_entry_id
        );
    }
}

#[test]
#[ignore = "regenerates shared-conformance/default-model-vectors.json"]
fn regenerate() {
    let existing = load();
    let catalog = hw_catalog::CloudSttCatalog::embedded().expect("catalog parses");
    let providers = catalog
        .providers()
        .iter()
        .map(|entry| {
            let previous = existing
                .providers
                .iter()
                .find(|p| p.catalog_entry_id == entry.id);
            let default_model_id = entry.default_model_id().unwrap_or("").to_string();
            ProviderVector {
                catalog_entry_id: entry.id.clone(),
                // Not derivable from the catalog: it is the spelling a Mode
                // persists in its `cloudProvider` column. A NEW provider must be
                // given one by hand.
                provider_identifier: previous
                    .map(|p| p.provider_identifier.clone())
                    .unwrap_or_else(|| {
                        panic!("add a providerIdentifier for the new entry `{}`", entry.id)
                    }),
                credits_per_minute: entry.credits_per_minute_for_model(&default_model_id),
                default_model_id,
                // Carried over, NOT recomputed from `rust_resolvers()`. Deriving
                // it here would make [`every_byok_builder_row_has_a_rust_resolver`]
                // compare `rust_resolvers()` with itself: delete a provider
                // module's resolver, regenerate, and the flag would flip to
                // false and the test would still pass, having silently agreed
                // that hw-net no longer sends a model for that vendor. A new
                // entry is given its flag by hand, like `providerIdentifier`.
                byok_request_builder: previous
                    .map(|p| p.byok_request_builder)
                    .unwrap_or_else(|| {
                        panic!("add a byokRequestBuilder for the new entry `{}`", entry.id)
                    }),
            }
        })
        .collect();
    let doc = Document {
        description: existing.description,
        not_in_catalog: existing.not_in_catalog,
        cloud_tier_note: existing.cloud_tier_note,
        providers,
    };
    let mut json = serde_json::to_string_pretty(&doc).expect("vectors must serialize");
    json.push('\n');
    std::fs::write(vectors_path(), json).expect("vectors must be writable");
    eprintln!("wrote {}", vectors_path().display());
}
