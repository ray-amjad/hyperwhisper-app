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
//! 3. **Windows settings adapter** ([`windows_settings_to_universal`] /
//!    [`universal_to_windows_settings`]) — Windows' settings are FLAT and
//!    PascalCase, and their names diverge from the universal ones, so this half
//!    is driven by `(native, universal)` PAIRS tables rather than the bare key
//!    lists macOS can use. It emits the five universal categories ONLY: the
//!    curated `platformExtensions.windows.settings` list stays native.
//!
//! 4. **Linux settings adapter** ([`linux_settings_to_universal`] /
//!    [`universal_to_linux_settings`]) — near-identity: `PortableSettingsService`'s
//!    dotted storage keys ARE the universal keys. The tables carry the export
//!    DEFAULTS as well, because Linux's export emits every shared key.
//!
//! Parity note: macOS is the verified platform. The Windows and Linux adapters
//! (3, 4) were ported from the shipping native mappers against
//! `shared-conformance/backup-vectors.json`, which captured those mappers'
//! answers before they were replaced (issue #277).

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

/// PRESENT-ONLY tier migration: `None` for a missing/blank source, so an absent
/// field stays absent and the CALLER keeps its own entity default. Mirrors
/// `UniversalBackupMapper.MigrateCloudAccuracyTierPresent`.
fn migrate_tier_present(value: Option<&str>) -> Option<String> {
    let v = value?;
    if v.trim().is_empty() {
        return None;
    }
    Some(migrate_cloud_accuracy_tier(Some(v)))
}

/// PRESENT-ONLY post-processing-model migration. See [`migrate_tier_present`].
fn migrate_pp_present(value: Option<&str>) -> Option<String> {
    let v = value?;
    if v.trim().is_empty() {
        return None;
    }
    Some(migrate_cloud_pp_model(Some(v)))
}

/// Read a string field from a JSON object, treating an explicit `null` and a
/// non-string value as absent (what `JsonNode`/`JsonElement` do on both heads).
fn str_field<'a>(obj: &'a Map<String, Value>, key: &str) -> Option<&'a str> {
    obj.get(key).and_then(Value::as_str)
}

/// Canonicalize the two cloud-ROUTING fields (`cloudAccuracyTier`,
/// `cloudPostProcessingModel`) of a wire-shaped universal mode object, IN PLACE.
///
/// `folded_accuracy_tier` is the tier the catalog's `cloudProvider` fold produced
/// (`hw_catalog::CloudSttCatalog::normalize_cloud_provider(...).accuracy_tier`).
/// It is passed in rather than computed here because `hw-backup` is deliberately
/// sans-catalog (`shared-core-rs/README.md`); `hw-core` composes the two.
///
/// # Precedence — ported from `UniversalBackupMapper.MapToMode`, which assigns
/// `CloudAccuracyTier` TWICE
///
/// The object initializer sets `folded ?? migrated(universal) ?? Mode default`,
/// and a post-initializer arm then reassigns UNCONDITIONALLY from
/// `migrated(universal.cloudAccuracyTier)` or, when `platformExtensions.windows`
/// is present, from `migrated(windowsExt.cloudAccuracyTier)`. Since the
/// present-only migration returns `None` only for a null/blank source, ANY
/// present tier overwrites the folded one; the folded tier survives only when
/// its arm's source is absent or blank. Collapsed:
///
/// - `platformExtensions.windows` present → `winExt ?? folded ?? universal`
/// - otherwise                            → `universal ?? folded`
///
/// and for `cloudPostProcessingModel` (never folded):
///
/// - `platformExtensions.windows` present → `winExt ?? universal`
/// - otherwise                            → `universal`
///
/// **Absent stays absent.** When a chain yields nothing the key is REMOVED, not
/// stamped with the core default — the caller applies its own entity default
/// afterwards. Writing `deepgramNova3` / `grok:grok-4.3` here would regress both
/// heads, whose shared native default pair is `elevenLabsScribeV2` /
/// `anthropic:claude-haiku-4-5`.
///
/// Non-object input is left untouched.
pub fn normalize_universal_mode_value(mode: &mut Value, folded_accuracy_tier: Option<&str>) {
    let Some(obj) = mode.as_object_mut() else {
        return;
    };

    // `platformExtensions.windows` is the second assignment's source when the
    // slice exists at all — mirroring `winExt != null` on Windows, which is
    // decided by the KEY's presence, not by whether the slice carries a tier.
    let win_ext = obj
        .get("platformExtensions")
        .and_then(Value::as_object)
        .and_then(|p| p.get("windows"))
        .and_then(Value::as_object)
        .cloned();

    let universal_tier = migrate_tier_present(str_field(obj, "cloudAccuracyTier"));
    let universal_pp = migrate_pp_present(str_field(obj, "cloudPostProcessingModel"));
    let folded = folded_accuracy_tier.map(str::to_string);

    let (tier, pp) = match win_ext {
        Some(ext) => {
            let win_tier = migrate_tier_present(str_field(&ext, "cloudAccuracyTier"));
            let win_pp = migrate_pp_present(str_field(&ext, "cloudPostProcessingModel"));
            (
                win_tier.or(folded).or(universal_tier),
                win_pp.or(universal_pp),
            )
        }
        None => (universal_tier.or(folded), universal_pp),
    };

    set_or_remove(obj, "cloudAccuracyTier", tier);
    set_or_remove(obj, "cloudPostProcessingModel", pp);
}

/// Write `value` at `key`, or REMOVE the key when there is nothing to write.
/// Removal (rather than a JSON `null`) is what keeps "absent stays absent" true
/// through a serialize/deserialize hop on either head.
fn set_or_remove(obj: &mut Map<String, Value>, key: &str, value: Option<String>) {
    match value {
        Some(v) => {
            obj.insert(key.to_string(), Value::String(v));
        }
        None => {
            obj.remove(key);
        }
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
    "shareAnonymousSpeedData",
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
/// - `general.{launchMinimized, showRecordingWindow, checkForUpdatesAutomatically, enableErrorLogging, shareAnonymousSpeedData}` ← `general`
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

// ============================================================================
// Layer 3: Windows flat settings adapter
// ============================================================================

/// Windows' native settings shape: FLAT and **PascalCase**, mirroring the
/// private `SettingsData` class in
/// `app/windows/HyperWhisper/Services/SettingsService.cs` — but only the subset
/// that is promoted to the universal `settings` block.
///
/// # Why only a subset — READ BEFORE ADDING A FIELD
///
/// This struct is deliberately NOT all of `SettingsData`. `SettingsData` also
/// holds `RecordingsFolder` (a real filesystem path), `LastSelectedMicrophone`
/// (a device name), `GettingStartedCompletedSteps` and
/// `LocalApiServerPersistedPort`. None of those belong in a `.hwbackup.json`,
/// which users share. The macOS adapter above parks every unpromoted key in
/// `platformExtensions.macos` ([`object_except`]); copying that rule here would
/// publish those four. Windows' `platformExtensions.windows.settings` is a
/// CURATED list (`WindowsSettingsExtensions`, `UniversalBackupModels.cs`) and
/// this adapter does not build it at all — it emits the five universal
/// categories and nothing else, so no unpromoted native key can reach the file
/// through this path.
///
/// # Naming
///
/// `settings.json` is PascalCase: `SettingsService.Save()` uses a plain
/// `JsonSerializerOptions { WriteIndented = true }` with no naming policy.
/// `UniversalBackupMapper.CamelCaseOptions` is a DIFFERENT serializer and does
/// not apply here. Two fields need an explicit rename because `rename_all`
/// lowercases the trailing acronym: `StoreAsM4A` and `TypingSpeedWPM`.
///
/// `StreamingShortcut` is a `KeyboardShortcut` on Windows, not a scalar. It
/// crosses this boundary as the PERSISTED STRING form
/// (`KeyboardShortcut.ToPersistedString()`); `FromPersistedString` stays native.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct WindowsSettings {
    // -- general --
    #[serde(skip_serializing_if = "Option::is_none")]
    pub launch_minimized: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_recording_window: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub check_for_updates_automatically: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enable_error_logging: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub share_anonymous_speed_data: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enable_sound_effects: Option<bool>,

    // -- textOutput --
    #[serde(skip_serializing_if = "Option::is_none")]
    pub auto_paste_enabled: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remove_filler_words: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub restore_clipboard_after_paste: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hide_from_clipboard_history: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub clipboard_restore_delay_seconds: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub autocapitalize_insert: Option<bool>,

    // -- storage --
    #[serde(rename = "StoreAsM4A", skip_serializing_if = "Option::is_none")]
    pub store_as_m4a: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub keep_audio_files: Option<bool>,

    // -- streaming -- SEVEN separately-named native properties, not four.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_enabled: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_provider: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_language: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_deepgram_model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_cloud_tier: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_fast_formatting: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub streaming_shortcut: Option<String>,

    // -- advanced --
    #[serde(rename = "TypingSpeedWPM", skip_serializing_if = "Option::is_none")]
    pub typing_speed_wpm: Option<i64>,
    /// SECONDS, like the universal key and like macOS's
    /// `maxRecordingDurationSeconds`. Windows caps it at
    /// [`WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS`] — see
    /// [`universal_to_windows_settings`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_recording_duration: Option<i64>,
}

/// Windows' hard recording/streaming safety ceiling, in seconds (20 minutes).
///
/// It mirrors `MainViewModel.MaxRecordingDuration = TimeSpan.FromMinutes(20)`,
/// which is both the default and the maximum. A backup file may LOWER the cap
/// but must never raise it: `verify_recording_runaway_guard.ps1` exists because
/// an unbounded recording is a real failure mode (disk fill, a session the user
/// forgot about), and an importable setting would otherwise let any shared
/// `.hwbackup.json` switch the guard off.
pub const WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS: i64 = 20 * 60;

/// The value macOS wrote as `advanced.maxRecordingDuration` before the setting
/// was ever exposed in its UI. macOS itself treats it as "unset" on import
/// (`BackupManager.swift`), and `shared-backup/examples/windows-export.hwbackup.json`
/// used to declare it. Windows mirrors the quirk rather than silently capping a
/// user's recordings at five minutes because of a value nobody chose.
const MACOS_UNSET_MAX_RECORDING_DURATION_SECS: i64 = 300;

/// `(native PascalCase key, universal camelCase key)`.
///
/// PAIRS, not a bare `&[&str]` like [`MACOS_GENERAL_KEYS`]: macOS's native key
/// names already equal the universal ones, Windows' do not
/// (`textOutput.pasteResultText` ← `AutoPasteEnabled`, and all six `Streaming*`).
/// Rows where the two halves happen to match are still written out in full so
/// the table reads as one thing.
pub const WINDOWS_GENERAL_PAIRS: &[(&str, &str)] = &[
    ("LaunchMinimized", "launchMinimized"),
    ("ShowRecordingWindow", "showRecordingWindow"),
    ("CheckForUpdatesAutomatically", "checkForUpdatesAutomatically"),
    ("EnableErrorLogging", "enableErrorLogging"),
    ("ShareAnonymousSpeedData", "shareAnonymousSpeedData"),
    ("EnableSoundEffects", "enableSoundEffects"),
];

pub const WINDOWS_TEXT_OUTPUT_PAIRS: &[(&str, &str)] = &[
    // THE rename: Windows calls it AutoPasteEnabled.
    ("AutoPasteEnabled", "pasteResultText"),
    ("RemoveFillerWords", "removeFillerWords"),
    ("RestoreClipboardAfterPaste", "restoreClipboardAfterPaste"),
    ("HideFromClipboardHistory", "hideFromClipboardHistory"),
    ("ClipboardRestoreDelaySeconds", "clipboardRestoreDelaySeconds"),
    ("AutocapitalizeInsert", "autocapitalizeInsert"),
    // `storeWordTimestamps` NEVER gets a row here: there is no Windows native
    // property and none is being added. It round-trips through the phase-3b
    // extension-data path instead.
];

pub const WINDOWS_STORAGE_PAIRS: &[(&str, &str)] = &[
    ("StoreAsM4A", "storeAsM4A"),
    // Added in phase 3a together with the native `SettingsData.KeepAudioFiles`.
    // Before it existed, a macOS or Linux backup's value was dropped here while
    // the Windows golden fixture already declared the key — #288's first named
    // fidelity bug.
    ("KeepAudioFiles", "keepAudioFiles"),
];

pub const WINDOWS_STREAMING_PAIRS: &[(&str, &str)] = &[
    ("StreamingEnabled", "enabled"),
    ("StreamingProvider", "provider"),
    ("StreamingLanguage", "language"),
    ("StreamingDeepgramModel", "deepgramModel"),
    // Live cloud tier for streaming dictation. The native side hands us the
    // SettingsService getter's answer, which is already clamped to the
    // live-eligible catalog set, so an unset install exports `deepgramNova3`
    // rather than a null.
    ("StreamingCloudTier", "cloudTier"),
    ("StreamingFastFormatting", "fastFormatting"),
    // Persisted-string form; KeyboardShortcut conversion stays native.
    ("StreamingShortcut", "shortcut"),
];

pub const WINDOWS_ADVANCED_PAIRS: &[(&str, &str)] = &[
    ("TypingSpeedWPM", "typingSpeedWPM"),
    // Added in phase 3a. The EXPORT direction is a plain table row; the IMPORT
    // direction runs one extra step (see `universal_to_windows_settings`),
    // because this key is a safety limit and cannot be restored verbatim.
    ("MaxRecordingDuration", "maxRecordingDuration"),
];

/// Every Windows pairs table, with the universal category each one feeds.
const WINDOWS_SECTIONS: &[(&str, &[(&str, &str)])] = &[
    ("general", WINDOWS_GENERAL_PAIRS),
    ("textOutput", WINDOWS_TEXT_OUTPUT_PAIRS),
    ("storage", WINDOWS_STORAGE_PAIRS),
    ("streaming", WINDOWS_STREAMING_PAIRS),
    ("advanced", WINDOWS_ADVANCED_PAIRS),
];

/// Windows native settings → the universal-v2 `settings` block.
///
/// PRESENT-ONLY: a native field that is `None` produces no universal key, and a
/// category with no keys is omitted entirely. Windows' own snapshot is always
/// complete, so in practice every category is emitted — but the rule matters for
/// a section-selective caller and keeps the adapter from inventing defaults.
///
/// Only the five universal categories are produced. `platformExtensions` is NOT
/// built here; see the [`WindowsSettings`] doc comment.
pub fn windows_settings_to_universal(windows: &WindowsSettings) -> UniversalSettings {
    // Serializing the struct gives exactly the present PascalCase keys, so the
    // tables are looked up against one authoritative flat view.
    let flat = match serde_json::to_value(windows) {
        Ok(Value::Object(o)) => o,
        _ => Map::new(),
    };

    let mut sections: BTreeMap<&str, Map<String, Value>> = BTreeMap::new();
    for (section, pairs) in WINDOWS_SECTIONS {
        let mut out = Map::new();
        for (native, universal) in *pairs {
            if let Some(v) = flat.get(*native) {
                out.insert((*universal).to_string(), v.clone());
            }
        }
        sections.insert(section, out);
    }

    UniversalSettings {
        general: non_empty(sections.remove("general").unwrap_or_default()),
        text_output: non_empty(sections.remove("textOutput").unwrap_or_default()),
        storage: non_empty(sections.remove("storage").unwrap_or_default()),
        streaming: non_empty(sections.remove("streaming").unwrap_or_default()),
        advanced: non_empty(sections.remove("advanced").unwrap_or_default()),
        extra: BTreeMap::new(),
    }
}

/// Inverse of [`windows_settings_to_universal`]: the universal-v2 `settings`
/// block → Windows native settings.
///
/// PRESENT-ONLY, and an explicit JSON `null` counts as absent — exactly what
/// `UniversalBackupMapper.ApplySettings`'s `HasValue` gates do today. A universal
/// key with no pairs row (`textOutput.storeWordTimestamps`, or any future key) is
/// DROPPED here; on Windows it survives instead through the unknown-key
/// passthrough (`SettingsData.BackupUnknownSettings`), which is native.
///
/// **Almost no value interpretation happens here.** The Windows setters
/// re-canonicalise `StreamingProvider`, collapse `StreamingDeepgramModel`,
/// re-parse `StreamingShortcut` and clamp `ClipboardRestoreDelaySeconds`; that is
/// a `SettingsService` responsibility and stays native. This function otherwise
/// only renames and regroups.
///
/// # The one exception: `advanced.maxRecordingDuration`
///
/// It is a SAFETY limit, so it is the one key a backup file must not be able to
/// set freely. Three rules, applied here so all three bindings answer the same
/// way and `shared-conformance/backup-vectors.json` can pin them:
///
/// | Universal value | Native `MaxRecordingDuration` |
/// |---|---|
/// | `300` (the macOS never-exposed default) | ABSENT — keep the live value, as macOS does |
/// | `<= 0` (macOS's "no limit") | ABSENT — Windows has no "off"; the guard always runs |
/// | `1..=1200` | the value |
/// | `> 1200` | `1200` — clamped to the 20-minute ceiling |
///
/// `SettingsService.MaxRecordingDurationSeconds` clamps again on the native side,
/// so a hand-edited `settings.json` cannot raise the ceiling either.
pub fn universal_to_windows_settings(
    record: &UniversalSettings,
) -> Result<WindowsSettings, serde_json::Error> {
    let categories = [
        ("general", record.general.as_ref()),
        ("textOutput", record.text_output.as_ref()),
        ("storage", record.storage.as_ref()),
        ("streaming", record.streaming.as_ref()),
        ("advanced", record.advanced.as_ref()),
    ];

    let mut flat = Map::new();
    for (section, pairs) in WINDOWS_SECTIONS {
        let Some(Some(obj)) = categories
            .iter()
            .find(|(name, _)| name == section)
            .map(|(_, v)| v.and_then(|v| v.as_object()))
        else {
            continue;
        };
        for (native, universal) in *pairs {
            match obj.get(*universal) {
                Some(Value::Null) | None => continue,
                Some(v) => {
                    flat.insert((*native).to_string(), v.clone());
                }
            }
        }
    }

    // The one non-rename step. Deliberately AFTER the table loop and keyed on the
    // NATIVE name, so it applies to whatever the table produced and cannot be
    // bypassed by a future second row feeding the same field.
    match flat.get("MaxRecordingDuration").and_then(Value::as_i64) {
        None => {}
        Some(MACOS_UNSET_MAX_RECORDING_DURATION_SECS) => {
            flat.remove("MaxRecordingDuration");
        }
        Some(secs) if secs <= 0 => {
            flat.remove("MaxRecordingDuration");
        }
        Some(secs) if secs > WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS => {
            flat.insert(
                "MaxRecordingDuration".to_string(),
                Value::from(WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS),
            );
        }
        Some(_) => {}
    }

    serde_json::from_value(Value::Object(flat))
}

// ============================================================================
// Layer 4: Linux flat settings adapter
// ============================================================================

/// The value `ApplicationBackupExport.BuildSharedSettings` emits for a key the
/// Linux settings store does not hold. Linux's export has NO present-only rule —
/// every shared key is emitted with its default, so a Linux `settings` block is
/// always complete (23 keys).
///
/// These are the BACKUP path's defaults, ported verbatim. They are not always
/// the live UI's: `SettingsViewModel` reads `streaming.provider` with a
/// `"deepgram"` default where the backup path uses `null`. That divergence
/// already ships; reproducing it here is deliberate, and collapsing it would be
/// a behaviour change outside this phase.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum LinuxSettingDefault {
    Bool(bool),
    Int(i64),
    Float(f64),
    Null,
}

impl LinuxSettingDefault {
    fn to_value(self) -> Value {
        match self {
            LinuxSettingDefault::Bool(b) => Value::Bool(b),
            LinuxSettingDefault::Int(i) => Value::from(i),
            LinuxSettingDefault::Float(f) => Value::from(f),
            LinuxSettingDefault::Null => Value::Null,
        }
    }
}

use LinuxSettingDefault::{Bool as B, Float as F, Int as I, Null as N};

/// `(native dotted storage key, universal camelCase key, export default)`.
///
/// The Linux half is a NEAR-IDENTITY map: `PortableSettingsService`'s keys ARE
/// the universal dotted keys, so nothing is renamed. The rows are still written
/// out in full rather than derived from `"{section}.{universal}"`, so the table
/// is greppable from either name and a future divergence has somewhere to live —
/// `linux_native_keys_are_the_dotted_universal_keys` asserts the identity holds
/// today.
///
/// A three-tuple rather than the Windows two-tuple because the Linux EXPORT
/// defaults are load-bearing (they are what makes an untouched profile emit all
/// 23 keys) and have nowhere else to live.
/// One row of a Linux pairs table: `(native dotted key, universal key, export
/// default)`.
pub type LinuxSettingPair = (&'static str, &'static str, LinuxSettingDefault);

pub const LINUX_GENERAL_PAIRS: &[LinuxSettingPair] = &[
    ("general.launchMinimized", "launchMinimized", B(false)),
    ("general.showRecordingWindow", "showRecordingWindow", B(true)),
    (
        "general.checkForUpdatesAutomatically",
        "checkForUpdatesAutomatically",
        B(true),
    ),
    ("general.enableErrorLogging", "enableErrorLogging", B(true)),
    (
        "general.shareAnonymousSpeedData",
        "shareAnonymousSpeedData",
        B(true),
    ),
    ("general.enableSoundEffects", "enableSoundEffects", B(true)),
];

pub const LINUX_TEXT_OUTPUT_PAIRS: &[LinuxSettingPair] = &[
    ("textOutput.pasteResultText", "pasteResultText", B(true)),
    ("textOutput.removeFillerWords", "removeFillerWords", B(true)),
    (
        "textOutput.restoreClipboardAfterPaste",
        "restoreClipboardAfterPaste",
        B(true),
    ),
    (
        "textOutput.hideFromClipboardHistory",
        "hideFromClipboardHistory",
        B(true),
    ),
    (
        "textOutput.clipboardRestoreDelaySeconds",
        "clipboardRestoreDelaySeconds",
        F(10.0),
    ),
    (
        "textOutput.autocapitalizeInsert",
        "autocapitalizeInsert",
        B(true),
    ),
    // Linux HAS this one; Windows has no native property for it (see
    // WINDOWS_TEXT_OUTPUT_PAIRS). That asymmetry is #288's named victim.
    (
        "textOutput.storeWordTimestamps",
        "storeWordTimestamps",
        B(true),
    ),
];

pub const LINUX_STORAGE_PAIRS: &[LinuxSettingPair] = &[
    ("storage.keepAudioFiles", "keepAudioFiles", B(true)),
    ("storage.storeAsM4A", "storeAsM4A", B(false)),
];

pub const LINUX_STREAMING_PAIRS: &[LinuxSettingPair] = &[
    ("streaming.enabled", "enabled", B(false)),
    // The five string keys default to an explicit JSON null, not to a value and
    // not to omission — a Linux→Linux round trip depends on the null surviving.
    //
    // `cloudTier` defaults to null here and to `deepgramNova3` on Windows on
    // PURPOSE: Linux reads the raw stored key, while the Windows property clamps
    // on read. Both reproduce their own shipping head, which is what the vectors
    // pin. Do not "harmonise" these two defaults.
    ("streaming.provider", "provider", N),
    ("streaming.language", "language", N),
    ("streaming.deepgramModel", "deepgramModel", N),
    ("streaming.cloudTier", "cloudTier", N),
    ("streaming.fastFormatting", "fastFormatting", B(false)),
    ("streaming.shortcut", "shortcut", N),
];

pub const LINUX_ADVANCED_PAIRS: &[LinuxSettingPair] = &[
    ("advanced.maxRecordingDuration", "maxRecordingDuration", I(3600)),
    ("advanced.typingSpeedWPM", "typingSpeedWPM", I(40)),
];

const LINUX_SECTIONS: &[(&str, &[LinuxSettingPair])] = &[
    ("general", LINUX_GENERAL_PAIRS),
    ("textOutput", LINUX_TEXT_OUTPUT_PAIRS),
    ("storage", LINUX_STORAGE_PAIRS),
    ("streaming", LINUX_STREAMING_PAIRS),
    ("advanced", LINUX_ADVANCED_PAIRS),
];

/// Linux native settings (the flat `PortableSettingsService` store) → the
/// universal-v2 `settings` block.
///
/// `native` may be the WHOLE settings store: every key without a pairs row is
/// ignored, so nothing Linux-only — and nothing device-local such as
/// `selectedModeId` — can reach the export through this path. The
/// `platformExtensions.linux.settings` blob is built natively by
/// `ApplicationBackupExport.ApplyLinuxSettings` and is deliberately NOT modelled
/// here: its defaults are already duplicated against the live UI defaults in
/// `ApplicationViewModels.cs`, and a Rust copy would be a third home.
///
/// Always complete: an absent key is emitted with its [`LinuxSettingDefault`].
pub fn linux_settings_to_universal(native: &Map<String, Value>) -> UniversalSettings {
    let mut sections: BTreeMap<&str, Map<String, Value>> = BTreeMap::new();
    for (section, pairs) in LINUX_SECTIONS {
        let mut out = Map::new();
        for (native_key, universal, default) in *pairs {
            let value = native
                .get(*native_key)
                .cloned()
                .unwrap_or_else(|| default.to_value());
            out.insert((*universal).to_string(), value);
        }
        sections.insert(section, out);
    }

    UniversalSettings {
        general: non_empty(sections.remove("general").unwrap_or_default()),
        text_output: non_empty(sections.remove("textOutput").unwrap_or_default()),
        storage: non_empty(sections.remove("storage").unwrap_or_default()),
        streaming: non_empty(sections.remove("streaming").unwrap_or_default()),
        advanced: non_empty(sections.remove("advanced").unwrap_or_default()),
        extra: BTreeMap::new(),
    }
}

/// Inverse of [`linux_settings_to_universal`]: the universal-v2 `settings` block
/// → the flat dotted keys `PortableSettingsService` stores.
///
/// PRESENT-ONLY and null-dropping, reproducing `ApplySharedSettings`/`CopyCategory`
/// exactly: the tables are a per-category ALLOWLIST, so an unknown key inside a
/// known category and a whole unknown category are both dropped, and an explicit
/// JSON `null` leaves the live value alone (`source[key] is { } value` is false
/// for a JSON null). That drop is the Linux half of the unknown-key gap #288
/// names; it is reproduced here, not fixed.
pub fn universal_to_linux_settings(record: &UniversalSettings) -> Map<String, Value> {
    let categories = [
        ("general", record.general.as_ref()),
        ("textOutput", record.text_output.as_ref()),
        ("storage", record.storage.as_ref()),
        ("streaming", record.streaming.as_ref()),
        ("advanced", record.advanced.as_ref()),
    ];

    let mut flat = Map::new();
    for (section, pairs) in LINUX_SECTIONS {
        let Some(Some(obj)) = categories
            .iter()
            .find(|(name, _)| name == section)
            .map(|(_, v)| v.and_then(|v| v.as_object()))
        else {
            continue;
        };
        for (native_key, universal, _) in *pairs {
            match obj.get(*universal) {
                Some(Value::Null) | None => continue,
                Some(v) => {
                    flat.insert((*native_key).to_string(), v.clone());
                }
            }
        }
    }
    flat
}

#[cfg(test)]
mod windows_linux_settings_tests {
    use super::*;
    use serde_json::json;

    fn universal(v: Value) -> UniversalSettings {
        serde_json::from_value(v).unwrap()
    }

    /// Guards table → struct drift: every native name in the five pairs tables
    /// must be a real `WindowsSettings` field, or the round trip silently
    /// swallows it.
    #[test]
    fn every_pairs_row_names_a_real_windows_settings_field() {
        let mut flat = Map::new();
        for (_, pairs) in WINDOWS_SECTIONS {
            for (native, _) in *pairs {
                // A bool is wrong for three of them, so type by name.
                let value = match *native {
                    "ClipboardRestoreDelaySeconds" => json!(1.5),
                    "TypingSpeedWPM" => json!(77),
                    "MaxRecordingDuration" => json!(600),
                    n if n.starts_with("Streaming")
                        && !matches!(n, "StreamingEnabled" | "StreamingFastFormatting") =>
                    {
                        json!("x")
                    }
                    _ => json!(true),
                };
                flat.insert((*native).to_string(), value);
            }
        }
        let parsed: WindowsSettings = serde_json::from_value(Value::Object(flat.clone())).unwrap();
        let round_tripped = serde_json::to_value(&parsed).unwrap();
        assert_eq!(
            round_tripped,
            Value::Object(flat),
            "a pairs-table native key does not exist on WindowsSettings"
        );
    }

    /// Guards struct → table drift: a new `WindowsSettings` field with no pairs
    /// row would be write-only. Phase 3a is what this was waiting for — it added
    /// `KeepAudioFiles` and `MaxRecordingDuration` on both sides at once.
    #[test]
    fn every_windows_settings_field_appears_in_exactly_one_pairs_table() {
        let full = WindowsSettings {
            launch_minimized: Some(true),
            show_recording_window: Some(true),
            check_for_updates_automatically: Some(true),
            enable_error_logging: Some(true),
            share_anonymous_speed_data: Some(true),
            enable_sound_effects: Some(true),
            auto_paste_enabled: Some(true),
            remove_filler_words: Some(true),
            restore_clipboard_after_paste: Some(true),
            hide_from_clipboard_history: Some(true),
            clipboard_restore_delay_seconds: Some(1.5),
            autocapitalize_insert: Some(true),
            store_as_m4a: Some(true),
            keep_audio_files: Some(true),
            streaming_enabled: Some(true),
            streaming_provider: Some("p".into()),
            streaming_language: Some("l".into()),
            streaming_deepgram_model: Some("m".into()),
            streaming_cloud_tier: Some("t".into()),
            streaming_fast_formatting: Some(true),
            streaming_shortcut: Some("s".into()),
            typing_speed_wpm: Some(77),
            max_recording_duration: Some(600),
        };
        let serialized = serde_json::to_value(&full).unwrap();
        let mut struct_keys: Vec<String> =
            serialized.as_object().unwrap().keys().cloned().collect();
        struct_keys.sort();

        let mut table_keys: Vec<String> = WINDOWS_SECTIONS
            .iter()
            .flat_map(|(_, pairs)| pairs.iter().map(|(native, _)| (*native).to_string()))
            .collect();
        table_keys.sort();

        assert_eq!(
            struct_keys, table_keys,
            "WindowsSettings fields and the pairs tables have drifted apart"
        );
        assert_eq!(
            struct_keys.len(),
            23,
            "Windows promotes 23 keys: 20, + KeepAudioFiles and MaxRecordingDuration from \
             phase 3a, + StreamingCloudTier from the catalog-v8 live tier picker"
        );
    }

    #[test]
    fn windows_export_renames_and_regroups() {
        let native: WindowsSettings = serde_json::from_value(json!({
            "LaunchMinimized": true,
            "AutoPasteEnabled": false,
            "ClipboardRestoreDelaySeconds": 2.5,
            "StoreAsM4A": true,
            "StreamingEnabled": true,
            "StreamingProvider": "deepgram",
            "StreamingDeepgramModel": "nova-3-medical",
            "StreamingShortcut": "Ctrl+Alt+Shift+F9",
            "TypingSpeedWPM": 95
        }))
        .unwrap();

        let out = serde_json::to_value(windows_settings_to_universal(&native)).unwrap();
        assert_eq!(out["general"], json!({ "launchMinimized": true }));
        assert_eq!(
            out["textOutput"],
            json!({ "pasteResultText": false, "clipboardRestoreDelaySeconds": 2.5 })
        );
        assert_eq!(out["storage"], json!({ "storeAsM4A": true }));
        assert_eq!(
            out["streaming"],
            json!({
                "enabled": true,
                "provider": "deepgram",
                "deepgramModel": "nova-3-medical",
                "shortcut": "Ctrl+Alt+Shift+F9"
            })
        );
        assert_eq!(out["advanced"], json!({ "typingSpeedWPM": 95 }));
    }

    #[test]
    fn windows_import_is_present_only_and_null_is_absent() {
        let out = universal_to_windows_settings(&universal(json!({
            "general": { "launchMinimized": true, "enableErrorLogging": null },
            "streaming": { "provider": "assemblyai" }
        })))
        .unwrap();
        let flat = serde_json::to_value(&out).unwrap();
        assert_eq!(
            flat,
            json!({ "LaunchMinimized": true, "StreamingProvider": "assemblyai" }),
            "an explicit null must behave exactly like an absent key"
        );
    }

    /// Phase 3a closed two of the three gaps. `storeWordTimestamps` stays dropped
    /// HERE on purpose: there is no Windows native property for it and none is
    /// being added, so it round-trips through the native unknown-key store
    /// instead of through this adapter.
    #[test]
    fn windows_import_now_carries_keep_audio_files_and_max_recording_duration() {
        let out = universal_to_windows_settings(&universal(json!({
            "textOutput": { "pasteResultText": true, "storeWordTimestamps": true },
            "storage": { "storeAsM4A": false, "keepAudioFiles": false },
            "advanced": { "typingSpeedWPM": 55, "maxRecordingDuration": 600 }
        })))
        .unwrap();
        let flat = serde_json::to_value(&out).unwrap();
        assert_eq!(
            flat,
            json!({
                "AutoPasteEnabled": true,
                "StoreAsM4A": false,
                "KeepAudioFiles": false,
                "TypingSpeedWPM": 55,
                "MaxRecordingDuration": 600
            }),
            "storeWordTimestamps is the only one of the three still dropped here"
        );
    }

    /// The safety rule, stated as a table so a future edit has to argue with it.
    #[test]
    fn windows_import_never_lets_a_backup_weaken_the_recording_guard() {
        fn imported(secs: Value) -> Option<i64> {
            universal_to_windows_settings(&universal(json!({
                "advanced": { "maxRecordingDuration": secs }
            })))
            .unwrap()
            .max_recording_duration
        }

        // In range: verbatim, including a LOWER cap — tightening is always allowed.
        assert_eq!(imported(json!(600)), Some(600));
        assert_eq!(imported(json!(1)), Some(1));
        assert_eq!(imported(json!(1200)), Some(1200));

        // Above the ceiling: clamped, never accepted. Linux's own default (3600)
        // is the realistic case, so a Linux -> Windows restore lands on 20 min.
        assert_eq!(imported(json!(1201)), Some(1200));
        assert_eq!(imported(json!(3600)), Some(1200));
        assert_eq!(imported(json!(i64::MAX)), Some(1200));

        // macOS's "no limit" must not disable the Windows guard; it reads as unset
        // so the live 20-minute value stands.
        assert_eq!(imported(json!(0)), None);
        assert_eq!(imported(json!(-1)), None);

        // macOS's never-exposed legacy default is not a user choice either.
        assert_eq!(imported(json!(300)), None);
    }

    #[test]
    fn windows_import_does_not_interpret_values() {
        // The setters' rewrites (provider fallback, deepgram-model collapse,
        // shortcut re-canonicalisation, delay clamp) are SettingsService's job.
        let out = universal_to_windows_settings(&universal(json!({
            "streaming": {
                "provider": "assemblyai",
                "deepgramModel": "nova-2-general",
                "shortcut": "shift+CONTROL+f9"
            },
            "textOutput": { "clipboardRestoreDelaySeconds": 900.0 }
        })))
        .unwrap();
        assert_eq!(out.streaming_provider.as_deref(), Some("assemblyai"));
        assert_eq!(out.streaming_deepgram_model.as_deref(), Some("nova-2-general"));
        assert_eq!(out.streaming_shortcut.as_deref(), Some("shift+CONTROL+f9"));
        assert_eq!(out.clipboard_restore_delay_seconds, Some(900.0));
    }

    #[test]
    fn windows_round_trip_is_lossless_for_every_promoted_key() {
        let native: WindowsSettings = serde_json::from_value(json!({
            "LaunchMinimized": true, "ShowRecordingWindow": false,
            "CheckForUpdatesAutomatically": false, "EnableErrorLogging": false,
            "ShareAnonymousSpeedData": false, "EnableSoundEffects": false,
            "AutoPasteEnabled": false, "RemoveFillerWords": false,
            "RestoreClipboardAfterPaste": false, "HideFromClipboardHistory": false,
            "ClipboardRestoreDelaySeconds": 2.5, "AutocapitalizeInsert": false,
            "StoreAsM4A": true, "KeepAudioFiles": false, "StreamingEnabled": true,
            "StreamingProvider": "deepgram", "StreamingLanguage": "de",
            "StreamingDeepgramModel": "nova-3-medical", "StreamingFastFormatting": false,
            "StreamingShortcut": "Ctrl+Alt+Shift+F9", "TypingSpeedWPM": 95,
            // Inside the ceiling, so the clamp is a no-op and the trip is lossless.
            "MaxRecordingDuration": 600
        }))
        .unwrap();
        let back = universal_to_windows_settings(&windows_settings_to_universal(&native)).unwrap();
        assert_eq!(back, native);
    }

    #[test]
    fn windows_adapter_emits_only_the_five_universal_categories() {
        // The privacy guard, asserted rather than assumed: this adapter has no
        // way to produce a platformExtensions slice at all.
        let native: WindowsSettings = serde_json::from_value(json!({ "LaunchMinimized": true }))
            .unwrap();
        let out = serde_json::to_value(windows_settings_to_universal(&native)).unwrap();
        let keys: Vec<&String> = out.as_object().unwrap().keys().collect();
        assert_eq!(keys, vec!["general"]);
        assert!(!out
            .as_object()
            .unwrap()
            .contains_key("platformExtensions"));
    }

    #[test]
    fn linux_native_keys_are_the_dotted_universal_keys() {
        for (section, pairs) in LINUX_SECTIONS {
            for (native, universal, _) in *pairs {
                assert_eq!(
                    *native,
                    format!("{section}.{universal}"),
                    "the Linux map is near-identity; a divergence needs a comment, not a silent row"
                );
            }
        }
        let count: usize = LINUX_SECTIONS.iter().map(|(_, p)| p.len()).sum();
        assert_eq!(
            count, 24,
            "Linux carries 24 shared keys today (23 + streaming.cloudTier)"
        );
    }

    #[test]
    fn linux_export_of_an_untouched_profile_emits_every_default() {
        let out = serde_json::to_value(linux_settings_to_universal(&Map::new())).unwrap();
        assert_eq!(
            out,
            json!({
                "general": {
                    "launchMinimized": false, "showRecordingWindow": true,
                    "checkForUpdatesAutomatically": true, "enableErrorLogging": true,
                    "shareAnonymousSpeedData": true, "enableSoundEffects": true
                },
                "textOutput": {
                    "pasteResultText": true, "removeFillerWords": true,
                    "restoreClipboardAfterPaste": true, "hideFromClipboardHistory": true,
                    "clipboardRestoreDelaySeconds": 10.0, "autocapitalizeInsert": true,
                    "storeWordTimestamps": true
                },
                "storage": { "keepAudioFiles": true, "storeAsM4A": false },
                "streaming": {
                    "enabled": false, "provider": null, "language": null,
                    "deepgramModel": null, "cloudTier": null,
                    "fastFormatting": false, "shortcut": null
                },
                "advanced": { "maxRecordingDuration": 3600, "typingSpeedWPM": 40 }
            })
        );
    }

    #[test]
    fn linux_export_ignores_every_key_without_a_pairs_row() {
        let mut native = Map::new();
        native.insert("general.launchMinimized".into(), json!(true));
        native.insert("selectedModeId".into(), json!("device-local"));
        native.insert("localWhisperBackend".into(), json!("cuda12"));
        native.insert("backup.platformExtensions".into(), json!({ "macos": {} }));

        let out = serde_json::to_value(linux_settings_to_universal(&native)).unwrap();
        assert_eq!(out["general"]["launchMinimized"], json!(true));
        let rendered = out.to_string();
        for leaked in ["selectedModeId", "device-local", "localWhisperBackend", "cuda12"] {
            assert!(
                !rendered.contains(leaked),
                "the whole settings store is passed in; only promoted keys may come out"
            );
        }
    }

    #[test]
    fn linux_import_drops_unknown_keys_and_keeps_live_values_on_null() {
        let out = universal_to_linux_settings(&universal(json!({
            "general": { "launchMinimized": true, "futureGeneralKey": 41 },
            "textOutput": { "storeWordTimestamps": false, "futureTextKey": "x" },
            "streaming": { "enabled": false, "provider": null },
            "futureSection": { "a": 1 }
        })));
        assert_eq!(
            Value::Object(out),
            json!({
                "general.launchMinimized": true,
                "textOutput.storeWordTimestamps": false,
                "streaming.enabled": false
            })
        );
    }

    #[test]
    fn linux_round_trip_is_lossless_for_every_shared_key() {
        let mut native = Map::new();
        for (_, pairs) in LINUX_SECTIONS {
            for (native_key, _, default) in *pairs {
                let seeded = match default {
                    LinuxSettingDefault::Bool(b) => json!(!b),
                    LinuxSettingDefault::Int(i) => json!(i + 1),
                    LinuxSettingDefault::Float(f) => json!(f + 1.0),
                    LinuxSettingDefault::Null => json!("seeded"),
                };
                native.insert((*native_key).to_string(), seeded);
            }
        }
        let back = universal_to_linux_settings(&linux_settings_to_universal(&native));
        assert_eq!(back, native);
    }
}
