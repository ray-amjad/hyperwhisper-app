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
