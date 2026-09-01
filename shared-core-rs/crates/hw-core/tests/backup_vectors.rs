//! Conformance-vector tests for the shared backup adapters.
//!
//! `shared-conformance/backup-vectors.json` is the cross-platform source of
//! truth for what issue #277 moved into `hw-backup`: the universal-v2 mode
//! normalization and the three settings mapping halves. Swift and C# run the
//! same file through their own UniFFI bindings:
//!
//! - `app/macos/hyperwhisperTests/BackupConformanceVectorTests.swift`
//! - `app/macos/hyperwhisperTests/BackupTopLevelExtensionsTests.swift`
//! - `app/shared-dotnet/HyperWhisper.Backup.Application.Tests/Program.cs`
//! - `app/windows/HyperWhisper.SmokeTests/Program.cs`
//!
//! This is the Rust head, and it is the only one that calls the core with no
//! app around it. The other three drive their SHIPPING adapters, so a row that
//! passes there proves the head still agrees; a row that passes here proves the
//! core itself did not move. Both are needed — the vectors were captured from
//! the native code first (phase 1a/2a) precisely so the port could be diffed
//! against them.
//!
//! The functions under test are the exported UniFFI ones, not the `hw_backup`
//! internals, so this test crosses the same boundary the apps do.
//!
//! One group is NOT answerable here and is checked by the heads instead:
//! `unknownKeyRoundTrip`. Both of its kinds are a native STORE behaviour — the
//! Windows `SettingsData.BackupUnknownSettings` mirror and the macOS
//! `BackupManager` foreign-slice store. The core has no store, so it has no
//! answer to give. [`vector_groups_are_populated`] still pins the group's
//! existence and shape so it cannot be quietly emptied.

use std::collections::BTreeSet;
use std::path::PathBuf;

use serde_json::{Map, Value};

// The crate's `[lib] name` is `hyperwhisper_core` (it drives the artifact
// name), so that — not `hw_core` — is how an integration test imports it.
use hyperwhisper_core::ffi_backup::{
    linux_settings_to_universal_settings_json, macos_settings_to_universal_settings_json,
    normalize_universal_mode_json, universal_settings_to_linux_settings_json,
    universal_settings_to_macos_settings_json, universal_settings_to_windows_settings_json,
    windows_settings_to_universal_settings_json,
};

const VECTORS_PATH: &str = "../../../shared-conformance/backup-vectors.json";

fn vectors() -> Map<String, Value> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(VECTORS_PATH);
    let raw = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("could not read {}: {e}", path.display()));
    serde_json::from_str::<Value>(&raw)
        .unwrap_or_else(|e| panic!("{} is not valid JSON: {e}", path.display()))
        .as_object()
        .expect("the vectors document must be a JSON object")
        .clone()
}

fn rows(group: &str) -> Vec<Map<String, Value>> {
    let doc = vectors();
    let array = doc
        .get(group)
        .unwrap_or_else(|| panic!("backup-vectors.json has no `{group}` group"))
        .as_array()
        .unwrap_or_else(|| panic!("`{group}` must be an array"));
    assert!(!array.is_empty(), "`{group}` has no rows");
    array
        .iter()
        .map(|row| {
            row.as_object()
                .unwrap_or_else(|| panic!("every `{group}` row must be an object"))
                .clone()
        })
        .collect()
}

fn name(row: &Map<String, Value>) -> &str {
    row.get("name")
        .and_then(Value::as_str)
        .expect("every vector row must carry a `name`")
}

fn field<'a>(row: &'a Map<String, Value>, key: &str, label: &str) -> &'a Value {
    row.get(key)
        .unwrap_or_else(|| panic!("vector '{label}' is missing `{key}`"))
}

fn as_str(value: &Value, label: &str, what: &str) -> String {
    serde_json::to_string(value).unwrap_or_else(|e| panic!("vector '{label}': {what}: {e}"))
}

fn parse(json: &str, label: &str, what: &str) -> Value {
    serde_json::from_str(json)
        .unwrap_or_else(|e| panic!("vector '{label}': the core's {what} is not JSON: {e}"))
}

/// Rewrite every number in a JSON tree to its `f64` value.
///
/// A vector writes `clipboardRestoreDelaySeconds` as `10`, and the field is an
/// `f64` in the record, so the core re-serializes it as `10.0`.
/// `serde_json::Value` holds the integer and the float in different variants
/// and compares them unequal, which would make the vectors pin JSON FORMATTING
/// instead of values — and would make a hand-written `10` in a reviewed vector
/// row a test failure. .NET's `JsonNode.DeepEquals` and Swift's `NSNumber`
/// comparison are both representation-insensitive already; this is how the Rust
/// head reads the rows at the same altitude.
fn normalize_numbers(value: &Value) -> Value {
    match value {
        Value::Number(number) => number
            .as_f64()
            .and_then(serde_json::Number::from_f64)
            .map(Value::Number)
            .unwrap_or_else(|| value.clone()),
        Value::Array(items) => Value::Array(items.iter().map(normalize_numbers).collect()),
        Value::Object(map) => Value::Object(
            map.iter()
                .map(|(key, inner)| (key.clone(), normalize_numbers(inner)))
                .collect(),
        ),
        _ => value.clone(),
    }
}

/// Assert two JSON documents are equal by VALUE. `serde_json::Value` compares
/// objects as maps, so key order never matters, and [`normalize_numbers`]
/// removes the integer/float split; the vectors pin values, never formatting.
/// This mirrors `JsonNode.DeepEquals` on the .NET side.
fn assert_json_eq(label: &str, what: &str, expected: &Value, actual: &Value) {
    assert!(
        normalize_numbers(expected) == normalize_numbers(actual),
        "vector '{label}': {what} mismatch\n  expected {}\n  actual   {}",
        serde_json::to_string(expected).unwrap(),
        serde_json::to_string(actual).unwrap()
    );
}

/// Deep-merge `overlay` over `base`, the way every head is obliged to apply a
/// present-only settings result over its own live baseline before writing it
/// back (`BackupManager.currentSettingsBaseline()` → `deepMerged(over:)` on
/// macOS, and the same rule on Windows and Linux). Objects merge key by key;
/// any other value replaces outright.
fn deep_merge(base: &Value, overlay: &Value) -> Value {
    match (base, overlay) {
        (Value::Object(base_map), Value::Object(overlay_map)) => {
            let mut merged = base_map.clone();
            for (key, value) in overlay_map {
                let next = match merged.get(key) {
                    Some(existing) => deep_merge(existing, value),
                    None => value.clone(),
                };
                merged.insert(key.clone(), next);
            }
            Value::Object(merged)
        }
        _ => overlay.clone(),
    }
}

/// Every `section.key` path present in a settings tree, two levels deep — the
/// shape both the universal block and the flat Windows snapshot use.
fn leaf_paths(value: &Value) -> BTreeSet<String> {
    let mut paths = BTreeSet::new();
    let Some(map) = value.as_object() else {
        return paths;
    };
    for (section, contents) in map {
        match contents.as_object() {
            Some(inner) => {
                for key in inner.keys() {
                    paths.insert(format!("{section}.{key}"));
                }
            }
            None => {
                paths.insert(section.clone());
            }
        }
    }
    paths
}

// ---------------------------------------------------------------------------
// modeNormalization
// ---------------------------------------------------------------------------

/// The five cloud-routing fields a `modeNormalization` row pins, each with the
/// CALLER's default for an absent field.
///
/// `normalize_universal_mode_json` leaves an absent field absent on purpose —
/// the entity default belongs to the head, not to the core. Both heads take it
/// from the same place (`Mode`'s field initialisers on Windows, the matching
/// Linux entity), so the vectors record the post-default answer and this table
/// is how the core's output is read at that same altitude. A change here is a
/// change to what the apps ship, so it must be made in all three places at once.
const MODE_FIELDS: &[(&str, Option<&str>)] = &[
    ("cloudProvider", None),
    ("cloudTranscriptionModel", None),
    ("cloudTranscriptionDomain", None),
    ("cloudAccuracyTier", Some("elevenLabsScribeV2")),
    (
        "cloudPostProcessingModel",
        Some("anthropic:claude-haiku-4-5"),
    ),
];

#[test]
fn mode_normalization_rows() {
    let rows = rows("modeNormalization");
    let count = rows.len();

    for row in &rows {
        let label = name(row);
        let mode = field(row, "mode", label);

        // Phase 1b collapsed every recorded Windows/Linux drift row to a single
        // `expected`, because after the port there is one answer. A row that
        // grew a head-specific expectation again is a re-divergence, so it is
        // rejected here rather than silently read as one side's answer.
        assert!(
            !row.contains_key("expectedWindows") && !row.contains_key("expectedLinux"),
            "vector '{label}' carries a per-head expectation; the heads share one \
             normalizer since phase 1b, so a row must pin a single `expected`"
        );
        let expected = field(row, "expected", label)
            .as_object()
            .unwrap_or_else(|| panic!("vector '{label}': `expected` must be an object"));

        let normalized = normalize_universal_mode_json(as_str(mode, label, "mode"))
            .unwrap_or_else(|e| panic!("vector '{label}': the core rejected the mode: {e}"));
        let normalized = parse(&normalized, label, "normalized mode");
        let normalized = normalized
            .as_object()
            .unwrap_or_else(|| panic!("vector '{label}': the core returned a non-object"));

        for (key, caller_default) in MODE_FIELDS {
            assert!(
                expected.contains_key(*key),
                "vector '{label}' is missing the expected field '{key}'"
            );
            let want = expected[*key].as_str();
            // Absent OR explicitly null both mean "the core said nothing", so
            // the caller's own default is what the mode ends up carrying.
            let got = match normalized.get(*key) {
                Some(Value::Null) | None => *caller_default,
                Some(Value::String(s)) => Some(s.as_str()),
                Some(other) => panic!("vector '{label}': {key} came back as {other}, not a string"),
            };
            assert_eq!(
                want, got,
                "vector '{label}': {key} expected {want:?}, got {got:?}"
            );
        }

        // Every key the row did not pin must survive untouched. This is the
        // half of the contract an expectation table cannot state: the
        // normalizer canonicalises five fields and is not allowed to drop,
        // rename or invent anything else.
        let mode_object = mode
            .as_object()
            .unwrap_or_else(|| panic!("vector '{label}': `mode` must be an object"));
        for (key, value) in mode_object {
            if key == "name" || MODE_FIELDS.iter().any(|(pinned, _)| pinned == key) {
                continue;
            }
            assert_eq!(
                Some(value),
                normalized.get(key),
                "vector '{label}': the untouched key '{key}' did not survive normalization"
            );
        }
    }

    println!("Backup mode-normalization vectors: {count}/{count} rows matched the shared core.");
}

#[test]
fn mode_name_normalization_rows() {
    let rows = rows("modeNameNormalization");
    let count = rows.len();

    for row in &rows {
        let label = name(row);
        let mode = field(row, "mode", label);
        let expected = field(row, "expectedName", label)
            .as_str()
            .unwrap_or_else(|| panic!("vector '{label}': `expectedName` must be a string"));
        let normalized = normalize_universal_mode_json(as_str(mode, label, "mode"))
            .unwrap_or_else(|e| panic!("vector '{label}': the core rejected the mode: {e}"));
        let normalized = parse(&normalized, label, "normalized mode");
        assert_eq!(
            normalized.get("name").and_then(Value::as_str),
            Some(expected),
            "vector '{label}': normalized name mismatch"
        );
    }

    println!("Backup mode-name vectors: {count}/{count} rows matched the shared core.");
}

// ---------------------------------------------------------------------------
// windowsSettings
// ---------------------------------------------------------------------------

#[test]
fn windows_settings_rows() {
    let rows = rows("windowsSettings");
    let count = rows.len();

    for row in &rows {
        let label = name(row);
        let direction = field(row, "direction", label)
            .as_str()
            .unwrap_or_else(|| panic!("vector '{label}': `direction` must be a string"));

        match direction {
            "export" => {
                let native = field(row, "native", label);
                let produced =
                    windows_settings_to_universal_settings_json(as_str(native, label, "native"))
                        .unwrap_or_else(|e| {
                            panic!("vector '{label}': the core rejected the native block: {e}")
                        });
                let produced = parse(&produced, label, "universal settings block");
                assert_json_eq(
                    label,
                    "universal settings block",
                    field(row, "expectedUniversal", label),
                    &produced,
                );

                // `absentUniversalKeys` names the universal keys this head does
                // NOT promote. The point of the row is that they stay out of the
                // typed block — `textOutput.storeWordTimestamps` reaching it
                // would mean Windows grew a property it does not have, and the
                // unknown-key store that carries it today would be dead code.
                let produced_paths = leaf_paths(&produced);
                for absent in row
                    .get("absentUniversalKeys")
                    .and_then(Value::as_array)
                    .unwrap_or(&Vec::new())
                {
                    let path = absent.as_str().expect("absentUniversalKeys holds strings");
                    assert!(
                        !produced_paths.contains(path),
                        "vector '{label}': '{path}' is recorded as absent from the Windows \
                         export, but the core emitted it"
                    );
                }
            }
            "import" => {
                let universal = field(row, "universal", label);
                let produced = universal_settings_to_windows_settings_json(as_str(
                    universal,
                    label,
                    "universal",
                ))
                .unwrap_or_else(|e| {
                    panic!("vector '{label}': the core rejected the universal block: {e}")
                });
                let produced = parse(&produced, label, "native settings block");
                let produced = produced
                    .as_object()
                    .unwrap_or_else(|| panic!("vector '{label}': the core returned a non-object"));

                // The core is present-only, so the post-import native state is
                // its result deep-merged over the live baseline — checkable
                // here for every key the core actually decides.
                let expected_native = field(row, "expectedNative", label)
                    .as_object()
                    .unwrap_or_else(|| {
                        panic!("vector '{label}': `expectedNative` must be an object")
                    });
                let merged = deep_merge(
                    field(row, "baselineNative", label),
                    &Value::Object(produced.clone()),
                );
                let merged = merged.as_object().expect("deep_merge of two objects");

                // `setterRewrittenNativeKeys` names the keys whose FINAL value
                // is not the core's to give: the `SettingsService` setters own
                // the streaming-provider fallback, the deepgram-model collapse
                // and the shortcut re-canonicalisation, and the vectors record
                // the answer after those run. Every other key must land exactly.
                // Ten of the twelve rows declare no exemption at all, so this is
                // a narrow carve-out and not a general escape hatch.
                let empty = Vec::new();
                let rewritten: BTreeSet<&str> = row
                    .get("setterRewrittenNativeKeys")
                    .and_then(Value::as_array)
                    .unwrap_or(&empty)
                    .iter()
                    .map(|key| {
                        key.as_str()
                            .expect("setterRewrittenNativeKeys holds strings")
                    })
                    .collect();

                for key in &rewritten {
                    // The exemption covers the VALUE, never the key's presence:
                    // a mapping-table typo that stopped emitting the key would
                    // otherwise hide behind it.
                    assert!(
                        produced.contains_key(*key),
                        "vector '{label}': '{key}' is exempted as setter-rewritten, but the \
                         core did not emit it at all"
                    );
                }

                for (key, want) in expected_native {
                    if rewritten.contains(key.as_str()) {
                        continue;
                    }
                    let got = merged.get(key);
                    assert!(
                        got.map(normalize_numbers).as_ref() == Some(&normalize_numbers(want)),
                        "vector '{label}': native key '{key}' expected {want}, got {}",
                        got.map(ToString::to_string).unwrap_or("absent".to_string())
                    );
                }
                for key in produced.keys() {
                    assert!(
                        expected_native.contains_key(key),
                        "vector '{label}': the core emitted the native key '{key}', which the \
                         Windows settings snapshot does not carry"
                    );
                }

                // A universal key that is absent, or explicitly null, must
                // produce no native key at all — the rule that stops a partial
                // backup clobbering a live setting.
                if leaf_paths(universal).is_empty() {
                    assert!(
                        produced.is_empty(),
                        "vector '{label}': an empty universal block produced native keys {:?}",
                        produced.keys().collect::<Vec<_>>()
                    );
                }
            }
            other => panic!("vector '{label}': unknown direction '{other}'"),
        }
    }

    println!("Backup windows-settings vectors: {count}/{count} rows matched the shared core.");
}

// ---------------------------------------------------------------------------
// linuxSettings
// ---------------------------------------------------------------------------

#[test]
fn linux_settings_rows() {
    let rows = rows("linuxSettings");
    let count = rows.len();

    for row in &rows {
        let label = name(row);
        let direction = field(row, "direction", label)
            .as_str()
            .unwrap_or_else(|| panic!("vector '{label}': `direction` must be a string"));

        match direction {
            "export" => {
                let native = field(row, "native", label);
                let produced =
                    linux_settings_to_universal_settings_json(as_str(native, label, "native"))
                        .unwrap_or_else(|e| {
                            panic!("vector '{label}': the core rejected the native store: {e}")
                        });
                let produced = parse(&produced, label, "universal settings block");
                assert_json_eq(
                    label,
                    "universal settings block",
                    field(row, "expectedUniversal", label),
                    &produced,
                );
            }
            "import" => {
                let universal = field(row, "universal", label);
                let produced = universal_settings_to_linux_settings_json(as_str(
                    universal,
                    label,
                    "universal",
                ))
                .unwrap_or_else(|e| {
                    panic!("vector '{label}': the core rejected the universal block: {e}")
                });
                let produced = parse(&produced, label, "native settings store");

                // Linux renames nothing and its setters rewrite nothing, so the
                // post-import store IS the core's present-only result merged
                // over the live baseline. That makes `expectedNative` directly
                // checkable here, unlike the Windows half.
                let merged = deep_merge(field(row, "baselineNative", label), &produced);
                assert_json_eq(
                    label,
                    "native settings store after import",
                    field(row, "expectedNative", label),
                    &merged,
                );

                // `ignoredUniversalKeys` names the universal keys `CopyCategory`
                // drops — an unknown key inside a known category, and a whole
                // unknown category. The per-category allowlist is the thing
                // being pinned, so a key that started leaking through would be
                // a real widening of what a backup can write.
                let produced_keys: BTreeSet<&str> = produced
                    .as_object()
                    .map(|m| m.keys().map(String::as_str).collect())
                    .unwrap_or_default();
                for ignored in row
                    .get("ignoredUniversalKeys")
                    .and_then(Value::as_array)
                    .unwrap_or(&Vec::new())
                {
                    let path = ignored
                        .as_str()
                        .expect("ignoredUniversalKeys holds strings");
                    assert!(
                        !produced_keys.contains(path),
                        "vector '{label}': '{path}' is recorded as dropped by the per-category \
                         allowlist, but the core wrote it to the Linux store"
                    );
                }
            }
            other => panic!("vector '{label}': unknown direction '{other}'"),
        }
    }

    println!("Backup linux-settings vectors: {count}/{count} rows matched the shared core.");
}

// ---------------------------------------------------------------------------
// macosSettings
// ---------------------------------------------------------------------------

#[test]
fn macos_settings_rows() {
    let rows = rows("macosSettings");
    let count = rows.len();

    for row in &rows {
        let label = name(row);
        let direction = field(row, "direction", label)
            .as_str()
            .unwrap_or_else(|| panic!("vector '{label}': `direction` must be a string"));

        match direction {
            "toUniversal" => {
                let macos = field(row, "macos", label);
                let existing = row
                    .get("existingMacosExtension")
                    .filter(|value| !value.is_null())
                    .map(|value| as_str(value, label, "existingMacosExtension"));
                let produced = macos_settings_to_universal_settings_json(
                    as_str(macos, label, "macos"),
                    existing,
                )
                .unwrap_or_else(|e| {
                    panic!("vector '{label}': the core rejected the macOS categories: {e}")
                });
                assert_json_eq(
                    label,
                    "universal settings record",
                    field(row, "expectedUniversal", label),
                    &parse(&produced, label, "universal settings record"),
                );
            }
            "toMacos" => {
                let universal = field(row, "universal", label);
                let produced = universal_settings_to_macos_settings_json(as_str(
                    universal,
                    label,
                    "universal",
                ))
                .unwrap_or_else(|e| {
                    panic!("vector '{label}': the core rejected the universal record: {e}")
                });
                assert_json_eq(
                    label,
                    "macOS 7-category settings",
                    field(row, "expectedMacos", label),
                    &parse(&produced, label, "macOS 7-category settings"),
                );
            }
            other => panic!("vector '{label}': unknown direction '{other}'"),
        }
    }

    println!("Backup macos-settings vectors: {count}/{count} rows matched the shared core.");
}

// ---------------------------------------------------------------------------
// Coverage guard
// ---------------------------------------------------------------------------

/// The vectors are only a contract while they have rows in them. An empty group
/// makes every test above pass by doing nothing, and `unknownKeyRoundTrip` has
/// no core function to fail loudly the way the others would — so the shape of
/// the file is pinned here, in the one suite that reads all five groups.
#[test]
fn vector_groups_are_populated() {
    let doc = vectors();
    assert!(
        doc.get("description").and_then(Value::as_str).is_some(),
        "the vectors document must carry a `description` saying how it was captured"
    );

    for group in [
        "modeNormalization",
        "modeNameNormalization",
        "windowsSettings",
        "linuxSettings",
        "macosSettings",
        "unknownKeyRoundTrip",
    ] {
        for row in rows(group) {
            let label = name(&row);
            assert!(
                !label.is_empty(),
                "a `{group}` row has an empty `name`; the name is what a failure quotes"
            );
        }
    }

    // The mode rows are FROZEN (see the document's own description): they were
    // captured against the shipping native code and phase 1b's whole argument is
    // that the port reproduces them. Losing rows silently would shrink the
    // contract to whatever still passes.
    //
    // 132 → 134: catalog v8 (#331) added the `googlechirp3` and `chirp_3` tier
    // aliases, and the rows cover the alias table exhaustively. The same merge
    // re-derived seven existing rows onto `geminiTranscribe`; it removed none.
    assert_eq!(
        rows("modeNormalization").len(),
        134,
        "the frozen modeNormalization row count changed; a row may only be ADDED, and the \
         count here must be updated deliberately when one is"
    );

    // Both `unknownKeyRoundTrip` kinds are native-store behaviours with no core
    // function, so this suite cannot run them. Pin that every row names a head
    // that does — otherwise a row could be added here and run nowhere.
    for row in rows("unknownKeyRoundTrip") {
        let label = name(&row);
        let kind = field(&row, "kind", label)
            .as_str()
            .expect("`kind` must be a string");
        assert!(
            matches!(kind, "settingsUnknownKey" | "topLevelPlatformExtensions"),
            "vector '{label}': unknown kind '{kind}'; add its runner to a head suite first"
        );
        let heads = field(&row, "heads", label)
            .as_array()
            .expect("`heads` must be an array");
        assert!(
            !heads.is_empty(),
            "vector '{label}' names no head, so no suite runs it"
        );
        for head in heads {
            let head = head.as_str().expect("`heads` holds strings");
            assert!(
                matches!(head, "windows" | "linux" | "macos"),
                "vector '{label}': unknown head '{head}'"
            );
        }
    }
}
