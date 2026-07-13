//! Bidirectional mapping between the universal-v2 wire format
//! ([`UniversalBackup`]) and the platform-neutral record PODs
//! ([`BackupRecords`] / [`ModeRecord`] / [`SettingsRecord`]).
//!
//! Two layers:
//!
//! 1. **Universal ⇄ records** ([`to_records`] / [`from_records`]) — the generic,
//!    platform-agnostic projection. Lossless: `from_records(to_records(b)) == b`
//!    for any well-formed universal backup (the golden round-trip).
//!
//! 2. **macOS settings adapter** ([`macos_settings_to_universal`] /
//!    [`universal_to_macos_settings`]) — the macOS-specific path that ADDS v2:
//!    it maps macOS's 7 settings categories (`general`, `audio`, `storage`,
//!    `textOutput`, `shortcuts`, `aiModel`, `advanced` — see
//!    `BackupModels.swift`) onto the universal 5 (`general`, `textOutput`,
//!    `storage`, `streaming`, `advanced`) plus a `platformExtensions.macos`
//!    blob that carries every macOS-only setting (audio extras, aiModel,
//!    shortcuts, storage/advanced extras) so a mac→…→mac round-trip is lossless.
//!
//! Parity note: macOS is the verified platform. Windows already speaks universal
//! v2 natively (`UniversalBackupModels.cs`), so the generic layer (1) IS its
//! mapping — no Windows-specific adapter is needed. The macOS adapter (2) is the
//! new code path the schema's mapping table describes as "intended".

use crate::migrate::{migrate_cloud_accuracy_tier, migrate_cloud_pp_model};
use crate::records::*;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;

// ============================================================================
// Layer 1: Universal ⇄ records (generic)
// ============================================================================

/// Project a parsed [`UniversalBackup`] into platform-neutral [`BackupRecords`].
pub fn to_records(backup: &UniversalBackup) -> BackupRecords {
    BackupRecords {
        schema_version: backup.schema_version,
        export_date: backup.export_date.clone(),
        app_version: backup.app_version.clone(),
        platform: backup.platform.clone(),
        settings: backup.settings.as_ref().map(settings_to_record),
        modes: backup
            .modes
            .as_ref()
            .map(|ms| ms.iter().map(mode_to_record).collect())
            .unwrap_or_default(),
        vocabulary: backup.vocabulary.clone().unwrap_or_default(),
        api_keys: backup.api_keys.clone().unwrap_or_default(),
        license_key: backup.license_key.clone(),
        platform_extensions: backup.platform_extensions.clone().unwrap_or_default(),
        extra: backup.extra.clone(),
    }
}

/// Rebuild a [`UniversalBackup`] from platform-neutral [`BackupRecords`]. Inverse
/// of [`to_records`]. Absent optional sections stay absent.
pub fn from_records(records: &BackupRecords) -> UniversalBackup {
    let platform_extensions = if records.platform_extensions.is_empty() {
        None
    } else {
        Some(records.platform_extensions.clone())
    };

    UniversalBackup {
        schema_version: records.schema_version,
        export_date: records.export_date.clone(),
        app_version: records.app_version.clone(),
        platform: records.platform.clone(),
        settings: records.settings.as_ref().map(record_to_settings),
        modes: if records.modes.is_empty() {
            None
        } else {
            Some(records.modes.iter().map(record_to_mode).collect())
        },
        vocabulary: if records.vocabulary.is_empty() {
            None
        } else {
            Some(records.vocabulary.clone())
        },
        api_keys: if records.api_keys.is_empty() {
            None
        } else {
            Some(records.api_keys.clone())
        },
        license_key: records.license_key.clone(),
        platform_extensions,
        extra: records.extra.clone(),
    }
}

fn settings_to_record(s: &UniversalSettings) -> SettingsRecord {
    SettingsRecord {
        general: s.general.clone(),
        text_output: s.text_output.clone(),
        storage: s.storage.clone(),
        streaming: s.streaming.clone(),
        advanced: s.advanced.clone(),
        // Generic path: top-level platformExtensions lives on BackupRecords, not
        // here. The macOS adapter populates this field for its own purposes.
        platform_extensions: BTreeMap::new(),
        extra: s.extra.clone(),
    }
}

fn record_to_settings(r: &SettingsRecord) -> UniversalSettings {
    UniversalSettings {
        general: r.general.clone(),
        text_output: r.text_output.clone(),
        storage: r.storage.clone(),
        streaming: r.streaming.clone(),
        advanced: r.advanced.clone(),
        extra: r.extra.clone(),
    }
}

fn mode_to_record(m: &UniversalMode) -> ModeRecord {
    ModeRecord {
        id: m.id.clone(),
        name: m.name.clone(),
        preset: m.preset.clone(),
        language: m.language.clone(),
        model: m.model.clone(),
        is_default: m.is_default,
        sort_order: m.sort_order,
        punctuation: m.punctuation,
        capitalization: m.capitalization,
        profanity_filter: m.profanity_filter,
        remove_trailing_period: m.remove_trailing_period,
        english_spelling: m.english_spelling.clone(),
        cloud_provider: m.cloud_provider.clone(),
        cloud_transcription_model: m.cloud_transcription_model.clone(),
        cloud_transcription_domain: m.cloud_transcription_domain.clone(),
        post_processing_mode: m.post_processing_mode,
        post_processing_provider: m.post_processing_provider.clone(),
        language_model: m.language_model.clone(),
        local_post_processing_model: m.local_post_processing_model.clone(),
        user_system_prompt: m.user_system_prompt.clone(),
        custom_instructions: m.custom_instructions.clone(),
        gemini_custom_prompt: m.gemini_custom_prompt.clone(),
        cloud_accuracy_tier: m.cloud_accuracy_tier.clone(),
        cloud_post_processing_model: m.cloud_post_processing_model.clone(),
        // Mirror the wire Option exactly: absent stays None, explicit `{}` stays
        // Some(empty). Do NOT collapse to a plain map (that erased the key).
        platform_extensions: m.platform_extensions.clone(),
        extra: m.extra.clone(),
    }
}

fn record_to_mode(r: &ModeRecord) -> UniversalMode {
    UniversalMode {
        id: r.id.clone(),
        name: r.name.clone(),
        preset: r.preset.clone(),
        language: r.language.clone(),
        model: r.model.clone(),
        is_default: r.is_default,
        sort_order: r.sort_order,
        punctuation: r.punctuation,
        capitalization: r.capitalization,
        profanity_filter: r.profanity_filter,
        remove_trailing_period: r.remove_trailing_period,
        english_spelling: r.english_spelling.clone(),
        cloud_provider: r.cloud_provider.clone(),
        cloud_transcription_model: r.cloud_transcription_model.clone(),
        cloud_transcription_domain: r.cloud_transcription_domain.clone(),
        post_processing_mode: r.post_processing_mode,
        post_processing_provider: r.post_processing_provider.clone(),
        language_model: r.language_model.clone(),
        local_post_processing_model: r.local_post_processing_model.clone(),
        user_system_prompt: r.user_system_prompt.clone(),
        custom_instructions: r.custom_instructions.clone(),
        gemini_custom_prompt: r.gemini_custom_prompt.clone(),
        cloud_accuracy_tier: r.cloud_accuracy_tier.clone(),
        cloud_post_processing_model: r.cloud_post_processing_model.clone(),
        // Mirror the record Option exactly (inverse of mode_to_record): preserves
        // both the absent key (None) and an explicit empty object (Some(empty)).
        platform_extensions: r.platform_extensions.clone(),
        extra: r.extra.clone(),
    }
}

/// Apply the legacy cloud-routing migration to a [`ModeRecord`] in place,
/// canonicalizing `cloud_accuracy_tier` and `cloud_post_processing_model`.
/// Call this on import when the source may carry legacy single-token values
/// (e.g. the Windows example's `"claudeHaiku"` / `"grokFast"`). Only rewrites a
/// field when it is present, so an absent field stays absent (round-trip safe).
pub fn migrate_mode_cloud_routing(m: &mut ModeRecord) {
    if let Some(tier) = m.cloud_accuracy_tier.as_deref() {
        m.cloud_accuracy_tier = Some(migrate_cloud_accuracy_tier(Some(tier)));
    }
    if let Some(pp) = m.cloud_post_processing_model.as_deref() {
        m.cloud_post_processing_model = Some(migrate_cloud_pp_model(Some(pp)));
    }
}

// ============================================================================
// Layer 2: macOS 7-category settings adapter
// ============================================================================

/// macOS's seven settings categories, mirroring `BackupSettings` in
/// `app/macos/hyperwhisper/Models/BackupModels.swift`. Each category is an open
/// JSON object (the platform serializes its native managers into these); the
/// adapter only reads/moves the cross-platform keys and treats the rest as
/// macOS-only payload.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MacosSettings {
    pub general: Value,
    pub audio: Value,
    pub storage: Value,
    pub text_output: Value,
    pub shortcuts: Value,
    pub ai_model: Value,
    pub advanced: Value,
}

/// Keys that promote from a macOS category into a universal category.
/// `(macos_category_accessor, macos_key, universal_category, universal_key)`.
/// Mirrors the Settings Mapping table in `shared-backup/CLAUDE.md`.
const MACOS_GENERAL_KEYS: &[&str] = &[
    "launchMinimized",
    "showRecordingWindow",
    "checkForUpdatesAutomatically",
    "enableErrorLogging",
];
const MACOS_TEXT_OUTPUT_KEYS: &[&str] = &[
    "pasteResultText",
    "removeFillerWords",
    "restoreClipboardAfterPaste",
    "hideFromClipboardHistory",
    "clipboardRestoreDelaySeconds",
    "autocapitalizeInsert",
    "storeWordTimestamps",
];

/// Map macOS's 7 settings categories → the universal 5 categories, returning the
/// universal [`SettingsRecord`]. Every macOS-only field (and the whole `audio`,
/// `aiModel`, `shortcuts` categories minus the few promoted keys) is parked in
/// `platform_extensions["macos"]["settings"]` so a mac→universal→mac trip loses
/// nothing.
///
/// Promotion rules (universal ← macOS):
/// - `general.{launchMinimized, showRecordingWindow, checkForUpdatesAutomatically, enableErrorLogging}` ← `general`
/// - `general.enableSoundEffects` ← `audio.enableSoundEffects`
/// - `textOutput.*` ← `textOutput`
/// - `storage.storeAsM4A` ← `storage.storeAsM4A`
/// - `storage.keepAudioFiles` ← `advanced.keepAudioFiles`
/// - `advanced.maxRecordingDuration` ← `advanced.maxRecordingDuration`
///
/// `existing_macos_ext` lets a caller fold the promoted settings into a macOS
/// extension blob that already holds other macOS-only data; pass `None` to start
/// fresh.
pub fn macos_settings_to_universal(
    macos: &MacosSettings,
    existing_macos_ext: Option<&Value>,
) -> SettingsRecord {
    // ---- universal.general ----
    let mut general = Map::new();
    copy_keys(&macos.general, MACOS_GENERAL_KEYS, &mut general);
    if let Some(v) = get(&macos.audio, "enableSoundEffects") {
        general.insert("enableSoundEffects".into(), v.clone());
    }

    // ---- universal.textOutput ----
    let mut text_output = Map::new();
    copy_keys(&macos.text_output, MACOS_TEXT_OUTPUT_KEYS, &mut text_output);

    // ---- universal.storage ----
    let mut storage = Map::new();
    if let Some(v) = get(&macos.storage, "storeAsM4A") {
        storage.insert("storeAsM4A".into(), v.clone());
    }
    if let Some(v) = get(&macos.advanced, "keepAudioFiles") {
        storage.insert("keepAudioFiles".into(), v.clone());
    }

    // ---- universal.advanced ----
    let mut advanced = Map::new();
    if let Some(v) = get(&macos.advanced, "maxRecordingDuration") {
        advanced.insert("maxRecordingDuration".into(), v.clone());
    }

    // ---- platformExtensions.macos.settings (category-keyed macOS-only payload) ----
    // Each macOS category contributes its own NESTED sub-object
    // (`settings.{audio,general,storage,advanced,shortcuts,aiModel}`) holding only
    // the macOS-only keys (promoted keys excluded). On import every key routes home
    // by its recorded category — no per-key allowlist that silently misroutes a
    // future macOS-only key into the wrong category (review #12). Empty categories
    // are omitted to keep the blob tidy.
    let mut macos_settings = Map::new();
    insert_category(
        &mut macos_settings,
        "audio",
        object_except(&macos.audio, &["enableSoundEffects"]),
    );
    insert_category(
        &mut macos_settings,
        "general",
        object_except(&macos.general, MACOS_GENERAL_KEYS),
    );
    insert_category(
        &mut macos_settings,
        "storage",
        object_except(&macos.storage, &["storeAsM4A"]),
    );
    insert_category(
        &mut macos_settings,
        "advanced",
        object_except(&macos.advanced, &["maxRecordingDuration", "keepAudioFiles"]),
    );
    // shortcuts + aiModel: wholly macOS-only, carried as whole sub-objects.
    insert_category(&mut macos_settings, "shortcuts", object_all(&macos.shortcuts));
    insert_category(&mut macos_settings, "aiModel", object_all(&macos.ai_model));

    // Fold into any existing macos extension object the caller passed.
    let mut macos_ext_obj = existing_macos_ext
        .and_then(|v| v.as_object().cloned())
        .unwrap_or_default();
    macos_ext_obj.insert("settings".into(), Value::Object(macos_settings));

    let mut platform_extensions: BTreeMap<String, Value> = BTreeMap::new();
    platform_extensions.insert("macos".into(), Value::Object(macos_ext_obj));

    SettingsRecord {
        general: non_empty(general),
        text_output: non_empty(text_output),
        storage: non_empty(storage),
        streaming: None, // macOS does not export the universal streaming block today.
        advanced: non_empty(advanced),
        platform_extensions,
        extra: BTreeMap::new(),
    }
}

/// The category-keyed export's category names (the nested sub-objects under
/// `platformExtensions.macos.settings`). Used to distinguish a nested
/// (category-keyed) blob from a legacy flat blob on import.
const MACOS_SETTINGS_CATEGORIES: &[&str] =
    &["audio", "general", "storage", "advanced", "shortcuts", "aiModel"];

/// Inverse of [`macos_settings_to_universal`]: reconstruct macOS's 7 categories
/// from a universal [`SettingsRecord`]. The promoted universal keys are written
/// back into their macOS home category; the `platformExtensions.macos.settings`
/// blob supplies every macOS-only field. When a key exists in both (it should
/// not), the macOS-extension value wins (it is the authoritative macOS copy).
///
/// Tolerates BOTH blob shapes so old and new v2 backups restore:
/// - **nested** (category-keyed, current export): each macOS-only key lives under
///   `settings.<category>.<key>` and routes home by its recorded category;
/// - **legacy flat** (the Wave-2 export): each macOS-only key lives directly at
///   `settings.<key>` and is routed home by the per-key owning-category map the
///   old export implied (unknown flat keys fall back to `aiModel`, matching the
///   legacy catch-all so a legacy round-trip is exact).
///
/// Nested wins: when a key is present in both a category sub-object and as a flat
/// sibling, the nested value is kept (the flat fallback never overwrites it).
pub fn universal_to_macos_settings(record: &SettingsRecord) -> MacosSettings {
    // Seed each macOS category from its recorded macOS-only sub-object under
    // `platformExtensions.macos.settings.<category>` (category-keyed export, H2),
    // then overlay the promoted universal keys into their macOS home category.
    // Routing by recorded category — not a per-key allowlist with a catch-all —
    // means a future macOS-only key round-trips into the correct category instead
    // of drifting into `aiModel` (review #12).
    let blob = record
        .platform_extensions
        .get("macos")
        .and_then(|v| v.get("settings"))
        .and_then(|v| v.as_object())
        .cloned()
        .unwrap_or_default();

    let mut general = sub_object(&blob, "general");
    let mut audio = sub_object(&blob, "audio");
    let mut storage = sub_object(&blob, "storage");
    let mut advanced = sub_object(&blob, "advanced");
    let mut shortcuts = sub_object(&blob, "shortcuts");
    let mut ai_model = sub_object(&blob, "aiModel");

    // Legacy flat fallback: any key in the blob that is NOT one of the nested
    // category sub-objects is a flat macOS-only setting from the Wave-2 export.
    // Route each home by its owning category; nested entries already populated
    // above win (`entry().or_insert`), so we never clobber the preferred shape.
    for (k, v) in &blob {
        if MACOS_SETTINGS_CATEGORIES.contains(&k.as_str()) {
            continue;
        }
        let home = match k.as_str() {
            "audioSampleRate" => &mut advanced, // lives in advanced on macOS
            "autoIncreaseMicVolume" | "mediaControlMode" | "soundTheme"
            | "soundEffectsVolume" => &mut audio,
            "launchAtLogin" | "showInDock" => &mut general,
            "filesyncEnabled" => &mut storage,
            "historyRetentionDays" => &mut advanced,
            "pushToTalkMode" | "pushToTalkDoublePressEnabled" | "quickCaptureEnabled"
            | "quickCaptureModeId" => &mut shortcuts,
            // aiModel-owned + unknown catch-all (matches the legacy export).
            _ => &mut ai_model,
        };
        home.entry(k.clone()).or_insert_with(|| v.clone());
    }

    let text_output = record
        .text_output
        .as_ref()
        .and_then(|v| v.as_object().cloned())
        .unwrap_or_default();

    // Promoted universal → macOS home category. (Disjoint from the macOS-only
    // sub-objects above, which excluded the promoted keys on export.)
    if let Some(g) = record.general.as_ref().and_then(|v| v.as_object()) {
        for k in MACOS_GENERAL_KEYS {
            if let Some(v) = g.get(*k) {
                general.insert((*k).into(), v.clone());
            }
        }
        if let Some(v) = g.get("enableSoundEffects") {
            audio.insert("enableSoundEffects".into(), v.clone());
        }
    }
    if let Some(s) = record.storage.as_ref().and_then(|v| v.as_object()) {
        if let Some(v) = s.get("storeAsM4A") {
            storage.insert("storeAsM4A".into(), v.clone());
        }
        if let Some(v) = s.get("keepAudioFiles") {
            advanced.insert("keepAudioFiles".into(), v.clone());
        }
    }
    if let Some(a) = record.advanced.as_ref().and_then(|v| v.as_object()) {
        if let Some(v) = a.get("maxRecordingDuration") {
            advanced.insert("maxRecordingDuration".into(), v.clone());
        }
    }

    MacosSettings {
        general: Value::Object(general),
        audio: Value::Object(audio),
        storage: Value::Object(storage),
        text_output: Value::Object(text_output),
        shortcuts: Value::Object(shortcuts),
        ai_model: Value::Object(ai_model),
        advanced: Value::Object(advanced),
    }
}

// ---- small JSON helpers ----

fn get<'a>(v: &'a Value, key: &str) -> Option<&'a Value> {
    v.as_object().and_then(|o| o.get(key))
}

fn copy_keys(src: &Value, keys: &[&str], dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for k in keys {
            if let Some(v) = o.get(*k) {
                dst.insert((*k).to_string(), v.clone());
            }
        }
    }
}

fn copy_except(src: &Value, skip: &[&str], dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for (k, v) in o {
            if !skip.contains(&k.as_str()) {
                dst.insert(k.clone(), v.clone());
            }
        }
    }
}

fn copy_all(src: &Value, dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for (k, v) in o {
            dst.insert(k.clone(), v.clone());
        }
    }
}

/// Object form of [`copy_except`]: every key of `src` except `skip`.
fn object_except(src: &Value, skip: &[&str]) -> Map<String, Value> {
    let mut m = Map::new();
    copy_except(src, skip, &mut m);
    m
}

/// Object form of [`copy_all`]: every key of `src`.
fn object_all(src: &Value) -> Map<String, Value> {
    let mut m = Map::new();
    copy_all(src, &mut m);
    m
}

/// Insert `cat` under `key` in `dst`, but only when `cat` is non-empty (keeps the
/// category-keyed extension blob free of empty `{}` sub-objects).
fn insert_category(dst: &mut Map<String, Value>, key: &str, cat: Map<String, Value>) {
    if !cat.is_empty() {
        dst.insert(key.to_string(), Value::Object(cat));
    }
}

/// Read a nested object sub-category from the macOS settings extension blob,
/// returning an empty map when the category is absent or not an object.
fn sub_object(blob: &Map<String, Value>, key: &str) -> Map<String, Value> {
    blob.get(key)
        .and_then(|v| v.as_object())
        .cloned()
        .unwrap_or_default()
}

fn non_empty(m: Map<String, Value>) -> Option<Value> {
    if m.is_empty() {
        None
    } else {
        Some(Value::Object(m))
    }
}

/// Build a fresh, minimal universal backup envelope (no settings/modes/vocab).
/// Convenience for callers assembling a backup from records.
pub fn empty_backup(
    export_date: impl Into<String>,
    app_version: impl Into<String>,
    platform: impl Into<String>,
) -> UniversalBackup {
    UniversalBackup {
        schema_version: crate::validate::UNIVERSAL_SCHEMA_VERSION,
        export_date: export_date.into(),
        app_version: app_version.into(),
        platform: platform.into(),
        settings: None,
        modes: None,
        vocabulary: None,
        api_keys: None,
        license_key: None,
        platform_extensions: None,
        extra: BTreeMap::new(),
    }
}
