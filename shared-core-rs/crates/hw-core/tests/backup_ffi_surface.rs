//! The parts of the backup UniFFI surface that `backup_vectors.rs` does not
//! reach: whole-document validation and normalisation, the error mapping every
//! head shows to the user, and the reject paths of the settings adapters.
//!
//! These cross the same exported boundary the apps do. A fault here is a fault
//! on every platform at once: a valid backup that validates as broken blocks
//! a restore, and a malformed one that slips through is applied half-read.

use std::path::PathBuf;

use serde_json::{json, Value};

use hyperwhisper_core::ffi_backup::{
    linux_settings_to_universal_settings_json, macos_settings_to_universal_settings_json,
    migrate_cloud_accuracy_tier, migrate_cloud_pp_model, normalize_backup_json,
    universal_settings_to_linux_settings_json, universal_settings_to_macos_settings_json,
    universal_settings_to_windows_settings_json, validate_backup_json,
    windows_settings_to_universal_settings_json, BackupError,
};

const EXAMPLES_DIR: &str = "../../../shared-backup/examples";

const EXAMPLES: &[&str] = &[
    "macos-export.hwbackup.json",
    "windows-export.hwbackup.json",
    "linux-export.hwbackup.json",
    "vocab-only.hwbackup.json",
];

fn example(name: &str) -> String {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join(EXAMPLES_DIR)
        .join(name);
    std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("could not read {}: {e}", path.display()))
}

fn parse_message(result: Result<String, BackupError>) -> String {
    match result {
        Err(BackupError::Parse { message }) => message,
        Err(other) => panic!("expected BackupError::Parse, got {other:?}"),
        Ok(json) => panic!("expected BackupError::Parse, got Ok({json})"),
    }
}

// ---- validate_backup_json -------------------------------------------------

#[test]
fn every_shipped_example_backup_validates_clean() {
    for name in EXAMPLES {
        let errors = validate_backup_json(example(name));
        let found: Vec<String> = errors
            .iter()
            .map(|e| format!("{}: {}", e.path, e.message))
            .collect();
        assert!(found.is_empty(), "{name} should be valid, got {found:?}");
    }
}

#[test]
fn validation_reports_every_fault_with_its_path_and_message() {
    let doc = json!({
        "schemaVersion": 1,
        "platform": "beos",
        "modes": [{ "id": "m1", "name": "Work" }, { "id": "m2" }],
        "vocabulary": [{ "word": "HyperWhisper" }]
    });
    let errors = validate_backup_json(doc.to_string());
    let mut found: Vec<(String, String)> =
        errors.into_iter().map(|e| (e.path, e.message)).collect();
    found.sort();

    let paths: Vec<&str> = found.iter().map(|(p, _)| p.as_str()).collect();
    assert_eq!(
        paths,
        vec![
            "appVersion",
            "exportDate",
            "modes[1].name",
            "platform",
            "schemaVersion",
            "vocabulary[0].id",
        ],
        "all six faults are reported, not only the first"
    );

    let message = |path: &str| {
        found
            .iter()
            .find(|(p, _)| p == path)
            .map(|(_, m)| m.as_str())
            .unwrap()
    };
    assert_eq!(message("schemaVersion"), "must be 2, got 1");
    assert_eq!(message("appVersion"), "required field is missing");
    assert!(
        message("platform").contains("\"beos\""),
        "the platform message names the rejected value: {}",
        message("platform")
    );
}

#[test]
fn unparseable_json_is_one_root_error_not_a_panic() {
    let errors = validate_backup_json("{ \"schemaVersion\": 2,".to_string());
    assert_eq!(errors.len(), 1);
    assert_eq!(errors[0].path, "$");
    assert!(
        errors[0].message.starts_with("invalid JSON: "),
        "got {}",
        errors[0].message
    );
}

// ---- normalize_backup_json ------------------------------------------------

fn without_nulls(value: Value) -> Value {
    match value {
        Value::Object(map) => Value::Object(
            map.into_iter()
                .filter(|(_, v)| !v.is_null())
                .map(|(k, v)| (k, without_nulls(v)))
                .collect(),
        ),
        Value::Array(items) => Value::Array(items.into_iter().map(without_nulls).collect()),
        other => other,
    }
}

#[test]
fn normalize_keeps_every_example_backup_intact_and_is_idempotent() {
    for name in EXAMPLES {
        let raw = example(name);
        let once = normalize_backup_json(raw.clone())
            .unwrap_or_else(|e| panic!("{name} failed to normalize: {e}"));

        // An explicit `null` and an absent key mean the same thing in a backup
        // (absent stays absent), so the only change a round trip may make is to
        // drop a null. Every real value must come back unchanged.
        let before = without_nulls(serde_json::from_str(&raw).unwrap());
        let after = without_nulls(serde_json::from_str(&once).unwrap());
        assert_eq!(
            after, before,
            "{name}: a round trip must not add, drop or change any non-null field"
        );

        let twice = normalize_backup_json(once.clone()).unwrap();
        assert_eq!(twice, once, "{name}: normalising twice changes nothing");
    }
}

#[test]
fn normalize_rejects_a_backup_with_a_mistyped_field() {
    let doc = json!({
        "schemaVersion": 2,
        "exportDate": "2026-01-01T00:00:00Z",
        "appVersion": "1.0.0",
        "platform": "macos",
        "modes": "not-an-array"
    });
    let message = parse_message(normalize_backup_json(doc.to_string()));
    assert!(message.contains("invalid type"), "got {message}");
}

// ---- BackupError: the message every head shows ----------------------------

#[test]
fn ffi_error_keeps_the_variant_and_the_text_of_the_core_error() {
    let cases = [
        hw_backup::BackupError::Parse("p".to_string()),
        hw_backup::BackupError::Serialize("s".to_string()),
        hw_backup::BackupError::Invalid("i".to_string()),
    ];
    for core in cases {
        let core_text = core.to_string();
        let ffi = BackupError::from(core);
        assert_eq!(
            ffi.to_string(),
            core_text,
            "the FFI Display must match the core's own message"
        );
    }

    assert!(matches!(
        BackupError::from(hw_backup::BackupError::Parse("p".into())),
        BackupError::Parse { message } if message == "p"
    ));
    assert!(matches!(
        BackupError::from(hw_backup::BackupError::Serialize("s".into())),
        BackupError::Serialize { message } if message == "s"
    ));
    assert!(matches!(
        BackupError::from(hw_backup::BackupError::Invalid("i".into())),
        BackupError::Invalid { message } if message == "i"
    ));
}

#[test]
fn a_normalize_failure_reads_as_a_parse_failure() {
    let err = normalize_backup_json("[]".to_string()).unwrap_err();
    assert!(
        err.to_string().starts_with("failed to parse backup JSON: "),
        "got {err}"
    );
}

// ---- settings adapters: reject paths --------------------------------------

#[test]
fn macos_settings_reject_bad_native_json_and_a_bad_extension_blob() {
    parse_message(macos_settings_to_universal_settings_json(
        "not json".to_string(),
        None,
    ));

    let macos = json!({
        "general": {}, "audio": {}, "storage": {}, "textOutput": {},
        "shortcuts": {}, "aiModel": {}, "advanced": {}
    })
    .to_string();
    // The native half is fine, so this proves the EXTENSION blob is parsed too
    // and a corrupt one is refused rather than dropped.
    assert!(macos_settings_to_universal_settings_json(macos.clone(), None).is_ok());
    parse_message(macos_settings_to_universal_settings_json(
        macos,
        Some("{ broken".to_string()),
    ));
}

#[test]
fn universal_to_macos_rejects_a_mistyped_record() {
    let message = parse_message(universal_settings_to_macos_settings_json(
        json!({ "general": {}, "platformExtensions": "x" }).to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");
}

#[test]
fn windows_settings_reject_a_mistyped_native_value() {
    let message = parse_message(windows_settings_to_universal_settings_json(
        json!({ "LaunchMinimized": "yes" }).to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");
}

#[test]
fn universal_to_windows_refuses_a_value_of_the_wrong_type() {
    // The universal block is free-form JSON, so a hand-edited backup can carry a
    // string where Windows stores a bool. It must be refused, not applied.
    let message = parse_message(universal_settings_to_windows_settings_json(
        json!({ "general": { "launchMinimized": "yes" } }).to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");

    // The same key with the right type goes through, so the refusal above is
    // about the type and not about the key.
    let ok: Value = serde_json::from_str(
        &universal_settings_to_windows_settings_json(
            json!({ "general": { "launchMinimized": true } }).to_string(),
        )
        .unwrap(),
    )
    .unwrap();
    assert_eq!(ok, json!({ "LaunchMinimized": true }));

    let message = parse_message(universal_settings_to_windows_settings_json(
        "\"general\"".to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");
}

#[test]
fn linux_settings_reject_a_store_that_is_not_an_object() {
    let message = parse_message(linux_settings_to_universal_settings_json(
        "[\"general.launchMinimized\"]".to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");
    let message = parse_message(universal_settings_to_linux_settings_json(
        "\"general\"".to_string(),
    ));
    assert!(message.contains("invalid type"), "got {message}");
}

// ---- storage-value migration ----------------------------------------------

#[test]
fn accuracy_tier_migration_folds_legacy_values_and_defaults_the_rest() {
    let cases: &[(Option<&str>, &str)] = &[
        (None, "deepgramNova3"),
        (Some("   "), "deepgramNova3"),
        (Some("no-such-tier"), "deepgramNova3"),
        (Some("ELEVENLABSSCRIBEV2"), "elevenLabsScribeV2"),
        (Some("googleChirp3"), "geminiTranscribe"),
        (Some(" Azure "), "azureMaiTranscribe"),
        (Some("medium"), "groqWhisper"),
    ];
    for (input, expected) in cases {
        assert_eq!(
            migrate_cloud_accuracy_tier(input.map(str::to_string)),
            *expected,
            "input {input:?}"
        );
    }
}

#[test]
fn pp_model_migration_qualifies_legacy_tokens_and_defaults_the_rest() {
    let cases: &[(Option<&str>, &str)] = &[
        (None, "grok:grok-4.3"),
        (Some(""), "grok:grok-4.3"),
        (Some("something-else"), "grok:grok-4.3"),
        (Some("claudeHaiku"), "anthropic:claude-haiku-4-5"),
        (Some("default"), "cerebras:gpt-oss-120b"),
        (Some("GROQ"), "groq:openai/gpt-oss-120b"),
        (Some("CEREBRAS:gpt-oss-120b"), "cerebras:gpt-oss-120b"),
    ];
    for (input, expected) in cases {
        assert_eq!(
            migrate_cloud_pp_model(input.map(str::to_string)),
            *expected,
            "input {input:?}"
        );
    }
}
