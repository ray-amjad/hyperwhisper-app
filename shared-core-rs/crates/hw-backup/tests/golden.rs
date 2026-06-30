//! Golden parity tests for `hw-backup`.
//!
//! The core guarantee: parsing a real example fixture, mapping it to the
//! platform-neutral records, mapping it back, and re-serializing yields a JSON
//! document *semantically equal* to the original (key order and pretty-printing
//! aside). Comparison is done on `serde_json::Value`, which is order-independent
//! for objects, so this is a true content round-trip.
//!
//! Fixtures are the three shipped examples under `shared-backup/examples/`,
//! embedded at build time.

use hw_backup::{
    from_records, macos_settings_to_universal, migrate_mode_cloud_routing, parse_backup,
    to_records, universal_to_macos_settings, validate_str, validate_value, MacosSettings,
    SettingsRecord,
};
use serde_json::Value;

const MACOS_EXPORT: &str =
    include_str!("../../../../shared-backup/examples/macos-export.hwbackup.json");
const WINDOWS_EXPORT: &str =
    include_str!("../../../../shared-backup/examples/windows-export.hwbackup.json");
const VOCAB_ONLY: &str =
    include_str!("../../../../shared-backup/examples/vocab-only.hwbackup.json");

/// Recursively drop object keys whose value is JSON `null`. In this schema an
/// explicit `null` and an absent key are semantically identical — both platforms
/// decode them to nil/None (Swift `decodeIfPresent`, C# nullable). The Rust PODs
/// model optional fields as `Option<T>` with `skip_serializing_if`, so a `null`
/// in the fixture re-serializes as an absent key. Normalizing nulls away on both
/// sides makes the round-trip comparison test *semantic* equality, which is the
/// contract. (Arrays are walked but never have their own null elements here.)
fn strip_nulls(v: &Value) -> Value {
    match v {
        Value::Object(map) => Value::Object(
            map.iter()
                .filter(|(_, val)| !val.is_null())
                .map(|(k, val)| (k.clone(), strip_nulls(val)))
                .collect(),
        ),
        Value::Array(arr) => Value::Array(arr.iter().map(strip_nulls).collect()),
        other => other.clone(),
    }
}

/// parse → to_records → from_records → serialize, compared as `Value`.
fn assert_value_round_trip(fixture: &str, label: &str) {
    let original: Value = serde_json::from_str(fixture)
        .unwrap_or_else(|e| panic!("[{label}] fixture is not valid JSON: {e}"));

    let backup = parse_backup(fixture)
        .unwrap_or_else(|e| panic!("[{label}] parse_backup failed: {e}"));

    let records = to_records(&backup);
    let rebuilt = from_records(&records);

    let rebuilt_value: Value = serde_json::to_value(&rebuilt)
        .unwrap_or_else(|e| panic!("[{label}] re-serialize failed: {e}"));

    let original = strip_nulls(&original);
    let rebuilt_value = strip_nulls(&rebuilt_value);

    assert_eq!(
        original, rebuilt_value,
        "[{label}] round-trip changed the document.\n--- original ---\n{}\n--- rebuilt ---\n{}",
        serde_json::to_string_pretty(&original).unwrap(),
        serde_json::to_string_pretty(&rebuilt_value).unwrap(),
    );
}

#[test]
fn macos_fixture_round_trips() {
    assert_value_round_trip(MACOS_EXPORT, "macos-export");
}

#[test]
fn windows_fixture_round_trips() {
    assert_value_round_trip(WINDOWS_EXPORT, "windows-export");
}

#[test]
fn vocab_only_fixture_round_trips() {
    assert_value_round_trip(VOCAB_ONLY, "vocab-only");
}

/// Also assert that the struct-level POD round-trips (parse → struct → re-parse
/// of re-serialized struct yields an equal struct). Catches any asymmetry in the
/// serde attributes that a Value comparison might mask.
#[test]
fn struct_level_round_trip_all_fixtures() {
    for (fixture, label) in [
        (MACOS_EXPORT, "macos-export"),
        (WINDOWS_EXPORT, "windows-export"),
        (VOCAB_ONLY, "vocab-only"),
    ] {
        let b1 = parse_backup(fixture).unwrap();
        let s = hw_backup::serialize_backup(&b1).unwrap();
        let b2 = parse_backup(&s).unwrap();
        assert_eq!(b1, b2, "[{label}] struct round-trip mismatch");
    }
}

// ---------------------------------------------------------------------------
// Validation against the embedded schema (lightweight structural check)
// ---------------------------------------------------------------------------

#[test]
fn all_fixtures_pass_validation() {
    for (fixture, label) in [
        (MACOS_EXPORT, "macos-export"),
        (WINDOWS_EXPORT, "windows-export"),
        (VOCAB_ONLY, "vocab-only"),
    ] {
        let errs = validate_str(fixture);
        assert!(
            errs.is_empty(),
            "[{label}] expected valid, got errors: {errs:?}"
        );
    }
}

#[test]
fn legacy_v1_macos_file_is_rejected() {
    // A legacy macOS v1 file uses `version`, not `schemaVersion`.
    let legacy = r#"{
        "version": 1,
        "exportDate": "2026-01-01T00:00:00Z",
        "appVersion": "2.0.0"
    }"#;
    let v: Value = serde_json::from_str(legacy).unwrap();
    let errs = validate_value(&v);
    assert!(
        errs.iter().any(|e| e.path == "schemaVersion"),
        "legacy v1 file must be rejected for missing schemaVersion: {errs:?}"
    );
    assert!(errs.iter().any(|e| e.path == "platform"));
}

// ---------------------------------------------------------------------------
// Mode-level detail preserved through the records mapping
// ---------------------------------------------------------------------------

#[test]
fn windows_mode_platform_extensions_preserved() {
    let backup = parse_backup(WINDOWS_EXPORT).unwrap();
    let records = to_records(&backup);
    // First Windows mode carries a windows extension blob with enableScreenOCR.
    let hyper = &records.modes[0];
    assert_eq!(hyper.name, "Hyper");
    let win = hyper
        .platform_extensions
        .as_ref()
        .expect("mode platform_extensions present")
        .get("windows")
        .expect("windows ext present");
    assert_eq!(win["enableScreenOCR"], Value::Bool(true));
    assert_eq!(win["localEngine"], Value::String("whisper".into()));
}

#[test]
fn macos_top_level_extensions_preserved() {
    let backup = parse_backup(MACOS_EXPORT).unwrap();
    let records = to_records(&backup);
    let macos = records
        .platform_extensions
        .get("macos")
        .expect("macos ext present");
    // The macOS-only shortcut + general settings live here, category-keyed (H2).
    assert_eq!(macos["settings"]["shortcuts"]["pushToTalkMode"], Value::String("disabled".into()));
    assert_eq!(macos["settings"]["general"]["launchAtLogin"], Value::Bool(true));
}

// ---------------------------------------------------------------------------
// H3: a mode's FOREIGN platform slice survives the core records mapping
// ---------------------------------------------------------------------------

#[test]
fn mode_foreign_platform_slice_passes_through_core() {
    // Models the mac→v2→Windows→v2→mac retention check at the CORE layer: a mode
    // carrying BOTH a `macos` and a `windows` extension slice must keep both
    // through to_records → from_records (the core never drops the foreign slice;
    // platform persistence of it is H4). (supports review #13)
    let json = r#"{
        "schemaVersion": 2,
        "exportDate": "2026-01-01T00:00:00Z",
        "appVersion": "1.0",
        "platform": "macos",
        "modes": [
            {
                "id": "m1",
                "name": "Mixed",
                "platformExtensions": {
                    "macos": {"shortcutBlob": "abc"},
                    "windows": {"localEngine": "whisper", "enableScreenOCR": true}
                }
            }
        ]
    }"#;
    let backup = parse_backup(json).unwrap();
    let rebuilt = from_records(&to_records(&backup));
    let v = serde_json::to_value(&rebuilt).unwrap();
    let ext = &v["modes"][0]["platformExtensions"];
    assert_eq!(ext["macos"]["shortcutBlob"], Value::String("abc".into()));
    assert_eq!(ext["windows"]["localEngine"], Value::String("whisper".into()));
    assert_eq!(ext["windows"]["enableScreenOCR"], Value::Bool(true));
}

// ---------------------------------------------------------------------------
// H1: unknown keys survive a parse → re-serialize round-trip (serde flatten)
// ---------------------------------------------------------------------------

#[test]
fn unknown_keys_survive_round_trip() {
    // A future schema adds keys this core doesn't model — at the top level, inside
    // a settings category group, on a mode, and on a vocabulary item. All must
    // survive parse → re-serialize verbatim (review #10).
    let json = r#"{
        "schemaVersion": 2,
        "exportDate": "2026-01-01T00:00:00Z",
        "appVersion": "9.9",
        "platform": "macos",
        "futureTopLevelKey": {"nested": [1, 2, 3]},
        "settings": {
            "general": {"launchMinimized": true},
            "futureCategory": {"someKey": "someValue"}
        },
        "modes": [
            {"id": "m1", "name": "Mode 1", "futureModeKey": 42}
        ],
        "vocabulary": [
            {"id": "v1", "word": "Foo", "futureVocabKey": true}
        ]
    }"#;

    let backup = parse_backup(json).unwrap();
    let out = hw_backup::serialize_backup(&backup).unwrap();
    let reparsed: Value = serde_json::from_str(&out).unwrap();

    assert_eq!(reparsed["futureTopLevelKey"]["nested"], serde_json::json!([1, 2, 3]));
    assert_eq!(reparsed["settings"]["futureCategory"]["someKey"], Value::String("someValue".into()));
    assert_eq!(reparsed["modes"][0]["futureModeKey"], serde_json::json!(42));
    assert_eq!(reparsed["vocabulary"][0]["futureVocabKey"], Value::Bool(true));

    // And the unknown keys also survive the to_records → from_records projection.
    let rebuilt = from_records(&to_records(&backup));
    let rebuilt_value = serde_json::to_value(&rebuilt).unwrap();
    assert_eq!(rebuilt_value["futureTopLevelKey"]["nested"], serde_json::json!([1, 2, 3]));
    assert_eq!(rebuilt_value["settings"]["futureCategory"]["someKey"], Value::String("someValue".into()));
    assert_eq!(rebuilt_value["modes"][0]["futureModeKey"], serde_json::json!(42));
}

// ---------------------------------------------------------------------------
// H2: a future macOS-only key in any category round-trips home (no allowlist drift)
// ---------------------------------------------------------------------------

#[test]
fn macos_future_only_key_round_trips_to_correct_category() {
    // A NEW macOS-only key in each category — none of which the import code knows
    // about by name — must land back in its original category, not drift into
    // aiModel via a catch-all (review #12).
    let macos = MacosSettings {
        general: serde_json::json!({"launchMinimized": true, "futureGeneral": "g"}),
        audio: serde_json::json!({"enableSoundEffects": true, "futureAudio": "a"}),
        storage: serde_json::json!({"storeAsM4A": true, "futureStorage": "s"}),
        text_output: serde_json::json!({"pasteResultText": true}),
        shortcuts: serde_json::json!({"futureShortcut": "sc"}),
        ai_model: serde_json::json!({"futureAiModel": "ai"}),
        advanced: serde_json::json!({"maxRecordingDuration": 300, "futureAdvanced": "adv"}),
    };

    let record = macos_settings_to_universal(&macos, None);

    // Each future key is parked under its own category in the extension blob.
    let blob = &record.platform_extensions["macos"]["settings"];
    assert_eq!(blob["general"]["futureGeneral"], Value::String("g".into()));
    assert_eq!(blob["audio"]["futureAudio"], Value::String("a".into()));
    assert_eq!(blob["storage"]["futureStorage"], Value::String("s".into()));
    assert_eq!(blob["shortcuts"]["futureShortcut"], Value::String("sc".into()));
    assert_eq!(blob["aiModel"]["futureAiModel"], Value::String("ai".into()));
    assert_eq!(blob["advanced"]["futureAdvanced"], Value::String("adv".into()));

    // Inverse: every future key routes back to its original macOS category.
    let back = universal_to_macos_settings(&record);
    assert_eq!(back.general["futureGeneral"], Value::String("g".into()));
    assert_eq!(back.audio["futureAudio"], Value::String("a".into()));
    assert_eq!(back.storage["futureStorage"], Value::String("s".into()));
    assert_eq!(back.shortcuts["futureShortcut"], Value::String("sc".into()));
    assert_eq!(back.ai_model["futureAiModel"], Value::String("ai".into()));
    assert_eq!(back.advanced["futureAdvanced"], Value::String("adv".into()));
    // Promoted keys still land in their home categories.
    assert_eq!(back.general["launchMinimized"], Value::Bool(true));
    assert_eq!(back.audio["enableSoundEffects"], Value::Bool(true));
    assert_eq!(back.storage["storeAsM4A"], Value::Bool(true));
    assert_eq!(back.advanced["maxRecordingDuration"], serde_json::json!(300));
}

// ---------------------------------------------------------------------------
// defaultModelByMode (aiModel) survives a category-keyed round-trip
// ---------------------------------------------------------------------------

#[test]
fn macos_default_model_by_mode_round_trips() {
    // `defaultModelByMode` is a macOS-only key in the aiModel category. With the
    // category-keyed export it is parked under settings.aiModel and must come back
    // from the SAME nested location through a mac → universal → mac round-trip
    // (Finding 1: importer must not read it from the old flat path).
    let by_mode = serde_json::json!({
        "mode-a": "large-v3-turbo",
        "mode-b": "parakeet-v2"
    });
    let macos = MacosSettings {
        general: serde_json::json!({"launchMinimized": true}),
        audio: serde_json::json!({"enableSoundEffects": true}),
        storage: serde_json::json!({}),
        text_output: serde_json::json!({}),
        shortcuts: serde_json::json!({}),
        ai_model: serde_json::json!({
            "defaultTranscriptionModel": "large-v3-turbo",
            "defaultModelByMode": by_mode
        }),
        advanced: serde_json::json!({}),
    };

    let record = macos_settings_to_universal(&macos, None);

    // Exported under the nested aiModel category (NOT a flat settings.* key).
    let blob = &record.platform_extensions["macos"]["settings"];
    assert_eq!(blob["aiModel"]["defaultModelByMode"], by_mode);
    assert!(blob.get("defaultModelByMode").is_none());

    // Inverse: read back from the same nested aiModel location, value preserved.
    let back = universal_to_macos_settings(&record);
    assert_eq!(back.ai_model["defaultModelByMode"], by_mode);
    assert_eq!(
        back.ai_model["defaultTranscriptionModel"],
        Value::String("large-v3-turbo".into())
    );
}

// ---------------------------------------------------------------------------
// Legacy FLAT extension blob (Wave-2 export) still imports (Finding 2)
// ---------------------------------------------------------------------------

#[test]
fn legacy_flat_macos_settings_blob_imports() {
    // A v2 backup produced by the PREVIOUS (flat) adapter parks every macOS-only
    // key directly at `platformExtensions.macos.settings.*` (no category nesting).
    // The current importer must STILL route each flat key home by its owning
    // category instead of silently ignoring it.
    let record = SettingsRecord {
        general: Some(serde_json::json!({"launchMinimized": false})),
        text_output: Some(serde_json::json!({"pasteResultText": true})),
        storage: Some(serde_json::json!({"storeAsM4A": true})),
        streaming: None,
        advanced: Some(serde_json::json!({"maxRecordingDuration": 300})),
        platform_extensions: [(
            "macos".to_string(),
            serde_json::json!({
                "settings": {
                    "launchAtLogin": true,
                    "showInDock": false,
                    "autoIncreaseMicVolume": true,
                    "soundTheme": "default",
                    "filesyncEnabled": false,
                    "audioSampleRate": 16000.0,
                    "historyRetentionDays": 30,
                    "pushToTalkMode": "disabled",
                    "pushToTalkDoublePressEnabled": false,
                    "defaultTranscriptionModel": "large-v3-turbo",
                    "defaultModelByMode": {"mode-a": "parakeet-v2"},
                    "someFutureFlatKey": "x"
                }
            }),
        )]
        .into_iter()
        .collect(),
        extra: Default::default(),
    };

    let back = universal_to_macos_settings(&record);

    // Flat macOS-only keys routed to their correct categories.
    assert_eq!(back.general["launchAtLogin"], Value::Bool(true));
    assert_eq!(back.general["showInDock"], Value::Bool(false));
    assert_eq!(back.audio["autoIncreaseMicVolume"], Value::Bool(true));
    assert_eq!(back.audio["soundTheme"], Value::String("default".into()));
    assert_eq!(back.storage["filesyncEnabled"], Value::Bool(false));
    assert_eq!(back.advanced["audioSampleRate"], serde_json::json!(16000.0));
    assert_eq!(back.advanced["historyRetentionDays"], serde_json::json!(30));
    assert_eq!(back.shortcuts["pushToTalkMode"], Value::String("disabled".into()));
    assert_eq!(back.shortcuts["pushToTalkDoublePressEnabled"], Value::Bool(false));
    assert_eq!(
        back.ai_model["defaultTranscriptionModel"],
        Value::String("large-v3-turbo".into())
    );
    assert_eq!(
        back.ai_model["defaultModelByMode"],
        serde_json::json!({"mode-a": "parakeet-v2"})
    );
    // Unknown flat key falls back to aiModel (legacy catch-all), not dropped.
    assert_eq!(back.ai_model["someFutureFlatKey"], Value::String("x".into()));

    // Promoted universal keys still land in their macOS home categories.
    assert_eq!(back.general["launchMinimized"], Value::Bool(false));
    assert_eq!(back.storage["storeAsM4A"], Value::Bool(true));
    assert_eq!(back.advanced["maxRecordingDuration"], serde_json::json!(300));
    assert_eq!(back.text_output["pasteResultText"], Value::Bool(true));
}

#[test]
fn nested_category_wins_over_flat_sibling() {
    // A malformed blob carrying BOTH a nested category value and a flat sibling of
    // the same key: the nested (preferred) value must win; the flat one is ignored.
    let record = SettingsRecord {
        general: None,
        text_output: None,
        storage: None,
        streaming: None,
        advanced: None,
        platform_extensions: [(
            "macos".to_string(),
            serde_json::json!({
                "settings": {
                    "shortcuts": {"pushToTalkMode": "nested-wins"},
                    "pushToTalkMode": "flat-loses"
                }
            }),
        )]
        .into_iter()
        .collect(),
        extra: Default::default(),
    };

    let back = universal_to_macos_settings(&record);
    assert_eq!(
        back.shortcuts["pushToTalkMode"],
        Value::String("nested-wins".into())
    );
}

// ---------------------------------------------------------------------------
// Legacy cloud-routing migration on import
// ---------------------------------------------------------------------------

#[test]
fn windows_legacy_cloud_routing_migrates() {
    // The Windows fixture has legacy single-token values: claudeHaiku, grokFast,
    // cerebrasGptOss120B, and tiers deepgramNova3/grokStt/azureMaiTranscribe.
    let backup = parse_backup(WINDOWS_EXPORT).unwrap();
    let mut records = to_records(&backup);
    for m in &mut records.modes {
        migrate_mode_cloud_routing(m);
    }

    // Hyper: cloudPostProcessingModel "claudeHaiku" → "anthropic:claude-haiku-4-5".
    assert_eq!(
        records.modes[0].cloud_post_processing_model.as_deref(),
        Some("anthropic:claude-haiku-4-5")
    );
    assert_eq!(
        records.modes[0].cloud_accuracy_tier.as_deref(),
        Some("deepgramNova3")
    );
    // Email: "grokFast" → "grok:grok-4.3"; tier grokStt stays canonical.
    assert_eq!(
        records.modes[1].cloud_post_processing_model.as_deref(),
        Some("grok:grok-4.3")
    );
    assert_eq!(records.modes[1].cloud_accuracy_tier.as_deref(), Some("grokStt"));
    // Local Cleanup: "cerebrasGptOss120B" → "cerebras:gpt-oss-120b".
    assert_eq!(
        records.modes[2].cloud_post_processing_model.as_deref(),
        Some("cerebras:gpt-oss-120b")
    );
    assert_eq!(
        records.modes[2].cloud_accuracy_tier.as_deref(),
        Some("azureMaiTranscribe")
    );
}

// ---------------------------------------------------------------------------
// macOS 7 → universal 5 settings adapter (the path that ADDS v2)
// ---------------------------------------------------------------------------

/// Build a representative macOS 7-category settings POD that matches the macOS
/// example fixture's intent, run it through the adapter, and assert the universal
/// projection + the macOS extension blob, then invert and confirm round-trip of
/// every macOS-owned key.
#[test]
fn macos_settings_adapter_promotes_and_preserves() {
    let macos = MacosSettings {
        general: serde_json::json!({
            "launchAtLogin": true,
            "showInDock": false,
            "launchMinimized": false,
            "showRecordingWindow": true,
            "checkForUpdatesAutomatically": true,
            "enableErrorLogging": true
        }),
        audio: serde_json::json!({
            "autoIncreaseMicVolume": true,
            "mediaControlMode": "pauseAndResume",
            "enableSoundEffects": true,
            "soundTheme": "default",
            "soundEffectsVolume": 0.8
        }),
        storage: serde_json::json!({
            "filesyncEnabled": false,
            "storeAsM4A": true
        }),
        text_output: serde_json::json!({
            "pasteResultText": true,
            "removeFillerWords": true,
            "restoreClipboardAfterPaste": true,
            "hideFromClipboardHistory": true,
            "clipboardRestoreDelaySeconds": 5.0,
            "autocapitalizeInsert": true,
            "storeWordTimestamps": true
        }),
        shortcuts: serde_json::json!({
            "pushToTalkMode": "disabled",
            "pushToTalkDoublePressEnabled": false,
            "quickCaptureEnabled": false,
            "quickCaptureModeId": ""
        }),
        ai_model: serde_json::json!({
            "showExperimentalModels": false,
            "defaultTranscriptionModel": "large-v3-turbo",
            "defaultLanguage": "en",
            "defaultModelByMode": {}
        }),
        advanced: serde_json::json!({
            "maxRecordingDuration": 300,
            "audioSampleRate": 16000.0,
            "keepAudioFiles": true,
            "historyRetentionDays": 30
        }),
    };

    let record = macos_settings_to_universal(&macos, None);

    // Universal general: promoted general keys + enableSoundEffects from audio.
    let g = record.general.as_ref().unwrap();
    assert_eq!(g["launchMinimized"], Value::Bool(false));
    assert_eq!(g["enableSoundEffects"], Value::Bool(true));
    // launchAtLogin / showInDock are macOS-only — NOT in universal general.
    assert!(g.get("launchAtLogin").is_none());

    // Universal storage: storeAsM4A (from storage) + keepAudioFiles (from advanced).
    let st = record.storage.as_ref().unwrap();
    assert_eq!(st["storeAsM4A"], Value::Bool(true));
    assert_eq!(st["keepAudioFiles"], Value::Bool(true));

    // Universal advanced: only maxRecordingDuration crosses over.
    let adv = record.advanced.as_ref().unwrap();
    assert_eq!(adv["maxRecordingDuration"], serde_json::json!(300));
    assert!(adv.get("audioSampleRate").is_none());
    assert!(adv.get("historyRetentionDays").is_none());

    // macOS extension blob holds every macOS-only setting, category-keyed (H2).
    let blob = &record.platform_extensions["macos"]["settings"];
    assert_eq!(blob["general"]["launchAtLogin"], Value::Bool(true));
    assert_eq!(blob["shortcuts"]["pushToTalkMode"], Value::String("disabled".into()));
    assert_eq!(blob["advanced"]["audioSampleRate"], serde_json::json!(16000.0));
    assert_eq!(blob["advanced"]["historyRetentionDays"], serde_json::json!(30));
    assert_eq!(blob["aiModel"]["defaultTranscriptionModel"], Value::String("large-v3-turbo".into()));
    assert_eq!(blob["storage"]["filesyncEnabled"], Value::Bool(false));
    // Promoted keys must NOT be duplicated into the blob (any category).
    assert!(blob["storage"].get("storeAsM4A").is_none());
    assert!(blob["audio"].get("enableSoundEffects").is_none());
    assert!(blob["advanced"].get("maxRecordingDuration").is_none());
    assert!(blob["general"].get("launchMinimized").is_none());

    // ---- inverse: universal → macOS 7 categories ----
    let back = universal_to_macos_settings(&record);

    assert_eq!(back.general["launchMinimized"], Value::Bool(false));
    assert_eq!(back.general["launchAtLogin"], Value::Bool(true));
    assert_eq!(back.general["showInDock"], Value::Bool(false));
    assert_eq!(back.audio["enableSoundEffects"], Value::Bool(true));
    assert_eq!(back.audio["soundTheme"], Value::String("default".into()));
    assert_eq!(back.audio["mediaControlMode"], Value::String("pauseAndResume".into()));
    assert_eq!(back.storage["storeAsM4A"], Value::Bool(true));
    assert_eq!(back.storage["filesyncEnabled"], Value::Bool(false));
    assert_eq!(back.advanced["maxRecordingDuration"], serde_json::json!(300));
    assert_eq!(back.advanced["keepAudioFiles"], Value::Bool(true));
    assert_eq!(back.advanced["audioSampleRate"], serde_json::json!(16000.0));
    assert_eq!(back.advanced["historyRetentionDays"], serde_json::json!(30));
    assert_eq!(back.shortcuts["pushToTalkMode"], Value::String("disabled".into()));
    assert_eq!(back.ai_model["defaultLanguage"], Value::String("en".into()));
    assert_eq!(back.text_output["pasteResultText"], Value::Bool(true));
}

/// The adapter's universal projection must match what the macОS example fixture
/// actually ships in its top-level `settings` block (the schema's intended map).
#[test]
fn macos_adapter_matches_fixture_universal_settings() {
    let fixture: Value = serde_json::from_str(MACOS_EXPORT).unwrap();
    let fixture_settings = &fixture["settings"];

    // Reconstruct the macOS 7-category POD from the fixture (universal settings +
    // the macos extension blob), then re-derive the universal projection.
    let macos = MacosSettings {
        general: serde_json::json!({
            "launchMinimized": fixture_settings["general"]["launchMinimized"],
            "showRecordingWindow": fixture_settings["general"]["showRecordingWindow"],
            "checkForUpdatesAutomatically": fixture_settings["general"]["checkForUpdatesAutomatically"],
            "enableErrorLogging": fixture_settings["general"]["enableErrorLogging"],
            "launchAtLogin": fixture["platformExtensions"]["macos"]["settings"]["general"]["launchAtLogin"],
            "showInDock": fixture["platformExtensions"]["macos"]["settings"]["general"]["showInDock"]
        }),
        audio: serde_json::json!({
            "enableSoundEffects": fixture_settings["general"]["enableSoundEffects"]
        }),
        storage: serde_json::json!({
            "storeAsM4A": fixture_settings["storage"]["storeAsM4A"]
        }),
        text_output: fixture_settings["textOutput"].clone(),
        shortcuts: serde_json::json!({}),
        ai_model: serde_json::json!({}),
        advanced: serde_json::json!({
            "maxRecordingDuration": fixture_settings["advanced"]["maxRecordingDuration"],
            "keepAudioFiles": fixture_settings["storage"]["keepAudioFiles"]
        }),
    };

    let record = macos_settings_to_universal(&macos, None);

    // general / textOutput / storage / advanced match the fixture's universal block.
    assert_eq!(record.general.as_ref().unwrap(), &fixture_settings["general"]);
    assert_eq!(
        record.text_output.as_ref().unwrap(),
        &fixture_settings["textOutput"]
    );
    assert_eq!(record.storage.as_ref().unwrap(), &fixture_settings["storage"]);
    assert_eq!(
        record.advanced.as_ref().unwrap(),
        &fixture_settings["advanced"]
    );
}
