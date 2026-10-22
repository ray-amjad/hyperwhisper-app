//! Layer 3: the Windows flat settings adapter
//! ([`windows_settings_to_universal`] / [`universal_to_windows_settings`]).

use super::helpers::non_empty;
use crate::records::*;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::BTreeMap;

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
/// `platformExtensions.macos` ([`object_except`](super::helpers::object_except)); copying that rule here would
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
/// PAIRS, not a bare `&[&str]` like [`MACOS_GENERAL_KEYS`](super::macos::MACOS_GENERAL_KEYS): macOS's native key
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

#[cfg(test)]
mod tests {
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
}
