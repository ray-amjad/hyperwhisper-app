//! UniFFI surface for the M4 catalogs (`hw_catalog`).
//!
//! The leaf catalog objects (`ModelsCatalog`, `CloudSttCatalog`, `CloudPpCatalog`,
//! `AppTypeClassifier`) expose borrow-returning methods (`&str`, `&[T]`,
//! `Option<&T>`, `impl Iterator`) that cannot cross UniFFI. So instead of mirroring
//! the catalog objects, we expose **free functions over the embedded catalogs**
//! returning OWNED values. Each catalog is parsed once from its compile-time
//! `include_str!` JSON into a `OnceLock` (the JSON is a build-time invariant, so
//! `.expect()` on parse is a programmer error, never a runtime failure).
//!
//! The surface is split one module per catalog. This file stays the module root
//! and re-exports all of it, so `hyperwhisper_core::ffi_catalog::*` names
//! exactly what it named before the split — the generated bindings, the
//! conformance vectors and `ffi_backup` all keep their paths.

mod app;
mod catalogs;
mod models;
mod pp;
mod stt;

pub use app::*;
pub use models::*;
pub use pp::*;
pub use stt::*;

// Re-exported at the old path so `ffi_backup` keeps calling
// `crate::ffi_catalog::cloud_stt()`. See `catalogs::cloud_stt` for why it is
// `pub(crate)` and the other three accessors are not.
pub(crate) use catalogs::cloud_stt;

#[cfg(test)]
mod tests;
