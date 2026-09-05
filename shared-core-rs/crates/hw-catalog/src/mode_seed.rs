//! The canonical first-run mode seed (#285, final item).
//!
//! Every head — macOS, Windows, Linux/portable — creates the SAME single mode
//! on a brand-new install, and this module is the only place that says what
//! that mode is. Before this, macOS seeded one mode (`"Default"`, `language
//! "en"`, post-processing provider `"hyperwhisper"`, a hardcoded
//! `claudeHaiku`) and the two .NET heads seeded six (`"Hyper"`, `"Voice to
//! Text"`, `"Message"`, `"Mail"`, `"Note"`, `"Meeting"`) with a hand-rolled
//! `JsonDocument` catalog parse. Three implementations, three answers.
//!
//! Two shape decisions carry the guarantee:
//!
//! * [`mode_seed_default`] returns ONE [`ModeSeed`], not a `Vec`. "Exactly one
//!   mode" is then not something a head can get wrong — there is no count to
//!   disagree about.
//! * Every field is filled in. macOS Core Data attribute defaults are hostile
//!   (`language="en"`, `model="base"`, `cloudProvider="openai"`,
//!   `postProcessingProvider="openai"`, `cloudPostProcessingModel="claudeHaiku"`),
//!   so a field a head *stops* writing silently inherits a wrong value rather
//!   than an empty one. The record has no optional fields for that reason.
//!
//! Seeding runs only when the store holds no modes. Existing users on every
//! platform are untouched; that guard lives in each head and does not move.
//!
//! # Never panics, so "fail closed" is a BUILD-time guarantee
//!
//! The workspace release profile sets `panic = "abort"`, and this runs on the
//! app's first-launch path, so a panic here is the app failing to start rather
//! than a recoverable error. The two catalog-resolved fields therefore fall
//! back to literals ([`FALLBACK_CLOUD_TRANSCRIPTION_MODEL`],
//! [`FALLBACK_CLOUD_POST_PROCESSING_MODEL`]) instead of unwrapping.
//!
//! That has a consequence worth stating plainly, because the portable .NET
//! seeder this replaced could `throw new InvalidDataException` and this cannot:
//! **at runtime there is no way to refuse to seed.** Whatever happens, a mode
//! is written. So the fail-closed behaviour is bought in two places instead:
//!
//! 1. *At resolution.* [`resolve_cloud_post_processing_model`] only ever
//!    resolves an engine the picker would SHOW. `enabled: false` on an engine
//!    is the documented rollout gate, and seeding a fresh install onto an
//!    engine the user cannot see or change is the failure this prevents. If the
//!    seed's own engine is gated off, the first engine the picker does show is
//!    used rather than the gated one.
//! 2. *At build time.* The catalog is embedded at compile time, so the tests in
//!    this module are the real gate:
//!    `catalog_resolution_matches_the_fallback_literals` asserts the resolved
//!    values EQUAL the literals, and `the_fallback_literal_names_a_picker_engine`
//!    asserts the literal itself names an ENABLED engine. Gating `anthropic`
//!    off therefore fails CI rather than shipping a hidden engine to every new
//!    install.
//!
//! The literal fallback is only reachable when the embedded catalog does not
//! parse or offers no enabled engine at all — a state those tests cannot let
//! into a build. It exists so first launch cannot panic, not as a second
//! opinion about what to seed.

use std::sync::OnceLock;

use crate::{CloudPpCatalog, CloudSttCatalog, PpProvider};

/// The seeded mode's UUID. Identical on all three heads already
/// (`ModeDefaults.DefaultModeId`, `PortableModeDefaults.HyperModeId`, and what
/// macOS `initializeDefaultModes()` writes) and anchored by onboarding
/// restore-point lookups on every platform. Do not change it.
pub const HYPER_MODE_ID: &str = "00000000-0000-0000-0000-000000000001";

/// The seeded mode's name.
///
/// `"Hyper"` over macOS' `"Default"`: it is the only one of the two that is a
/// *product* name — it is documented to users, it is the Windows recording-overlay
/// label, and two of the three heads already ship it. macOS' `"Default"` appears
/// only as a nil-coalescing placeholder for an *unnamed* mode.
pub const HYPER_MODE_NAME: &str = "Hyper";

/// The seeded preset. All three heads already agree on this one.
pub const HYPER_MODE_PRESET: &str = "hyper";

/// The cloud-STT tier the seed transcribes with.
pub const CLOUD_ACCURACY_TIER: &str = "elevenLabsScribeV2";

/// The cloud post-processing *engine* whose default model the seed resolves.
pub const CLOUD_POST_PROCESSING_ENGINE: &str = "anthropic";

/// The canonical stored token for HyperWhisper Cloud post-processing.
///
/// Windows and Linux already fold `"hyperwhisper"` and `"hyperwhisper_cloud"`
/// onto this on write. macOS does not understand it yet — teaching macOS to
/// read all three spellings is a separate, strictly-ordered piece of work that
/// must land before macOS starts seeding this value.
pub const POST_PROCESSING_PROVIDER: &str = "hyperwhispercloud";

/// Used when `cloud-stt-catalog.json` cannot be parsed or names no default
/// model for [`CLOUD_ACCURACY_TIER`]. Pinned equal to the catalog answer by a
/// unit test in this module.
pub const FALLBACK_CLOUD_TRANSCRIPTION_MODEL: &str = "scribe_v2";

/// Used when `cloud-pp-catalog.json` cannot be parsed or names no default model
/// for [`CLOUD_POST_PROCESSING_ENGINE`]. Pinned equal to the catalog answer by a
/// unit test in this module.
///
/// Carries the `<engineId>:<modelId>` prefix deliberately — see
/// [`ModeSeed::cloud_post_processing_model`].
pub const FALLBACK_CLOUD_POST_PROCESSING_MODEL: &str = "anthropic:claude-haiku-4-5";

/// The single mode a brand-new install creates, on every platform.
///
/// Field names are the shared vocabulary, not any one head's column names. Two
/// mappings are NOT interchangeable and have bitten this codebase before:
///
/// * [`Self::provider_type`] is written to macOS Core Data `mode.model` and to
///   the C# entity's `ProviderType`. macOS has no `providerType` column; the C#
///   entity has BOTH `Model` (left null) and `ProviderType`. Neither substitutes
///   for the other.
/// * [`Self::post_processing_mode`] is an `i32` here; macOS narrows it to
///   `Int16` at the Core Data boundary.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModeSeed {
    /// [`HYPER_MODE_ID`].
    pub id: String,
    /// [`HYPER_MODE_NAME`].
    pub name: String,
    /// [`HYPER_MODE_PRESET`].
    pub preset: String,
    /// `"auto"` — automatic language detection. macOS previously seeded `"en"`.
    pub language: String,
    /// `"cloud"`. macOS Core Data `mode.model`; C# `Mode.ProviderType`.
    pub provider_type: String,
    /// `"hyperwhisper"` — the cloud transcription provider (NOT the
    /// post-processing provider, which is [`Self::post_processing_provider`]).
    pub cloud_provider: String,
    /// [`CLOUD_ACCURACY_TIER`].
    pub cloud_accuracy_tier: String,
    /// Resolved from `cloud-stt-catalog.json`, not hardcoded.
    pub cloud_transcription_model: String,
    /// `1`. macOS narrows this to `Int16`.
    pub post_processing_mode: i32,
    /// [`POST_PROCESSING_PROVIDER`], the canonical token.
    pub post_processing_provider: String,
    /// `"<engineId>:<modelId>"`, resolved from `cloud-pp-catalog.json`.
    ///
    /// **Keep the prefix.** All three heads store the two-part form, and macOS'
    /// parser (`ModeModels.swift` `fromStorageValue`) falls back to **Grok** —
    /// not Anthropic — on a value it cannot split, so a bare model id would
    /// silently seed the wrong vendor.
    pub cloud_post_processing_model: String,
    /// `"american"` / `"british"` / `"australian"` / `"canadian"`, from the
    /// host's region. Never the empty token: `""` means "emit no spelling
    /// instruction at all", which is never the right thing to seed.
    pub english_spelling: String,
    pub punctuation: bool,
    pub capitalization: bool,
    pub profanity_filter: bool,
    /// `""`. Written explicitly — see the module note on hostile defaults.
    pub custom_instructions: String,
    pub is_default: bool,
    pub is_system_provided: bool,
    pub sort_order: i32,
}

/// The mode to seed into an empty store, for a host region.
///
/// `region` is an ISO 3166-1 alpha-2 code and is `Option` so Rust owns the nil
/// case: every host reads a nullable region (`Locale.current.region?.identifier`,
/// `RegionInfo.CurrentRegion` behind a try/catch). `None`, empty and unknown all
/// seed American spelling — the same contract, and the same parameter shape, as
/// `english_spelling_for_region` (item 1 of this issue).
///
/// Returns ONE mode. See the module docs for why this is not a `Vec`.
pub fn mode_seed_default(region: Option<&str>) -> ModeSeed {
    ModeSeed {
        id: HYPER_MODE_ID.to_string(),
        name: HYPER_MODE_NAME.to_string(),
        preset: HYPER_MODE_PRESET.to_string(),
        language: "auto".to_string(),
        provider_type: "cloud".to_string(),
        cloud_provider: "hyperwhisper".to_string(),
        cloud_accuracy_tier: CLOUD_ACCURACY_TIER.to_string(),
        cloud_transcription_model: cloud_transcription_model().to_string(),
        post_processing_mode: 1,
        post_processing_provider: POST_PROCESSING_PROVIDER.to_string(),
        cloud_post_processing_model: cloud_post_processing_model().to_string(),
        english_spelling: hw_text::EnglishSpelling::for_region(region)
            .raw_value()
            .to_string(),
        punctuation: true,
        capitalization: true,
        profanity_filter: false,
        custom_instructions: String::new(),
        is_default: true,
        is_system_provided: true,
        sort_order: 0,
    }
}

/// The catalog's default model id for [`CLOUD_ACCURACY_TIER`], or the fallback.
///
/// An empty id is treated as unresolved: the cloud-STT catalog allows `""` as a
/// legitimate model id (Grok's single implicit model), but seeding it here would
/// send an empty `X-STT-Model`.
fn cloud_transcription_model() -> &'static str {
    static RESOLVED: OnceLock<String> = OnceLock::new();
    RESOLVED.get_or_init(|| {
        CloudSttCatalog::embedded()
            .ok()
            .and_then(|catalog| {
                catalog
                    .default_model_id(CLOUD_ACCURACY_TIER)
                    .filter(|id| !id.is_empty())
                    .map(str::to_string)
            })
            .unwrap_or_else(|| FALLBACK_CLOUD_TRANSCRIPTION_MODEL.to_string())
    })
}

/// One engine's default model as `"<engineId>:<modelId>"`, or `None` when it
/// names none.
///
/// The engine id comes from the catalog row rather than from the caller's
/// lookup string, because [`CloudPpCatalog::provider`] matches
/// case-insensitively and the persisted prefix has to be the catalog's own
/// spelling.
fn engine_default_model(provider: &PpProvider) -> Option<String> {
    provider
        .default_model()
        // An empty model id would persist `"anthropic:"`, which splits but
        // names nothing.
        .filter(|model| !model.id.is_empty())
        .map(|model| format!("{}:{}", provider.id, model.id))
}

/// The seeded `"<engineId>:<modelId>"`, resolved from a parsed catalog, or
/// `None` when the catalog can offer nothing a user could actually pick.
///
/// Fail-closed on the rollout gate. [`CloudPpCatalog::default_model`]
/// deliberately does NOT filter on [`PpProvider::is_enabled`] — it mirrors the
/// macOS/Windows `provider(byId:)` lookups, which resolve a disabled engine so a
/// user already ON it keeps working. That is right for a lookup and wrong here:
/// a fresh install has no stored engine to preserve, so resolving a gated-off
/// engine seeds every new user onto an engine `picker_providers()` hides, with
/// no way to change it. Setting `"enabled": false` on the seed's engine is the
/// documented way to pull it from the rollout, and it must pull it from the seed
/// too.
///
/// Taking a `&CloudPpCatalog` rather than reading the embedded one keeps this
/// testable against a catalog with the gate actually flipped — the property the
/// deleted .NET `AssertInvalidCatalogAsync` used to assert.
fn resolve_cloud_post_processing_model(catalog: &CloudPpCatalog) -> Option<String> {
    catalog
        .provider(CLOUD_POST_PROCESSING_ENGINE)
        .filter(|provider| provider.is_enabled())
        .and_then(engine_default_model)
        // The seed's engine is gated off. Seed an engine the picker DOES show
        // rather than a hidden one; `picker_providers()` is the same enabled,
        // catalog-ordered list the Engine dropdown is built from.
        .or_else(|| catalog.picker_providers().find_map(engine_default_model))
}

/// The catalog's default post-processing model for
/// [`CLOUD_POST_PROCESSING_ENGINE`] as `"<engineId>:<modelId>"`, or the fallback.
fn cloud_post_processing_model() -> &'static str {
    static RESOLVED: OnceLock<String> = OnceLock::new();
    RESOLVED.get_or_init(|| {
        CloudPpCatalog::embedded()
            .ok()
            .and_then(|catalog| resolve_cloud_post_processing_model(&catalog))
            .unwrap_or_else(|| FALLBACK_CLOUD_POST_PROCESSING_MODEL.to_string())
    })
}

// The panic-free lints exist to protect the FIRST-LAUNCH path. In a test a
// panic is the intended failure signal, so they are lifted here — same split as
// the `language` module.
#[cfg(test)]
#[allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)]
mod tests {
    use super::*;

    /// THE guard that makes the never-panic fallback safe.
    ///
    /// The runtime path cannot panic when the catalog stops naming a default,
    /// so nothing at runtime would tell us it happened. This test is where that
    /// is noticed: it fails on a catalog edit that moves either default, which
    /// forces a deliberate choice (update the literal, or revert the catalog)
    /// rather than a silent divergence between what ships and what the fallback
    /// says.
    ///
    /// Since `cloud_post_processing_model` resolves only through ENABLED
    /// engines, gating the seeded engine off moves the resolved value and so
    /// fails here too — but do not rely on this test alone for that:
    /// `a_gated_off_engine_is_never_seeded` is the one that exercises the gate
    /// directly, and `the_seeded_engine_is_one_the_picker_shows` is the one that
    /// names the real problem when it happens.
    #[test]
    fn catalog_resolution_matches_the_fallback_literals() {
        assert_eq!(
            cloud_transcription_model(),
            FALLBACK_CLOUD_TRANSCRIPTION_MODEL,
            "cloud-stt-catalog.json no longer resolves {CLOUD_ACCURACY_TIER} to \
             {FALLBACK_CLOUD_TRANSCRIPTION_MODEL}. If the catalog change was \
             intended, update FALLBACK_CLOUD_TRANSCRIPTION_MODEL to match; the \
             fallback exists so first launch cannot panic, not so it can ship a \
             different model than the catalog names."
        );
        assert_eq!(
            cloud_post_processing_model(),
            FALLBACK_CLOUD_POST_PROCESSING_MODEL,
            "cloud-pp-catalog.json no longer resolves {CLOUD_POST_PROCESSING_ENGINE} \
             to {FALLBACK_CLOUD_POST_PROCESSING_MODEL}. Same rule as above."
        );
    }

    /// Independent of the catalog: proves the resolution really went through the
    /// catalog rather than short-circuiting to the literal, by checking the
    /// catalog directly with a different call.
    #[test]
    fn the_resolved_values_come_from_the_catalog() {
        let stt = CloudSttCatalog::embedded().expect("cloud-stt-catalog.json must parse");
        assert_eq!(
            stt.default_model_id(CLOUD_ACCURACY_TIER),
            Some("scribe_v2"),
            "the seed's STT tier must exist in the catalog and name a default"
        );

        let pp = CloudPpCatalog::embedded().expect("cloud-pp-catalog.json must parse");
        let model = pp
            .default_model(CLOUD_POST_PROCESSING_ENGINE)
            .expect("the seed's PP engine must exist in the catalog and name a default");
        assert_eq!(model.id, "claude-haiku-4-5");
    }

    /// The build-time half of "fail closed" — see the module docs.
    ///
    /// Nothing at runtime can refuse to seed, so this is where a rollout gate on
    /// the seeded engine has to be noticed. `"enabled": false` on `anthropic`
    /// hides it from the Engine dropdown; seeding every fresh install onto an
    /// engine the picker hides is the failure, and it fails HERE instead.
    #[test]
    fn the_seeded_engine_is_one_the_picker_shows() {
        let pp = CloudPpCatalog::embedded().expect("cloud-pp-catalog.json must parse");
        assert!(
            pp.is_enabled(CLOUD_POST_PROCESSING_ENGINE),
            "cloud-pp-catalog.json gates {CLOUD_POST_PROCESSING_ENGINE} off \
             (`\"enabled\": false`), so a fresh install would be seeded onto an \
             engine the picker does not show. Either re-enable it, or move \
             CLOUD_POST_PROCESSING_ENGINE and FALLBACK_CLOUD_POST_PROCESSING_MODEL \
             to an engine that is enabled."
        );
    }

    /// The literal is the never-panic fallback, so it too must name an engine a
    /// user could pick. Without this, a catalog edit could leave the fallback
    /// pointing at a hidden engine and nothing would say so.
    #[test]
    fn the_fallback_literal_names_a_picker_engine() {
        let pp = CloudPpCatalog::embedded().expect("cloud-pp-catalog.json must parse");
        let (engine, model) = FALLBACK_CLOUD_POST_PROCESSING_MODEL
            .split_once(':')
            .expect("the fallback literal must be `<engineId>:<modelId>`");
        assert!(
            pp.picker_providers().any(|p| p.id == engine),
            "FALLBACK_CLOUD_POST_PROCESSING_MODEL names engine `{engine}`, which \
             the picker does not show"
        );
        assert!(
            pp.model(engine, model).is_some(),
            "FALLBACK_CLOUD_POST_PROCESSING_MODEL names model `{model}`, which is \
             not a visible model of `{engine}`"
        );
    }

    /// The gate, exercised with the gate actually flipped.
    ///
    /// `catalog_resolution_matches_the_fallback_literals` compares the resolver
    /// against a literal that the shipped catalog agrees with, so on its own it
    /// cannot tell a filtered resolver from an unfiltered one. This can: it
    /// hands the resolver a catalog where the seeded engine IS gated off. This
    /// is what the deleted .NET `AssertInvalidCatalogAsync` asserted.
    #[test]
    fn a_gated_off_engine_is_never_seeded() {
        let json = format!(
            r#"{{
                "version": 1, "updated": "x",
                "providers": [
                    {{"id":"{CLOUD_POST_PROCESSING_ENGINE}","displayName":"Gated",
                      "llmProvider":"gated","enabled":false,
                      "models":[{{"id":"hidden-model","displayName":"H","isDefault":true}}]}},
                    {{"id":"shown","displayName":"Shown","llmProvider":"shown","enabled":true,
                      "models":[{{"id":"visible-model","displayName":"V","isDefault":true}}]}}
                ]
            }}"#
        );
        let catalog = CloudPpCatalog::parse(&json).expect("synthetic catalog must parse");

        // The unfiltered lookup still resolves it — that is the trap, and it is
        // the correct behaviour for `CloudPpCatalog::default_model` itself.
        assert_eq!(
            catalog
                .default_model(CLOUD_POST_PROCESSING_ENGINE)
                .map(|m| m.id.as_str()),
            Some("hidden-model"),
            "the plain catalog lookup is expected to be unfiltered; if that \
             changed, this test no longer proves the seed does its own filtering"
        );

        // The seed does not.
        assert_eq!(
            resolve_cloud_post_processing_model(&catalog).as_deref(),
            Some("shown:visible-model"),
            "the seed resolved a gated-off engine"
        );
    }

    /// No enabled engine at all → resolve to nothing, and the caller falls back
    /// to the pinned literal rather than panicking. Unreachable in a shipped
    /// build (`the_seeded_engine_is_one_the_picker_shows` fails first), pinned
    /// because it is the one place the never-panic contract and the
    /// fail-closed contract genuinely conflict.
    #[test]
    fn nothing_enabled_resolves_to_nothing_rather_than_panicking() {
        let json = format!(
            r#"{{
                "version": 1, "updated": "x",
                "providers": [
                    {{"id":"{CLOUD_POST_PROCESSING_ENGINE}","displayName":"Gated",
                      "llmProvider":"gated","enabled":false,
                      "models":[{{"id":"hidden-model","displayName":"H","isDefault":true}}]}}
                ]
            }}"#
        );
        let catalog = CloudPpCatalog::parse(&json).expect("synthetic catalog must parse");
        assert_eq!(resolve_cloud_post_processing_model(&catalog), None);
    }

    /// An engine with no models, and an engine whose default model id is empty,
    /// are both "offers nothing" — the next enabled engine is used.
    #[test]
    fn an_engine_that_names_no_usable_model_is_skipped() {
        let json = format!(
            r#"{{
                "version": 1, "updated": "x",
                "providers": [
                    {{"id":"{CLOUD_POST_PROCESSING_ENGINE}","displayName":"Empty",
                      "llmProvider":"empty","enabled":true,"models":[]}},
                    {{"id":"blank","displayName":"Blank","llmProvider":"blank","enabled":true,
                      "models":[{{"id":"","displayName":"B","isDefault":true}}]}},
                    {{"id":"shown","displayName":"Shown","llmProvider":"shown","enabled":true,
                      "models":[{{"id":"visible-model","displayName":"V","isDefault":true}}]}}
                ]
            }}"#
        );
        let catalog = CloudPpCatalog::parse(&json).expect("synthetic catalog must parse");
        assert_eq!(
            resolve_cloud_post_processing_model(&catalog).as_deref(),
            Some("shown:visible-model")
        );
    }

    /// The persisted prefix is the catalog's own spelling of the engine id, not
    /// the caller's — `provider()` matches case-insensitively, and macOS'
    /// `fromStorageValue` splits on the prefix.
    #[test]
    fn the_engine_prefix_is_the_catalogs_own_spelling() {
        let json = r#"{
            "version": 1, "updated": "x",
            "providers": [
                {"id":"AnThRoPiC","displayName":"A","llmProvider":"a","enabled":true,
                 "models":[{"id":"m","displayName":"M","isDefault":true}]}
            ]
        }"#;
        let catalog = CloudPpCatalog::parse(json).expect("synthetic catalog must parse");
        assert_eq!(
            resolve_cloud_post_processing_model(&catalog).as_deref(),
            Some("AnThRoPiC:m")
        );
    }

    #[test]
    fn seeds_the_agreed_field_set() {
        let seed = mode_seed_default(Some("US"));

        assert_eq!(seed.id, "00000000-0000-0000-0000-000000000001");
        assert_eq!(seed.name, "Hyper");
        assert_eq!(seed.preset, "hyper");
        assert_eq!(seed.language, "auto");
        assert_eq!(seed.provider_type, "cloud");
        assert_eq!(seed.cloud_provider, "hyperwhisper");
        assert_eq!(seed.cloud_accuracy_tier, "elevenLabsScribeV2");
        assert_eq!(seed.cloud_transcription_model, "scribe_v2");
        assert_eq!(seed.post_processing_mode, 1);
        assert_eq!(seed.post_processing_provider, "hyperwhispercloud");
        assert_eq!(
            seed.cloud_post_processing_model,
            "anthropic:claude-haiku-4-5"
        );
        assert_eq!(seed.english_spelling, "american");
        assert!(seed.punctuation);
        assert!(seed.capitalization);
        assert!(!seed.profanity_filter);
        assert_eq!(seed.custom_instructions, "");
        assert!(seed.is_default);
        assert!(seed.is_system_provided);
        assert_eq!(seed.sort_order, 0);
    }

    /// The `<engineId>:<modelId>` prefix is load-bearing — macOS falls back to
    /// Grok on a value it cannot split.
    #[test]
    fn the_post_processing_model_keeps_its_engine_prefix() {
        let seed = mode_seed_default(None);
        let (engine, model) = seed
            .cloud_post_processing_model
            .split_once(':')
            .expect("the seeded PP model must be `<engineId>:<modelId>`");
        assert_eq!(engine, CLOUD_POST_PROCESSING_ENGINE);
        assert!(!model.is_empty());
    }

    #[test]
    fn the_region_only_moves_english_spelling() {
        let us = mode_seed_default(Some("US"));
        for region in ["GB", "AU", "CA", "JP", "ZZ", "", "  ", " gb "] {
            let seed = mode_seed_default(Some(region));
            assert_eq!(
                ModeSeed {
                    english_spelling: us.english_spelling.clone(),
                    ..seed.clone()
                },
                us,
                "region `{region}` changed a field other than englishSpelling"
            );
        }
    }

    #[test]
    fn english_spelling_follows_the_region_table() {
        for (region, expected) in [
            (Some("GB"), "british"),
            (Some("AU"), "australian"),
            (Some("NF"), "australian"),
            (Some("CA"), "canadian"),
            (Some("US"), "american"),
            (Some("JP"), "american"),
            (Some("ZZ"), "american"),
            (Some(""), "american"),
            (Some("  "), "american"),
            (Some(" gb "), "british"),
            (None, "american"),
        ] {
            assert_eq!(
                mode_seed_default(region).english_spelling,
                expected,
                "region {region:?}"
            );
        }
    }

    /// `""` is a real `EnglishSpelling` token meaning "no spelling instruction",
    /// and it must never be what a fresh install gets.
    #[test]
    fn english_spelling_is_never_the_empty_token() {
        for region in [Some("GB"), Some(""), Some("  "), Some("ZZ"), None] {
            assert!(
                !mode_seed_default(region).english_spelling.is_empty(),
                "region {region:?} seeded the empty spelling token"
            );
        }
    }
}
