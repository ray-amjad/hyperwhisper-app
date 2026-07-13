//! Plain-Rust POD records for the universal-v2 backup format and the
//! platform-supplied `ModeRecord` / `SettingsRecord` shapes that `hw-backup`
//! maps to and from.
//!
//! These mirror the cross-platform schema (`shared-backup/hyperwhisper-backup.schema.json`)
//! and the platform models (`UniversalBackupModels.cs`, `BackupModels.swift`).
//! No `uniffi`, no I/O — `hw-core` re-declares the FFI-facing twins in Wave 2.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;

// ============================================================================
// Universal v2 wire format (parsed 1:1 from the .hwbackup.json file)
// ============================================================================

/// The root of a universal-v2 `.hwbackup.json` document.
///
/// Unknown / platform-only sections are preserved verbatim (`platform_extensions`,
/// and the open `extra` maps on the nested objects) so a parse → re-serialize
/// round-trip is lossless — the requirement the golden tests enforce.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UniversalBackup {
    #[serde(rename = "schemaVersion")]
    pub schema_version: i64,
    #[serde(rename = "exportDate")]
    pub export_date: String,
    #[serde(rename = "appVersion")]
    pub app_version: String,
    pub platform: String,

    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub settings: Option<UniversalSettings>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modes: Option<Vec<UniversalMode>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub vocabulary: Option<Vec<UniversalVocabularyItem>>,
    #[serde(rename = "apiKeys", default, skip_serializing_if = "Option::is_none")]
    pub api_keys: Option<BTreeMap<String, Value>>,
    #[serde(rename = "licenseKey", default, skip_serializing_if = "Option::is_none")]
    pub license_key: Option<String>,
    #[serde(
        rename = "platformExtensions",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub platform_extensions: Option<BTreeMap<String, Value>>,

    /// Any unknown top-level keys, captured verbatim so a parse → re-serialize
    /// round-trip is lossless even for keys this struct does not model (e.g. a
    /// future schema addition). An empty map serializes to nothing (flatten).
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

/// Grouped universal settings. Each category is optional (section-selectable
/// backups omit absent keys). `extra` captures any group the schema does not
/// model here so the round-trip stays lossless.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
pub struct UniversalSettings {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub general: Option<Value>,
    #[serde(
        rename = "textOutput",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub text_output: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub storage: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub streaming: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub advanced: Option<Value>,

    /// Unknown settings categories, preserved verbatim for a lossless round-trip.
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

/// A universal-v2 mode. Optional everywhere except `id`/`name` (schema-required).
/// `platform_extensions` and unknown keys are preserved.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UniversalMode {
    pub id: String,
    pub name: String,

    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub preset: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub language: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(rename = "isDefault", default, skip_serializing_if = "Option::is_none")]
    pub is_default: Option<bool>,
    #[serde(rename = "sortOrder", default, skip_serializing_if = "Option::is_none")]
    pub sort_order: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub punctuation: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capitalization: Option<bool>,
    #[serde(
        rename = "profanityFilter",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub profanity_filter: Option<bool>,
    #[serde(
        rename = "removeTrailingPeriod",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub remove_trailing_period: Option<bool>,
    #[serde(
        rename = "englishSpelling",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub english_spelling: Option<String>,
    #[serde(
        rename = "cloudProvider",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub cloud_provider: Option<String>,
    #[serde(
        rename = "cloudTranscriptionModel",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub cloud_transcription_model: Option<String>,
    #[serde(
        rename = "cloudTranscriptionDomain",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub cloud_transcription_domain: Option<String>,
    #[serde(
        rename = "postProcessingMode",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub post_processing_mode: Option<i64>,
    #[serde(
        rename = "postProcessingProvider",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub post_processing_provider: Option<String>,
    #[serde(
        rename = "languageModel",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub language_model: Option<String>,
    #[serde(
        rename = "localPostProcessingModel",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub local_post_processing_model: Option<String>,
    #[serde(
        rename = "userSystemPrompt",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub user_system_prompt: Option<String>,
    #[serde(
        rename = "customInstructions",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub custom_instructions: Option<String>,
    #[serde(
        rename = "geminiCustomPrompt",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub gemini_custom_prompt: Option<String>,
    #[serde(
        rename = "cloudAccuracyTier",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub cloud_accuracy_tier: Option<String>,
    #[serde(
        rename = "cloudPostProcessingModel",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub cloud_post_processing_model: Option<String>,
    #[serde(
        rename = "platformExtensions",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub platform_extensions: Option<BTreeMap<String, Value>>,

    /// Unknown mode keys, preserved verbatim for a lossless round-trip.
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

/// A universal-v2 vocabulary item.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UniversalVocabularyItem {
    pub id: String,
    pub word: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub replacement: Option<String>,
    #[serde(rename = "sortOrder", default, skip_serializing_if = "Option::is_none")]
    pub sort_order: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source: Option<String>,

    /// Unknown vocabulary-item keys, preserved verbatim for a lossless round-trip.
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

// ============================================================================
// Platform-supplied PODs (the records the platform hands in / gets back)
// ============================================================================

/// Platform-neutral mode record. This is the shape the host platform constructs
/// from its native `Mode` (macOS Core Data / Windows EF Core) and hands to the
/// mapping layer, and the shape it receives back when importing a universal
/// backup. Field names follow the shared mode mapping table in
/// `shared-backup/CLAUDE.md`.
///
/// Cross-platform shared fields are first-class; platform-only fields (Windows
/// `localEngine`, `enableScreenOCR`, … / macOS shortcut blob) ride along in
/// `platform_extensions` so they survive a round-trip even when the running
/// platform does not understand them.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ModeRecord {
    pub id: String,
    pub name: String,
    pub preset: Option<String>,
    pub language: Option<String>,
    pub model: Option<String>,
    pub is_default: Option<bool>,
    pub sort_order: Option<i64>,
    pub punctuation: Option<bool>,
    pub capitalization: Option<bool>,
    pub profanity_filter: Option<bool>,
    pub remove_trailing_period: Option<bool>,
    pub english_spelling: Option<String>,
    pub cloud_provider: Option<String>,
    pub cloud_transcription_model: Option<String>,
    pub cloud_transcription_domain: Option<String>,
    pub post_processing_mode: Option<i64>,
    pub post_processing_provider: Option<String>,
    pub language_model: Option<String>,
    pub local_post_processing_model: Option<String>,
    pub user_system_prompt: Option<String>,
    pub custom_instructions: Option<String>,
    pub gemini_custom_prompt: Option<String>,
    /// Already-migrated (provider-qualified) cloud accuracy tier id.
    pub cloud_accuracy_tier: Option<String>,
    /// Already-migrated (`<engineId>:<modelId>`) cloud post-processing key.
    pub cloud_post_processing_model: Option<String>,
    /// Preserved platform-only blobs keyed by platform name (`"windows"`, `"macos"`).
    ///
    /// Mirrors the wire type ([`UniversalMode::platform_extensions`]) as an
    /// `Option` so the absent-vs-explicit-`{}` distinction survives a round-trip.
    /// The macOS example fixture ships an explicit `"platformExtensions": {}` on
    /// every mode; collapsing that to a plain (empty) map would erase the key on
    /// re-serialize. `None` = key absent, `Some(empty)` = explicit `{}`.
    pub platform_extensions: Option<BTreeMap<String, Value>>,
    /// Unknown mode keys carried through from the wire ([`UniversalMode::extra`])
    /// so the records round-trip is as lossless as the wire round-trip.
    pub extra: BTreeMap<String, Value>,
}

/// Platform-neutral settings record. The five universal categories are kept as
/// open JSON objects (each setting is a simple scalar; modelling every field as
/// a typed Rust field would couple this crate to per-field churn for zero gain).
/// `platform_extensions` carries the top-level `platformExtensions.<platform>`
/// blob (including the macOS shortcuts settings, see `mapping`).
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsRecord {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub general: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text_output: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub storage: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub streaming: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub advanced: Option<Value>,
    /// Top-level `platformExtensions` map (e.g. `{"macos": {...}, "windows": {...}}`).
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub platform_extensions: BTreeMap<String, Value>,
    /// Unknown settings categories carried through from the wire
    /// ([`UniversalSettings::extra`]) for a lossless round-trip.
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}

/// Everything a host needs from a parsed backup, in platform-neutral record form.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct BackupRecords {
    pub schema_version: i64,
    pub export_date: String,
    pub app_version: String,
    pub platform: String,
    pub settings: Option<SettingsRecord>,
    pub modes: Vec<ModeRecord>,
    pub vocabulary: Vec<UniversalVocabularyItem>,
    pub api_keys: BTreeMap<String, Value>,
    pub license_key: Option<String>,
    /// Top-level `platformExtensions` map (`{"macos": {...}, "windows": {...}}`),
    /// preserved verbatim for round-trip fidelity. Distinct from a mode's own
    /// per-mode `platform_extensions`.
    pub platform_extensions: BTreeMap<String, Value>,
    /// Unknown top-level keys carried through from the wire
    /// ([`UniversalBackup::extra`]) for a lossless round-trip.
    pub extra: BTreeMap<String, Value>,
}
