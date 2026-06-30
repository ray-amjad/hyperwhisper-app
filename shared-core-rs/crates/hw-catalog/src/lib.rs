#![allow(dead_code)]
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

pub use models::{CatalogError, Entry, Kind, LanguageSupport, ModelsCatalog};

pub use cloud_stt::{
    Access, CloudSttCatalog, CloudSttError, CloudTier, CustomVocabulary, NormalizedCloudProvider,
    SttEntry, SttModel, VocabSupport,
};

pub use cloud_pp::{CloudPpCatalog, CloudPpError, PpModel, PpProvider};

pub use app_type::{AppClassification, AppType, AppTypeClassifier, AppTypeError};

/// Per-model metadata catalog, from `shared-models/models-catalog.json`.
pub const MODELS_CATALOG: &str =
    include_str!("../../../../shared-models/models-catalog.json");

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
