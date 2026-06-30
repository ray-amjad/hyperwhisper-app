//! Lightweight structural validation of a universal-v2 backup document.
//!
//! ## Limitation (documented, by design)
//!
//! This is **not** a full JSON Schema validator. Pulling in a real validator
//! (`jsonschema`, `valico`, …) would add dependencies, which is forbidden here
//! (offline build, fixed `Cargo.lock`). Instead this performs a hand-rolled
//! structural check of the contract's load-bearing invariants:
//!
//! - the document is a JSON object,
//! - the four schema-`required` top-level fields are present
//!   (`schemaVersion`, `exportDate`, `appVersion`, `platform`),
//! - `schemaVersion` is exactly `2` (the schema `const`),
//! - `platform` is one of the enum values,
//! - `modes`/`vocabulary`, when present, are arrays, and each item carries the
//!   required `id` (+ `name` for modes / `word` for vocabulary).
//!
//! It does **not** enforce `additionalProperties: false`, string formats
//! (`uuid`, `date-time`), or per-field types beyond the checks above. Documents
//! it accepts are guaranteed parseable into [`crate::records::UniversalBackup`];
//! documents it rejects are guaranteed to violate a required-field/version
//! invariant. Anything stricter is the platform's responsibility.

use serde_json::Value;

/// A single validation failure, with a JSON-ish path to the offending field.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ValidationError {
    /// Dotted/indexed path, e.g. `modes[2].id`.
    pub path: String,
    /// Human-readable message.
    pub message: String,
}

impl std::fmt::Display for ValidationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.path, self.message)
    }
}

/// The schema's `schemaVersion` const.
pub const UNIVERSAL_SCHEMA_VERSION: i64 = 2;

/// The schema's `platform` enum.
pub const PLATFORM_ENUM: &[&str] = &["macos", "windows", "ios", "android"];

/// Validate a parsed JSON value against the embedded backup schema's structural
/// invariants. Returns every error found (not just the first) so a caller can
/// surface all problems at once. An empty vec means structurally valid.
pub fn validate_value(doc: &Value) -> Vec<ValidationError> {
    let mut errors = Vec::new();

    let obj = match doc.as_object() {
        Some(o) => o,
        None => {
            errors.push(ValidationError {
                path: "$".to_string(),
                message: "backup root must be a JSON object".to_string(),
            });
            return errors;
        }
    };

    // Required top-level fields.
    require_field(obj, "schemaVersion", &mut errors);
    require_field(obj, "exportDate", &mut errors);
    require_field(obj, "appVersion", &mut errors);
    require_field(obj, "platform", &mut errors);

    // schemaVersion == 2.
    if let Some(v) = obj.get("schemaVersion") {
        match v.as_i64() {
            Some(n) if n == UNIVERSAL_SCHEMA_VERSION => {}
            Some(n) => errors.push(ValidationError {
                path: "schemaVersion".to_string(),
                message: format!("must be {UNIVERSAL_SCHEMA_VERSION}, got {n}"),
            }),
            None => errors.push(ValidationError {
                path: "schemaVersion".to_string(),
                message: "must be an integer".to_string(),
            }),
        }
    }

    // platform enum.
    if let Some(v) = obj.get("platform") {
        match v.as_str() {
            Some(s) if PLATFORM_ENUM.contains(&s) => {}
            Some(s) => errors.push(ValidationError {
                path: "platform".to_string(),
                message: format!("must be one of {PLATFORM_ENUM:?}, got {s:?}"),
            }),
            None => errors.push(ValidationError {
                path: "platform".to_string(),
                message: "must be a string".to_string(),
            }),
        }
    }

    // modes: array of objects each with required id + name.
    if let Some(modes) = obj.get("modes") {
        check_array_items(modes, "modes", &["id", "name"], &mut errors);
    }

    // vocabulary: array of objects each with required id + word.
    if let Some(vocab) = obj.get("vocabulary") {
        check_array_items(vocab, "vocabulary", &["id", "word"], &mut errors);
    }

    errors
}

/// Convenience: validate a raw JSON string. A parse failure is reported as a
/// single `$` error.
pub fn validate_str(json: &str) -> Vec<ValidationError> {
    match serde_json::from_str::<Value>(json) {
        Ok(v) => validate_value(&v),
        Err(e) => vec![ValidationError {
            path: "$".to_string(),
            message: format!("invalid JSON: {e}"),
        }],
    }
}

fn require_field(
    obj: &serde_json::Map<String, Value>,
    key: &str,
    errors: &mut Vec<ValidationError>,
) {
    if !obj.contains_key(key) {
        errors.push(ValidationError {
            path: key.to_string(),
            message: "required field is missing".to_string(),
        });
    }
}

fn check_array_items(
    value: &Value,
    field: &str,
    required_keys: &[&str],
    errors: &mut Vec<ValidationError>,
) {
    let arr = match value.as_array() {
        Some(a) => a,
        None => {
            errors.push(ValidationError {
                path: field.to_string(),
                message: "must be an array".to_string(),
            });
            return;
        }
    };
    for (i, item) in arr.iter().enumerate() {
        let Some(item_obj) = item.as_object() else {
            errors.push(ValidationError {
                path: format!("{field}[{i}]"),
                message: "must be an object".to_string(),
            });
            continue;
        };
        for key in required_keys {
            if !item_obj.contains_key(*key) {
                errors.push(ValidationError {
                    path: format!("{field}[{i}].{key}"),
                    message: "required field is missing".to_string(),
                });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn minimal_valid_doc() {
        let doc = json!({
            "schemaVersion": 2,
            "exportDate": "2026-01-01T00:00:00Z",
            "appVersion": "1.0.0",
            "platform": "macos"
        });
        assert!(validate_value(&doc).is_empty());
    }

    #[test]
    fn rejects_non_object_root() {
        let errs = validate_value(&json!([1, 2, 3]));
        assert_eq!(errs.len(), 1);
        assert_eq!(errs[0].path, "$");
    }

    #[test]
    fn rejects_missing_required() {
        let errs = validate_value(&json!({ "schemaVersion": 2 }));
        let paths: Vec<_> = errs.iter().map(|e| e.path.as_str()).collect();
        assert!(paths.contains(&"exportDate"));
        assert!(paths.contains(&"appVersion"));
        assert!(paths.contains(&"platform"));
    }

    #[test]
    fn rejects_wrong_version() {
        // A legacy macOS v1 file has `version`, not `schemaVersion` → missing +,
        // if it had schemaVersion:1, version mismatch.
        let errs = validate_value(&json!({
            "schemaVersion": 1,
            "exportDate": "x", "appVersion": "x", "platform": "macos"
        }));
        assert!(errs.iter().any(|e| e.path == "schemaVersion"));
    }

    #[test]
    fn rejects_bad_platform() {
        let errs = validate_value(&json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "linux"
        }));
        assert!(errs.iter().any(|e| e.path == "platform"));
    }

    #[test]
    fn rejects_mode_without_id() {
        let errs = validate_value(&json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "macos",
            "modes": [ { "name": "X" } ]
        }));
        assert!(errs.iter().any(|e| e.path == "modes[0].id"));
    }

    #[test]
    fn rejects_vocab_without_word() {
        let errs = validate_value(&json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "macos",
            "vocabulary": [ { "id": "a" } ]
        }));
        assert!(errs.iter().any(|e| e.path == "vocabulary[0].word"));
    }
}
