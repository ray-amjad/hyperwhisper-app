#![allow(dead_code)]
//! `hw-backup` — cross-platform backup core (sans-I/O).
//!
//! M4 scope:
//! - [`validate`] — lightweight, dependency-free structural validation of a
//!   universal-v2 backup document against the embedded schema's load-bearing
//!   invariants (required top-level fields + `schemaVersion == 2` + enums +
//!   required item keys). Not a full JSON Schema validator — see the module docs
//!   for the exact limitation.
//! - [`records`] — plain-Rust POD types: the universal-v2 wire format
//!   ([`records::UniversalBackup`]) and the platform-neutral
//!   [`records::ModeRecord`] / [`records::SettingsRecord`] /
//!   [`records::BackupRecords`].
//! - [`mapping`] — bidirectional map between the universal wire format and the
//!   records ([`mapping::to_records`] / [`mapping::from_records`]), plus the
//!   macOS-specific 7→5 settings adapter
//!   ([`mapping::macos_settings_to_universal`] /
//!   [`mapping::universal_to_macos_settings`]).
//! - [`migrate`] — legacy `cloudAccuracyTier` / `cloudPostProcessingModel`
//!   alias migration, matching the macOS `fromStorageValue` reference impls.
//!
//! Plain Rust only — no `uniffi`. `hw-core` mirrors these types for FFI (Wave 2).
//! No I/O: callers read/write the `.hwbackup.json` bytes; this crate parses,
//! validates, and maps in memory only.

pub mod mapping;
pub mod migrate;
pub mod records;
pub mod validate;

pub use mapping::{
    empty_backup, from_records, macos_settings_to_universal, migrate_mode_cloud_routing, to_records,
    universal_to_macos_settings, MacosSettings,
};
pub use migrate::{
    migrate_cloud_accuracy_tier, migrate_cloud_pp_model, DEFAULT_CLOUD_ACCURACY_TIER,
    DEFAULT_CLOUD_PP_MODEL,
};
pub use records::{
    BackupRecords, ModeRecord, SettingsRecord, UniversalBackup, UniversalMode, UniversalSettings,
    UniversalVocabularyItem,
};
pub use validate::{
    validate_str, validate_value, ValidationError, PLATFORM_ENUM, UNIVERSAL_SCHEMA_VERSION,
};

/// The shared backup JSON schema, embedded at build time from
/// `shared-backup/hyperwhisper-backup.schema.json`.
pub const EMBEDDED_SCHEMA: &str =
    include_str!("../../../../shared-backup/hyperwhisper-backup.schema.json");

/// Parse raw `.hwbackup.json` bytes into a [`UniversalBackup`].
///
/// Returns a [`BackupError::Parse`] on malformed JSON or a shape that does not
/// fit the universal-v2 record model. This does NOT run structural validation —
/// call [`validate_str`] / [`validate_value`] for that (e.g. to reject a legacy
/// v1 file that happens to deserialize).
pub fn parse_backup(json: &str) -> Result<UniversalBackup, BackupError> {
    serde_json::from_str(json).map_err(|e| BackupError::Parse(e.to_string()))
}

/// Serialize a [`UniversalBackup`] back to a pretty JSON string.
pub fn serialize_backup(backup: &UniversalBackup) -> Result<String, BackupError> {
    serde_json::to_string_pretty(backup).map_err(|e| BackupError::Serialize(e.to_string()))
}

/// Errors surfaced by the top-level `hw-backup` entry points.
#[derive(Debug, thiserror::Error)]
pub enum BackupError {
    #[error("failed to parse backup JSON: {0}")]
    Parse(String),
    #[error("failed to serialize backup JSON: {0}")]
    Serialize(String),
    #[error("backup failed schema validation: {0}")]
    Invalid(String),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_schema_is_valid_json_v2() {
        let v: serde_json::Value = serde_json::from_str(EMBEDDED_SCHEMA).unwrap();
        assert_eq!(v["$id"], "https://hyperwhisper.com/schemas/backup/v2");
    }

    #[test]
    fn parse_then_serialize_minimal() {
        let json = r#"{"schemaVersion":2,"exportDate":"2026-01-01T00:00:00Z","appVersion":"1.0","platform":"macos"}"#;
        let b = parse_backup(json).unwrap();
        assert_eq!(b.schema_version, 2);
        assert!(validate_str(json).is_empty());
        let out = serialize_backup(&b).unwrap();
        assert!(out.contains("\"schemaVersion\": 2"));
    }
}
