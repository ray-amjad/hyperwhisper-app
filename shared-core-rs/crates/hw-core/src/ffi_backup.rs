//! UniFFI surface for the M4 backup map/validate core (`hw_backup`).
//!
//! The backup record types are built on `serde_json::Value` trees, which cannot
//! cross UniFFI. So the surface is a **JSON-string boundary**: the platform (which
//! has its own JSON parser) passes JSON strings in and receives JSON strings out;
//! Rust parses to the leaf structs, runs the map/validate logic, and re-serializes.
//! Only the FFI-clean `HwValidationError` / `BackupError` types are mirrored.
//!
//! JSON naming: settings records serialize camelCase to match the universal-v2
//! schema (`textOutput`, `platformExtensions`) and the macOS native category JSON
//! (`textOutput`, `aiModel`). The macOS call-site wiring (Wave 3) confirms these
//! against `BackupModels.swift`.

/// One structural validation failure. Mirrors `hw_backup::ValidationError`.
#[derive(uniffi::Record)]
pub struct HwValidationError {
    pub path: String,
    pub message: String,
}

impl From<hw_backup::ValidationError> for HwValidationError {
    fn from(e: hw_backup::ValidationError) -> Self {
        HwValidationError {
            path: e.path,
            message: e.message,
        }
    }
}

/// A backup parse/serialize/validate failure. Mirrors `hw_backup::BackupError`.
#[derive(uniffi::Error, Debug)]
pub enum BackupError {
    Parse { message: String },
    Serialize { message: String },
    Invalid { message: String },
}

impl std::fmt::Display for BackupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            BackupError::Parse { message } => write!(f, "failed to parse backup JSON: {message}"),
            BackupError::Serialize { message } => {
                write!(f, "failed to serialize backup JSON: {message}")
            }
            BackupError::Invalid { message } => {
                write!(f, "backup failed schema validation: {message}")
            }
        }
    }
}

impl std::error::Error for BackupError {}

impl From<hw_backup::BackupError> for BackupError {
    fn from(e: hw_backup::BackupError) -> Self {
        match e {
            hw_backup::BackupError::Parse(m) => BackupError::Parse { message: m },
            hw_backup::BackupError::Serialize(m) => BackupError::Serialize { message: m },
            hw_backup::BackupError::Invalid(m) => BackupError::Invalid { message: m },
        }
    }
}

/// Validate a backup JSON document against the embedded universal-v2 schema's
/// structural invariants. Returns every error found (empty = valid).
#[uniffi::export]
pub fn validate_backup_json(json: String) -> Vec<HwValidationError> {
    hw_backup::validate_str(&json)
        .into_iter()
        .map(HwValidationError::from)
        .collect()
}

/// Parse a universal-v2 backup and re-serialize it (canonicalize / round-trip).
/// Errors if the JSON is not a well-formed `UniversalBackup`.
#[uniffi::export]
pub fn normalize_backup_json(json: String) -> Result<String, BackupError> {
    let backup = hw_backup::parse_backup(&json)?;
    Ok(hw_backup::serialize_backup(&backup)?)
}

/// Map a macOS 7-category native settings JSON into a universal-v2 5-category
/// `SettingsRecord` JSON (macOS-only keys parked under `platformExtensions.macos`).
/// `existing_macos_ext_json`, when present, is the existing
/// `platformExtensions.macos` blob to merge into.
#[uniffi::export]
pub fn macos_settings_to_universal_settings_json(
    macos_json: String,
    existing_macos_ext_json: Option<String>,
) -> Result<String, BackupError> {
    let macos: hw_backup::MacosSettings =
        serde_json::from_str(&macos_json).map_err(|e| BackupError::Parse {
            message: e.to_string(),
        })?;
    let existing_ext: Option<serde_json::Value> = match existing_macos_ext_json {
        Some(s) => Some(serde_json::from_str(&s).map_err(|e| BackupError::Parse {
            message: e.to_string(),
        })?),
        None => None,
    };
    let record = hw_backup::macos_settings_to_universal(&macos, existing_ext.as_ref());
    serde_json::to_string(&record).map_err(|e| BackupError::Serialize {
        message: e.to_string(),
    })
}

/// Inverse of [`macos_settings_to_universal_settings_json`]: rebuild the macOS
/// 7-category native settings JSON from a universal `SettingsRecord` JSON.
#[uniffi::export]
pub fn universal_settings_to_macos_settings_json(record_json: String) -> Result<String, BackupError> {
    let record: hw_backup::SettingsRecord =
        serde_json::from_str(&record_json).map_err(|e| BackupError::Parse {
            message: e.to_string(),
        })?;
    let macos = hw_backup::universal_to_macos_settings(&record);
    serde_json::to_string(&macos).map_err(|e| BackupError::Serialize {
        message: e.to_string(),
    })
}

/// Canonicalize ONE wire-shaped universal-v2 mode object, returning the same
/// object with its five cloud-routing fields normalized and every other key
/// untouched. This is the single entry point both non-macOS mode-import paths
/// call (`UniversalBackupMapper.MapToMode`, `ApplicationBackupExport.ParseMode`).
///
/// It is the COMPOSITION POINT: `hw_backup` owns the present-only
/// tier/post-processing-model migration, `hw_catalog` owns the `cloudProvider`
/// fold and the legacy model-alias tables, and the `cloudTranscriptionDomain`
/// gate lives here. `hw-backup` must not depend on `hw-catalog`
/// (`shared-core-rs/README.md`), which is why the seam is in this crate.
///
/// What it does, in the order Windows does it:
///
/// 1. `cloudProvider` is folded through the catalog — a legacy standalone-provider
///    alias such as `microsoftazurespeech` becomes `hyperwhisper` plus an accuracy
///    tier. BYOK names (`deepgram`, `groq`) pass through untouched.
/// 2. `cloudTranscriptionModel` is alias-resolved against the **RAW** (pre-fold)
///    provider. Windows passes `universal.CloudProvider` — not the folded value —
///    to `ResolveModelAlias`, so a folded azure mode resolves under
///    `MicrosoftAzureSpeech` (the passthrough arm) even though its stored provider
///    became `hyperwhisper`. Reproduced deliberately.
/// 3. `cloudAccuracyTier` / `cloudPostProcessingModel` follow the two-assignment
///    precedence documented on [`hw_backup::normalize_universal_mode_value`].
/// 4. `cloudTranscriptionDomain` (the `X-STT-Domain` header) only applies to
///    HyperWhisper Cloud modes, so it is DROPPED unless the folded provider is
///    `hyperwhisper` — a stale domain on a BYOK mode must not import.
///
/// Absent fields stay absent: the caller applies its own entity default (both
/// heads share `elevenLabsScribeV2` / `anthropic:claude-haiku-4-5` from `Mode`'s
/// field initialisers). Errors only on JSON that is not an object.
#[uniffi::export]
pub fn normalize_universal_mode_json(json: String) -> Result<String, BackupError> {
    let mut value: serde_json::Value =
        serde_json::from_str(&json).map_err(|e| BackupError::Parse {
            message: e.to_string(),
        })?;

    let accuracy_tier = {
        let Some(obj) = value.as_object_mut() else {
            return Err(BackupError::Parse {
                message: "universal mode must be a JSON object".to_string(),
            });
        };

        // (1) cloudProvider fold. The RAW value is kept for step (2).
        let raw_provider = obj
            .get("cloudProvider")
            .and_then(serde_json::Value::as_str)
            .map(str::to_string);
        let normalized =
            crate::ffi_catalog::cloud_stt().normalize_cloud_provider(raw_provider.as_deref());

        // (2) model alias, against the RAW provider (see the doc comment).
        if let Some(model) = obj
            .get("cloudTranscriptionModel")
            .and_then(serde_json::Value::as_str)
        {
            let resolved = hw_catalog::resolve_model_alias(model, raw_provider.as_deref());
            obj.insert(
                "cloudTranscriptionModel".to_string(),
                serde_json::Value::String(resolved),
            );
        }

        match &normalized.provider {
            Some(p) => {
                obj.insert(
                    "cloudProvider".to_string(),
                    serde_json::Value::String(p.clone()),
                );
            }
            None => {
                obj.remove("cloudProvider");
            }
        }

        // (4) domain gate — HyperWhisper Cloud modes only.
        if normalized.provider.as_deref() != Some("hyperwhisper") {
            obj.remove("cloudTranscriptionDomain");
        }

        normalized.accuracy_tier
    };

    // (3) tier / post-processing model.
    hw_backup::normalize_universal_mode_value(&mut value, accuracy_tier.as_deref());

    serde_json::to_string(&value).map_err(|e| BackupError::Serialize {
        message: e.to_string(),
    })
}

/// Migrate a persisted `cloudAccuracyTier` storage string to its canonical
/// catalog id. `None`/empty → the default tier.
#[uniffi::export]
pub fn migrate_cloud_accuracy_tier(value: Option<String>) -> String {
    hw_backup::migrate_cloud_accuracy_tier(value.as_deref())
}

/// Migrate a persisted `cloudPostProcessingModel` storage string to its canonical
/// `"<engineId>:<modelId>"` form. `None`/empty/unknown → the default model.
#[uniffi::export]
pub fn migrate_cloud_pp_model(value: Option<String>) -> String {
    hw_backup::migrate_cloud_pp_model(value.as_deref())
}

#[cfg(test)]
mod normalize_universal_mode_tests {
    use super::normalize_universal_mode_json;
    use serde_json::{json, Value};

    fn run(mode: Value) -> Value {
        serde_json::from_str(&normalize_universal_mode_json(mode.to_string()).unwrap()).unwrap()
    }

    #[test]
    fn legacy_provider_folds_onto_hyperwhisper_and_supplies_a_tier() {
        let out = run(json!({ "cloudProvider": "microsoftazurespeech" }));
        assert_eq!(out["cloudProvider"], "hyperwhisper");
        assert_eq!(out["cloudAccuracyTier"], "azureMaiTranscribe");
    }

    #[test]
    fn byok_providers_do_not_fold() {
        // "deepgram" appears in a tier's migrateFrom list but is a real BYOK
        // provider name; folding it would silently move a BYOK mode onto Cloud.
        let out = run(json!({ "cloudProvider": "deepgram" }));
        assert_eq!(out["cloudProvider"], "deepgram");
        assert!(!out.as_object().unwrap().contains_key("cloudAccuracyTier"));
    }

    #[test]
    fn model_alias_resolves_against_the_raw_pre_fold_provider() {
        // THE TRAP: Windows passes universal.CloudProvider — not the folded value —
        // to ResolveModelAlias. A folded azure mode therefore resolves under
        // MicrosoftAzureSpeech (the passthrough arm) even though its stored
        // provider became "hyperwhisper". Reproduced deliberately.
        let out = run(json!({
            "cloudProvider": "microsoftazurespeech",
            "cloudTranscriptionModel": "nova-2"
        }));
        assert_eq!(out["cloudProvider"], "hyperwhisper");
        assert_eq!(
            out["cloudTranscriptionModel"], "nova-2",
            "resolving against the FOLDED provider would have chained the tables"
        );
    }

    #[test]
    fn model_alias_uses_the_provider_scoped_table() {
        let out = run(json!({ "cloudProvider": "deepgram", "cloudTranscriptionModel": "nova-2" }));
        assert_eq!(out["cloudTranscriptionModel"], "nova-2-general");
    }

    #[test]
    fn model_alias_chains_every_table_when_no_provider_is_given() {
        let out = run(json!({ "cloudTranscriptionModel": "scribe_v1" }));
        assert_eq!(out["cloudTranscriptionModel"], "scribe_v2");
    }

    #[test]
    fn domain_survives_only_on_hyperwhisper_cloud_modes() {
        let cloud = run(json!({
            "cloudProvider": "hyperwhisper",
            "cloudTranscriptionDomain": "medical"
        }));
        assert_eq!(cloud["cloudTranscriptionDomain"], "medical");

        // A folded legacy provider becomes hyperwhisper, so its domain survives.
        let folded = run(json!({
            "cloudProvider": "microsoftazurespeech",
            "cloudTranscriptionDomain": "medical"
        }));
        assert_eq!(folded["cloudTranscriptionDomain"], "medical");

        // A stale domain on a BYOK mode must not import.
        let byok = run(json!({
            "cloudProvider": "deepgram",
            "cloudTranscriptionDomain": "medical"
        }));
        assert!(!byok
            .as_object()
            .unwrap()
            .contains_key("cloudTranscriptionDomain"));

        // No provider at all → not a Cloud mode either.
        let none = run(json!({ "cloudTranscriptionDomain": "medical" }));
        assert!(!none
            .as_object()
            .unwrap()
            .contains_key("cloudTranscriptionDomain"));
    }

    #[test]
    fn absent_stays_absent_end_to_end() {
        let out = run(json!({ "id": "m", "name": "n" }));
        assert_eq!(out, json!({ "id": "m", "name": "n" }));
    }

    #[test]
    fn pp_model_legacy_switch_matches_the_whole_token() {
        // "notanengine:claudeHaiku" has an unknown engine, so it falls through to
        // the single-token table, which matches the WHOLE trimmed string and misses
        // — landing on the grok fallback, not anthropic.
        let out = run(json!({ "cloudPostProcessingModel": "notanengine:claudeHaiku" }));
        assert_eq!(out["cloudPostProcessingModel"], "grok:grok-4.3");
    }

    #[test]
    fn a_non_object_document_is_an_error() {
        assert!(normalize_universal_mode_json("[]".to_string()).is_err());
        assert!(normalize_universal_mode_json("not json".to_string()).is_err());
    }
}
