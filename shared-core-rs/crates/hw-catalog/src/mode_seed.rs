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
//! # Never panics
//!
//! The workspace release profile sets `panic = "abort"`, and this runs on the
//! app's first-launch path, so a panic here is the app failing to start rather
//! than a recoverable error. The two catalog-resolved fields therefore fall
//! back to literals ([`FALLBACK_CLOUD_TRANSCRIPTION_MODEL`],
//! [`FALLBACK_CLOUD_POST_PROCESSING_MODEL`]) instead of unwrapping. That is not
//! a licence for the catalog to drift: `catalog_resolution_matches_the_fallback_literals`
//! asserts the resolved values EQUAL the literals, so a catalog edit that moves
//! either default fails CI loudly instead of degrading quietly at runtime.
//!
//! This is deliberately stronger than the `throw new InvalidDataException` the
//! portable .NET seeder used to do: the catalog is embedded at compile time, so
//! the guarantee is checked before the binary ships rather than at first launch.

use std::sync::OnceLock;

use crate::{CloudPpCatalog, CloudSttCatalog};

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

/// The catalog's default post-processing model for
/// [`CLOUD_POST_PROCESSING_ENGINE`] as `"<engineId>:<modelId>"`, or the fallback.
fn cloud_post_processing_model() -> &'static str {
    static RESOLVED: OnceLock<String> = OnceLock::new();
    RESOLVED.get_or_init(|| {
        CloudPpCatalog::embedded()
            .ok()
            .and_then(|catalog| {
                catalog
                    .default_model(CLOUD_POST_PROCESSING_ENGINE)
                    .filter(|model| !model.id.is_empty())
                    .map(|model| format!("{CLOUD_POST_PROCESSING_ENGINE}:{}", model.id))
            })
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
