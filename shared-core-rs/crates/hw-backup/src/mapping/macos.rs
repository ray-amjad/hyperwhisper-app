//! Layer 2: the macOS 7-category settings adapter
//! ([`macos_settings_to_universal`] / [`universal_to_macos_settings`]).

use super::helpers::{
    copy_keys, get, insert_category, non_empty, object_all, object_except, sub_object,
};
use crate::records::*;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;

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
pub(super) const MACOS_GENERAL_KEYS: &[&str] = &[
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
