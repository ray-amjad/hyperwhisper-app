//! `hw-catalog` — cross-platform catalog core (sans-I/O).
//!
//! Embeds the shared catalog JSON files and (in Wave 1) exposes typed lookups
//! over them. Plain Rust — `hw-core` mirrors its types for UniFFI.

// WP-D2: models-catalog parsing + lookup.
mod models;
// WP-D3: cloud-STT / cloud-PP / app-type classification catalogs.
mod app_type;
mod cloud_pp;
mod cloud_stt;
// Legacy cloud-STT model-id aliases (ported from the Windows dictionaries).
mod model_alias;
// The language catalog: BCP-47 canonicalization, the alias map and the one
// reconciled row set (#285).
//
// The three panic-free lints are applied HERE rather than in the package's
// Cargo.toml, because a package-level table would also cover the four older
// modules above and this change is not the place to audit them. The reason they
// apply to this module: it is on the picker's build path on every head, and the
// workspace release profile sets `panic = "abort"`, so an out-of-range index
// here is the settings window failing to open rather than a wrong list.
#[deny(
    clippy::indexing_slicing,
    clippy::unwrap_used,
    clippy::expect_used
)]
mod language;
// The one canonical first-run mode seed, shared by all three heads (#285).
//
// Same three panic-free lints, for a sharper version of the same reason: this
// module runs on the app's FIRST-LAUNCH path under `panic = "abort"`, so an
// unwrap here is the app failing to start on a brand-new install — the one
// moment with no prior state to recover from. The module's own docs explain the
// fallback-plus-pinning-test pattern that replaces unwrapping.
#[deny(
    clippy::indexing_slicing,
    clippy::unwrap_used,
    clippy::expect_used
)]
mod mode_seed;

pub use models::{CatalogError, Entry, Kind, LanguageSupport, ModelsCatalog};

pub use cloud_stt::{
    Access, CloudSttCatalog, CloudSttError, CloudTier, CustomVocabulary, Features, Languages,
    NormalizedCloudProvider, SttEntry, SttModel, VendorGroup, VocabSupport,
};

pub use cloud_pp::{CloudPpCatalog, CloudPpError, PpModel, PpProvider};

pub use model_alias::{
    resolve_assemblyai_model_alias, resolve_deepgram_model_alias, resolve_elevenlabs_model_alias,
    resolve_gemini_model_alias, resolve_model_alias, resolve_soniox_model_alias,
};

pub use app_type::{
    is_webmail, AppClassification, AppType, AppTypeClassifier, AppTypeError, ClassifyRequest,
};

pub use mode_seed::{
    mode_seed_default, ModeSeed, CLOUD_ACCURACY_TIER, CLOUD_POST_PROCESSING_ENGINE,
    FALLBACK_CLOUD_POST_PROCESSING_MODEL, FALLBACK_CLOUD_TRANSCRIPTION_MODEL, HYPER_MODE_ID,
    HYPER_MODE_NAME, HYPER_MODE_PRESET, POST_PROCESSING_PROVIDER,
};

pub use language::{
    all_languages, canonical_language_code, canonicalize as canonicalize_language_code, info,
    is_english, normalize_language_code, prioritize_automatic, resolve as resolve_languages,
    Language, AUTOMATIC_CODE, POPULAR_CODES,
};

/// Per-model metadata catalog, from `shared-models/models-catalog.json`.
pub const MODELS_CATALOG: &str = include_str!("../../../../shared-models/models-catalog.json");

/// Cloud STT provider/model catalog, from
/// `shared-app-classification/cloud-stt-catalog.json`.
pub const CLOUD_STT_CATALOG: &str =
    include_str!("../../../../shared-app-classification/cloud-stt-catalog.json");

/// Cloud post-processing provider/model catalog, from
/// `shared-app-classification/cloud-pp-catalog.json`.
pub const CLOUD_PP_CATALOG: &str =
    include_str!("../../../../shared-app-classification/cloud-pp-catalog.json");

/// App-type classification catalog, from
/// `shared-app-classification/app-type-catalog.json`.
pub const APP_TYPE_CATALOG: &str =
    include_str!("../../../../shared-app-classification/app-type-catalog.json");
