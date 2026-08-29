//! The four embedded catalogs, each parsed once into a `OnceLock`.
//!
//! The JSON is a build-time invariant (`include_str!`), so `.expect()` on parse
//! is a programmer error, never a runtime failure.

use std::sync::OnceLock;

pub(super) fn models() -> &'static hw_catalog::ModelsCatalog {
    static C: OnceLock<hw_catalog::ModelsCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::ModelsCatalog::embedded().expect("embedded models-catalog.json must parse")
    })
}

/// `pub(crate)` so `ffi_backup::normalize_universal_mode_json` can compose the
/// `cloudProvider` fold with the `hw-backup` tier/pp migration.
pub(crate) fn cloud_stt() -> &'static hw_catalog::CloudSttCatalog {
    static C: OnceLock<hw_catalog::CloudSttCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::CloudSttCatalog::embedded().expect("embedded cloud-stt-catalog.json must parse")
    })
}

pub(super) fn cloud_pp() -> &'static hw_catalog::CloudPpCatalog {
    static C: OnceLock<hw_catalog::CloudPpCatalog> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::CloudPpCatalog::embedded().expect("embedded cloud-pp-catalog.json must parse")
    })
}

pub(super) fn app_classifier() -> &'static hw_catalog::AppTypeClassifier {
    static C: OnceLock<hw_catalog::AppTypeClassifier> = OnceLock::new();
    C.get_or_init(|| {
        hw_catalog::AppTypeClassifier::embedded()
            .expect("embedded app-type-catalog.json must parse")
    })
}
