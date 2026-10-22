//! Layer 4: the Linux flat settings adapter
//! ([`linux_settings_to_universal`] / [`universal_to_linux_settings`]).

use super::helpers::non_empty;
use crate::records::*;
use serde_json::{Map, Value};
use std::collections::BTreeMap;

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
mod tests {
    use super::*;
    use serde_json::json;

    fn universal(v: Value) -> UniversalSettings {
        serde_json::from_value(v).unwrap()
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
