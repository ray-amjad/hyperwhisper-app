#![allow(dead_code)]
//! `hw-backup` — cross-platform backup core (sans-I/O).
//!
//! M4 scope:
//! - [`validate`] — lightweight, dependency-free validation of a universal-v2
//!   backup document: the embedded schema's required fields, `schemaVersion == 2`
//!   and the `platform` enum, plus the type of every field
//!   [`records::UniversalBackup`] decodes, so a document it accepts parses. Not a
//!   full JSON Schema validator — see the module docs for the exact limitation.
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
    from_records, linux_settings_to_universal, macos_settings_to_universal,
    migrate_mode_cloud_routing, normalize_universal_mode_value, to_records,
    universal_to_linux_settings, universal_to_macos_settings, universal_to_windows_settings,
    windows_settings_to_universal, LinuxSettingDefault, MacosSettings, WindowsSettings,
    LINUX_ADVANCED_PAIRS, LINUX_GENERAL_PAIRS, LINUX_STORAGE_PAIRS, LINUX_STREAMING_PAIRS,
    LINUX_TEXT_OUTPUT_PAIRS, WINDOWS_ADVANCED_PAIRS, WINDOWS_GENERAL_PAIRS, WINDOWS_STORAGE_PAIRS,
    WINDOWS_STREAMING_PAIRS, WINDOWS_TEXT_OUTPUT_PAIRS,
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

    #[test]
    fn schema_version_2_0_parses_and_serializes_as_2() {
        let json = r#"{"schemaVersion":2.0,"exportDate":"2026-01-01T00:00:00Z","appVersion":"1.0","platform":"macos"}"#;
        assert!(validate_str(json).is_empty());
        let b = parse_backup(json).unwrap();
        assert_eq!(b.schema_version, 2);
        let out = serialize_backup(&b).unwrap();
        assert!(out.contains("\"schemaVersion\": 2,"), "got {out}");
    }

    #[test]
    fn parse_rejects_a_fractional_schema_version() {
        let json = r#"{"schemaVersion":2.5,"exportDate":"2026-01-01T00:00:00Z","appVersion":"1.0","platform":"macos"}"#;
        let err = parse_backup(json).unwrap_err().to_string();
        assert!(
            err.contains("schemaVersion must be an integer, got 2.5"),
            "got {err}"
        );
    }

    fn doc(extra: serde_json::Value) -> String {
        let mut v = serde_json::json!({
            "schemaVersion": 2,
            "exportDate": "2026-01-01T00:00:00Z",
            "appVersion": "1.0",
            "platform": "macos"
        });
        for (k, x) in extra.as_object().unwrap() {
            v[k] = x.clone();
        }
        v.to_string()
    }

    /// Issue #2090: every document the validator accepts must parse, and every
    /// one the parser rejects for a field type must fail validation at that path.
    #[test]
    fn validator_accepted_documents_are_parseable() {
        use serde_json::json;
        let bad = [
            (
                json!({ "modes": [{ "id": 5, "name": "W" }] }),
                "modes[0].id",
            ),
            (
                json!({ "modes": [{ "id": "m", "name": null }] }),
                "modes[0].name",
            ),
            (
                json!({ "vocabulary": [{ "id": "v", "word": null }] }),
                "vocabulary[0].word",
            ),
            (json!({ "exportDate": 1760000000 }), "exportDate"),
            (json!({ "appVersion": 1 }), "appVersion"),
            (
                json!({ "modes": [{ "id": "m", "name": "M", "sortOrder": 1.5 }] }),
                "modes[0].sortOrder",
            ),
            (json!({ "settings": [] }), "settings"),
            (
                json!({ "modes": [{ "id": "m", "name": "M", "isDefault": 1 }] }),
                "modes[0].isDefault",
            ),
            (
                json!({ "vocabulary": [{ "id": "v", "word": "w", "sortOrder": "1" }] }),
                "vocabulary[0].sortOrder",
            ),
            (json!({ "licenseKey": 7 }), "licenseKey"),
            (json!({ "apiKeys": "k" }), "apiKeys"),
        ];
        for (extra, path) in bad {
            let json = doc(extra);
            assert!(parse_backup(&json).is_err(), "parser accepts {json}");
            let errs = validate_str(&json);
            assert!(
                errs.iter().any(|e| e.path == path),
                "{json}: expected an error at {path}, got {errs:?}"
            );
        }

        let good = [
            json!({}),
            json!({ "modes": null, "vocabulary": null, "settings": null }),
            json!({ "modes": [{ "id": "m", "name": "M", "sortOrder": 1.0, "postProcessingMode": 2.0 }] }),
            json!({ "vocabulary": [{ "id": "v", "word": "w", "sortOrder": 3.0, "source": null }] }),
            json!({ "settings": { "general": 1, "x": [] }, "apiKeys": { "a": 1 }, "future": true }),
        ];
        for extra in good {
            let json = doc(extra);
            assert!(validate_str(&json).is_empty(), "validator rejects {json}");
            parse_backup(&json).unwrap_or_else(|e| panic!("{json}: {e}"));
        }
    }

    #[test]
    fn whole_number_sort_order_parses_and_reserializes_as_integer() {
        let json = doc(serde_json::json!({
            "modes": [{ "id": "m", "name": "M", "sortOrder": 1.0, "postProcessingMode": 2.0 }],
            "vocabulary": [{ "id": "v", "word": "w", "sortOrder": 3.0 }]
        }));
        let b = parse_backup(&json).unwrap();
        let mode = &b.modes.as_ref().unwrap()[0];
        assert_eq!(mode.sort_order, Some(1));
        assert_eq!(mode.post_processing_mode, Some(2));
        assert_eq!(b.vocabulary.as_ref().unwrap()[0].sort_order, Some(3));
        let out = serialize_backup(&b).unwrap();
        assert!(out.contains("\"sortOrder\": 1,"), "got {out}");
        assert!(out.contains("\"postProcessingMode\": 2"), "got {out}");
        assert!(out.contains("\"sortOrder\": 3"), "got {out}");
    }

    #[test]
    fn parse_names_the_integer_field_it_rejects() {
        let json =
            doc(serde_json::json!({ "modes": [{ "id": "m", "name": "M", "sortOrder": 1.5 }] }));
        let err = parse_backup(&json).unwrap_err().to_string();
        assert!(
            err.contains("sortOrder must be an integer, got 1.5"),
            "got {err}"
        );
        let json = doc(
            serde_json::json!({ "modes": [{ "id": "m", "name": "M", "postProcessingMode": "1" }] }),
        );
        let err = parse_backup(&json).unwrap_err().to_string();
        assert!(
            err.contains("postProcessingMode must be an integer"),
            "got {err}"
        );
    }
}
