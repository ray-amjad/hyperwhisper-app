#![allow(dead_code)]
//! `hw-license` — license validation and usage-state core (sans-I/O).
//!
//! Persistence is delegated to the platform via [`KeyValueStore`]: Rust owns the
//! validation/usage logic, the platform owns where the bytes live (Keychain,
//! Windows Credential Manager, etc.). Plain Rust — `hw-core` re-exports
//! `KeyValueStore` as a UniFFI foreign trait (callback interface) in Wave 2.
//!
//! # Module layout
//! - [`validate`] — build the `/api/license/validate` request, parse the
//!   response, map it to a [`LicenseStatus`] / [`validate::ValidationOutcome`].
//! - [`cache`] — 24h validation cache, 7-day offline grace, 24h remote
//!   trial-limit override — all keyed off an injected `now_unix_secs`.
//! - [`usage`] — trial limit enforcement, usage recording, day-boundary reset.
//!
//! # No clock, no I/O
//! Nothing in this crate reads the wall clock or the network. Time-dependent
//! functions take `now_unix_secs: i64` from the platform; HTTP is expressed as
//! request/response *values*. This makes every behavior deterministically
//! golden-testable (see each module's `#[cfg(test)]` block and the `tests/`
//! integration suite).

pub mod cache;
pub mod usage;
pub mod validate;

/// The current license state. Mirrors macOS `LicenseStatus` (rawValue
/// "Trial"/"Active"/"Expired"/"Invalid") and Windows `LicenseStatus`.
///
/// - `Trial` — no paid license; subject to daily-seconds + model-download limits.
/// - `Active` — valid paid license; unlimited usage.
/// - `Expired` — was valid, now lapsed; reverts to trial limits.
/// - `Invalid` — malformed/revoked/unknown key.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LicenseStatus {
    Trial,
    Active,
    Expired,
    Invalid,
}

/// Native key-value persistence the platform implements; Rust calls back
/// into it for license + usage state. `hw-core` re-exports this as a UniFFI
/// foreign trait (callback interface) in Wave 2.
pub trait KeyValueStore: Send + Sync {
    fn get(&self, key: String) -> Option<String>;
    fn set(&self, key: String, value: String);
    fn delete(&self, key: String);
}

/// An in-memory [`KeyValueStore`] for tests (and any platform that wants a
/// volatile store). Thread-safe via an internal `Mutex`, matching the `Send +
/// Sync` bound on the trait.
///
/// Kept in the library (not `#[cfg(test)]`) so the integration tests in `tests/`
/// and downstream crates can reuse it.
#[derive(Default)]
pub struct MemoryStore {
    inner: std::sync::Mutex<std::collections::HashMap<String, String>>,
}

impl MemoryStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Number of keys currently stored (test helper).
    pub fn len(&self) -> usize {
        self.inner.lock().expect("store mutex poisoned").len()
    }

    /// Whether the store is empty (test helper).
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl KeyValueStore for MemoryStore {
    fn get(&self, key: String) -> Option<String> {
        self.inner
            .lock()
            .expect("store mutex poisoned")
            .get(&key)
            .cloned()
    }

    fn set(&self, key: String, value: String) {
        self.inner
            .lock()
            .expect("store mutex poisoned")
            .insert(key, value);
    }

    fn delete(&self, key: String) {
        self.inner
            .lock()
            .expect("store mutex poisoned")
            .remove(&key);
    }
}
