//! Lightweight structural validation of a universal-v2 backup document.
//!
//! ## Limitation (documented, by design)
//!
//! This is **not** a full JSON Schema validator. Pulling in a real validator
//! (`jsonschema`, `valico`, …) would add dependencies, which is forbidden here
//! (offline build, fixed `Cargo.lock`). Instead this performs a hand-rolled
//! check of everything [`crate::records::UniversalBackup`] decodes:
//!
//! - the document is a JSON object,
//! - the four schema-`required` top-level fields are present
//!   (`schemaVersion`, `exportDate`, `appVersion`, `platform`),
//! - `schemaVersion` is exactly `2` (the schema `const`; `2.0` is the same
//!   number, so it passes too),
//! - `platform` is one of the enum values,
//! - every typed field of the root, of each `modes[]` item and of each
//!   `vocabulary[]` item has the type the parser decodes it as (the tables
//!   below: string, boolean, integer, object or array). Required fields
//!   (`exportDate`, `appVersion`; mode `id`/`name`; vocabulary `id`/`word`)
//!   must be present and non-null; optional fields may be absent or `null`,
//!   as the parser reads both as `None`. Integers are compared by value, so
//!   `1.0` passes and `1.5` does not, matching the parser.
//!
//! Untyped fields pass whatever they hold: the `settings` categories
//! (`general`, `textOutput`, …), the values inside `apiKeys` and
//! `platformExtensions`, and unknown keys (the parser keeps them in `extra`).
//!
//! [`validate_str`] also reports a typed field repeated in one object, which the
//! parser rejects as a `duplicate field`.
//!
//! It does **not** enforce `additionalProperties: false` or string formats
//! (`uuid`, `date-time`). Documents it accepts are guaranteed parseable into
//! [`crate::records::UniversalBackup`]; each error it returns names the path of
//! the field that would stop the parse or that breaks a required/version
//! invariant. Anything stricter is the platform's responsibility.

use crate::records::integral_i64;
use serde_json::{Map, Value};

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
pub const PLATFORM_ENUM: &[&str] = &["macos", "windows", "linux", "ios", "android"];

/// The JSON type a typed record field decodes from.
#[derive(Debug, Clone, Copy)]
enum Kind {
    String,
    Bool,
    /// A whole number, compared by value ([`integral_i64`]).
    Integer,
    Object,
    Array,
}

impl Kind {
    fn matches(self, v: &Value) -> bool {
        match self {
            Kind::String => v.is_string(),
            Kind::Bool => v.is_boolean(),
            Kind::Integer => integral_i64(v).is_some(),
            Kind::Object => v.is_object(),
            Kind::Array => v.is_array(),
        }
    }

    fn message(self) -> &'static str {
        match self {
            Kind::String => "must be a string",
            Kind::Bool => "must be a boolean",
            Kind::Integer => "must be an integer",
            Kind::Object => "must be an object",
            Kind::Array => "must be an array",
        }
    }
}

/// One typed field of a record: its JSON key, its type, and whether the parser
/// requires it (a plain field) or reads absent/`null` as `None` (an `Option`).
struct Field {
    key: &'static str,
    kind: Kind,
    required: bool,
}

const fn req(key: &'static str, kind: Kind) -> Field {
    Field {
        key,
        kind,
        required: true,
    }
}

const fn opt(key: &'static str, kind: Kind) -> Field {
    Field {
        key,
        kind,
        required: false,
    }
}

/// [`crate::records::UniversalBackup`]'s typed fields, less `schemaVersion` and
/// `platform` (checked by value in [`validate_value`]).
const ROOT_FIELDS: &[Field] = &[
    req("exportDate", Kind::String),
    req("appVersion", Kind::String),
    opt("settings", Kind::Object),
    opt("modes", Kind::Array),
    opt("vocabulary", Kind::Array),
    opt("apiKeys", Kind::Object),
    opt("licenseKey", Kind::String),
    opt("platformExtensions", Kind::Object),
];

/// [`crate::records::UniversalMode`]'s typed fields.
const MODE_FIELDS: &[Field] = &[
    req("id", Kind::String),
    req("name", Kind::String),
    opt("preset", Kind::String),
    opt("language", Kind::String),
    opt("model", Kind::String),
    opt("isDefault", Kind::Bool),
    opt("sortOrder", Kind::Integer),
    opt("punctuation", Kind::Bool),
    opt("capitalization", Kind::Bool),
    opt("profanityFilter", Kind::Bool),
    opt("removeTrailingPeriod", Kind::Bool),
    opt("englishSpelling", Kind::String),
    opt("cloudProvider", Kind::String),
    opt("cloudTranscriptionModel", Kind::String),
    opt("cloudTranscriptionDomain", Kind::String),
    opt("postProcessingMode", Kind::Integer),
    opt("postProcessingProvider", Kind::String),
    opt("languageModel", Kind::String),
    opt("localPostProcessingModel", Kind::String),
    opt("userSystemPrompt", Kind::String),
    opt("customInstructions", Kind::String),
    opt("geminiCustomPrompt", Kind::String),
    opt("cloudAccuracyTier", Kind::String),
    opt("cloudPostProcessingModel", Kind::String),
    opt("platformExtensions", Kind::Object),
];

/// [`crate::records::UniversalVocabularyItem`]'s typed fields.
const VOCABULARY_FIELDS: &[Field] = &[
    req("id", Kind::String),
    req("word", Kind::String),
    opt("replacement", Kind::String),
    opt("sortOrder", Kind::Integer),
    opt("source", Kind::String),
];

/// [`crate::records::UniversalSettings`]'s categories. Each is an untyped
/// `Value`, so only a duplicate of one matters (see [`validate_str`]).
const SETTINGS_KEYS: &[&str] = &["general", "textOutput", "storage", "streaming", "advanced"];

/// Validate a parsed JSON value against the embedded backup schema's structural
/// invariants and the record model's field types. Returns every error found
/// (not just the first) so a caller can surface all problems at once. An empty
/// vec means the document is valid and its JSON text will parse with
/// [`crate::parse_backup`], unless that text repeats a key: a [`Value`] keeps
/// only the last copy. [`validate_str`] checks the text for that too.
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

    // schemaVersion == 2.
    match obj.get("schemaVersion") {
        None => errors.push(missing("schemaVersion".to_string())),
        Some(v) => match integral_i64(v) {
            Some(n) if n == UNIVERSAL_SCHEMA_VERSION => {}
            Some(n) => errors.push(ValidationError {
                path: "schemaVersion".to_string(),
                message: format!("must be {UNIVERSAL_SCHEMA_VERSION}, got {n}"),
            }),
            None => errors.push(ValidationError {
                path: "schemaVersion".to_string(),
                message: "must be an integer".to_string(),
            }),
        },
    }

    // Root fields, in table order (`exportDate`, `appVersion`, sections…).
    check_fields(obj, "", ROOT_FIELDS, &mut errors);

    // platform enum.
    match obj.get("platform") {
        None => errors.push(missing("platform".to_string())),
        Some(v) => match v.as_str() {
            Some(s) if PLATFORM_ENUM.contains(&s) => {}
            Some(s) => errors.push(ValidationError {
                path: "platform".to_string(),
                message: format!("must be one of {PLATFORM_ENUM:?}, got {s:?}"),
            }),
            None => errors.push(ValidationError {
                path: "platform".to_string(),
                message: "must be a string".to_string(),
            }),
        },
    }

    // modes / vocabulary items. A non-array section was reported above.
    if let Some(Value::Array(modes)) = obj.get("modes") {
        check_items(modes, "modes", MODE_FIELDS, &mut errors);
    }
    if let Some(Value::Array(vocab)) = obj.get("vocabulary") {
        check_items(vocab, "vocabulary", VOCABULARY_FIELDS, &mut errors);
    }

    errors
}

/// Convenience: validate a raw JSON string. A parse failure is reported as a
/// single `$` error. Besides [`validate_value`]'s checks, this reports a typed
/// field that appears twice in one object (`modes[0].id: duplicate key`), which
/// [`crate::parse_backup`] rejects and a parsed [`Value`] cannot show. An empty
/// vec means [`crate::parse_backup`] will accept `json`.
pub fn validate_str(json: &str) -> Vec<ValidationError> {
    let mut errors = match serde_json::from_str::<Value>(json) {
        Ok(v) => validate_value(&v),
        Err(e) => {
            return vec![ValidationError {
                path: "$".to_string(),
                message: format!("invalid JSON: {e}"),
            }]
        }
    };
    let mut de = serde_json::Deserializer::from_str(json);
    let walk = DuplicateKeys {
        at: Record::Root,
        path: String::new(),
        errors: &mut errors,
    };
    // The text already parsed as a `Value` above, so this walk cannot fail.
    let _ = serde::de::DeserializeSeed::deserialize(walk, &mut de);
    errors
}

/// Which record an object in the document decodes into, so the duplicate-key
/// walk knows which keys are typed fields. Duplicate unknown keys, and keys
/// inside untyped values, parse fine (the last one wins), so they pass.
#[derive(Debug, Clone, Copy)]
enum Record {
    Root,
    Settings,
    Modes,
    Mode,
    Vocabulary,
    VocabularyItem,
    Untyped,
}

impl Record {
    fn has_field(self, key: &str) -> bool {
        let in_table = |fields: &[Field]| fields.iter().any(|f| f.key == key);
        match self {
            Record::Root => key == "schemaVersion" || key == "platform" || in_table(ROOT_FIELDS),
            Record::Settings => SETTINGS_KEYS.contains(&key),
            Record::Mode => in_table(MODE_FIELDS),
            Record::VocabularyItem => in_table(VOCABULARY_FIELDS),
            Record::Modes | Record::Vocabulary | Record::Untyped => false,
        }
    }

    fn field(self, key: &str) -> Record {
        match (self, key) {
            (Record::Root, "settings") => Record::Settings,
            (Record::Root, "modes") => Record::Modes,
            (Record::Root, "vocabulary") => Record::Vocabulary,
            _ => Record::Untyped,
        }
    }

    fn item(self) -> Record {
        match self {
            Record::Modes => Record::Mode,
            Record::Vocabulary => Record::VocabularyItem,
            _ => Record::Untyped,
        }
    }
}

/// Walks the JSON text once and reports each repeated typed field.
struct DuplicateKeys<'a> {
    at: Record,
    path: String,
    errors: &'a mut Vec<ValidationError>,
}

impl<'de> serde::de::DeserializeSeed<'de> for DuplicateKeys<'_> {
    type Value = ();

    fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> Result<(), D::Error> {
        deserializer.deserialize_any(self)
    }
}

impl<'de> serde::de::Visitor<'de> for DuplicateKeys<'_> {
    type Value = ();

    fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("any JSON value")
    }

    fn visit_bool<E>(self, _: bool) -> Result<(), E> {
        Ok(())
    }

    fn visit_i64<E>(self, _: i64) -> Result<(), E> {
        Ok(())
    }

    fn visit_u64<E>(self, _: u64) -> Result<(), E> {
        Ok(())
    }

    fn visit_f64<E>(self, _: f64) -> Result<(), E> {
        Ok(())
    }

    fn visit_str<E>(self, _: &str) -> Result<(), E> {
        Ok(())
    }

    fn visit_unit<E>(self) -> Result<(), E> {
        Ok(())
    }

    fn visit_seq<A: serde::de::SeqAccess<'de>>(self, mut seq: A) -> Result<(), A::Error> {
        let item = self.at.item();
        let mut i = 0;
        while seq
            .next_element_seed(DuplicateKeys {
                at: item,
                path: format!("{}[{i}]", self.path),
                errors: &mut *self.errors,
            })?
            .is_some()
        {
            i += 1;
        }
        Ok(())
    }

    fn visit_map<A: serde::de::MapAccess<'de>>(self, mut map: A) -> Result<(), A::Error> {
        let mut seen = std::collections::HashSet::new();
        while let Some(key) = map.next_key::<String>()? {
            let path = if self.path.is_empty() {
                key.clone()
            } else {
                format!("{}.{key}", self.path)
            };
            if self.at.has_field(&key) && !seen.insert(key.clone()) {
                self.errors.push(ValidationError {
                    path: path.clone(),
                    message: "duplicate key".to_string(),
                });
            }
            map.next_value_seed(DuplicateKeys {
                at: self.at.field(&key),
                path,
                errors: &mut *self.errors,
            })?;
        }
        Ok(())
    }
}

fn missing(path: String) -> ValidationError {
    ValidationError {
        path,
        message: "required field is missing".to_string(),
    }
}

/// Check `obj` against one field table. `prefix` is the object's own path
/// (`""` for the root, `modes[2]` for a mode), so errors read `modes[2].id`.
fn check_fields(
    obj: &Map<String, Value>,
    prefix: &str,
    fields: &[Field],
    errors: &mut Vec<ValidationError>,
) {
    for field in fields {
        let path = if prefix.is_empty() {
            field.key.to_string()
        } else {
            format!("{prefix}.{}", field.key)
        };
        match obj.get(field.key) {
            None if field.required => errors.push(missing(path)),
            None => {}
            Some(Value::Null) if !field.required => {}
            Some(v) if field.kind.matches(v) => {}
            Some(_) => errors.push(ValidationError {
                path,
                message: field.kind.message().to_string(),
            }),
        }
    }
}

fn check_items(items: &[Value], field: &str, fields: &[Field], errors: &mut Vec<ValidationError>) {
    for (i, item) in items.iter().enumerate() {
        let path = format!("{field}[{i}]");
        match item.as_object() {
            Some(item_obj) => check_fields(item_obj, &path, fields, errors),
            None => errors.push(ValidationError {
                path,
                message: "must be an object".to_string(),
            }),
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
    fn accepts_linux_platform() {
        let errs = validate_value(&json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "linux"
        }));
        assert!(errs.is_empty());
    }

    #[test]
    fn rejects_bad_platform() {
        let errs = validate_value(&json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "plan9"
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

    #[test]
    fn schema_version_is_compared_by_value() {
        let doc = |v: Value| {
            json!({
                "schemaVersion": v,
                "exportDate": "2026-01-01T00:00:00Z",
                "appVersion": "1.0.0",
                "platform": "macos"
            })
        };
        assert!(validate_value(&doc(json!(2.0))).is_empty());
        let messages = |v: Value| -> Vec<String> {
            validate_value(&doc(v))
                .into_iter()
                .map(|e| e.to_string())
                .collect()
        };
        assert_eq!(messages(json!(3.0)), ["schemaVersion: must be 2, got 3"]);
        assert_eq!(messages(json!(2.5)), ["schemaVersion: must be an integer"]);
        assert_eq!(messages(json!("2")), ["schemaVersion: must be an integer"]);
        assert_eq!(
            messages(json!(1e300)),
            ["schemaVersion: must be an integer"]
        );
    }

    fn base() -> Value {
        json!({
            "schemaVersion": 2,
            "exportDate": "2026-01-01T00:00:00Z",
            "appVersion": "1.0.0",
            "platform": "macos"
        })
    }

    fn with(key: &str, v: Value) -> Value {
        let mut doc = base();
        doc[key] = v;
        doc
    }

    fn messages(doc: &Value) -> Vec<String> {
        validate_value(doc)
            .into_iter()
            .map(|e| e.to_string())
            .collect()
    }

    #[test]
    fn one_wrong_type_per_kind_is_named_by_path() {
        assert_eq!(
            messages(&with("modes", json!([{ "id": 5, "name": "W" }]))),
            ["modes[0].id: must be a string"]
        );
        assert_eq!(
            messages(&with(
                "modes",
                json!([{ "id": "a", "name": "A" }, { "id": "b", "name": "B" },
                       { "id": "c", "name": "C", "isDefault": "yes" }])
            )),
            ["modes[2].isDefault: must be a boolean"]
        );
        assert_eq!(
            messages(&with(
                "vocabulary",
                json!([{ "id": "v", "word": "w", "sortOrder": "1" }])
            )),
            ["vocabulary[0].sortOrder: must be an integer"]
        );
        assert_eq!(
            messages(&with("settings", json!([]))),
            ["settings: must be an object"]
        );
        assert_eq!(
            messages(&with("modes", json!({}))),
            ["modes: must be an array"]
        );
    }

    #[test]
    fn required_fields_must_be_non_null() {
        assert_eq!(
            messages(&with("appVersion", Value::Null)),
            ["appVersion: must be a string"]
        );
        assert_eq!(
            messages(&with("modes", json!([{ "id": "m", "name": null }]))),
            ["modes[0].name: must be a string"]
        );
        assert_eq!(
            messages(&with("vocabulary", json!([{ "id": "v", "word": null }]))),
            ["vocabulary[0].word: must be a string"]
        );
    }

    #[test]
    fn null_optional_fields_and_sections_pass() {
        for key in [
            "settings",
            "modes",
            "vocabulary",
            "apiKeys",
            "licenseKey",
            "platformExtensions",
        ] {
            assert!(messages(&with(key, Value::Null)).is_empty(), "{key}");
        }
        let doc = with(
            "modes",
            json!([{ "id": "m", "name": "M", "isDefault": null, "sortOrder": null,
                     "preset": null, "platformExtensions": null }]),
        );
        assert!(messages(&doc).is_empty());
    }

    #[test]
    fn untyped_and_unknown_fields_pass_anything() {
        let doc = json!({
            "schemaVersion": 2,
            "exportDate": "x", "appVersion": "x", "platform": "macos",
            "settings": { "general": 5, "textOutput": [], "futureGroup": "x" },
            "apiKeys": { "openai": 1 },
            "platformExtensions": { "macos": [] },
            "futureTopLevel": 42,
            "modes": [{ "id": "m", "name": "M", "futureKey": [1] }],
            "vocabulary": [{ "id": "v", "word": "w", "futureKey": {} }]
        });
        assert!(messages(&doc).is_empty());
    }

    #[test]
    fn integer_fields_are_compared_by_value() {
        let mode = |v: Value| with("modes", json!([{ "id": "m", "name": "M", "sortOrder": v }]));
        assert!(messages(&mode(json!(1.0))).is_empty());
        assert_eq!(
            messages(&mode(json!(1.5))),
            ["modes[0].sortOrder: must be an integer"]
        );
        assert_eq!(
            messages(&with(
                "modes",
                json!([{ "id": "m", "name": "M", "postProcessingMode": 2.5 }])
            )),
            ["modes[0].postProcessingMode: must be an integer"]
        );
    }

    #[test]
    fn collects_every_error_not_just_the_first() {
        let doc = json!({
            "schemaVersion": 2,
            "exportDate": 1760000000, "appVersion": 1, "platform": "macos",
            "modes": [{ "id": 5, "name": null }],
            "vocabulary": [{ "id": "v", "word": null }]
        });
        let mut found = messages(&doc);
        found.sort();
        assert_eq!(
            found,
            [
                "appVersion: must be a string",
                "exportDate: must be a string",
                "modes[0].id: must be a string",
                "modes[0].name: must be a string",
                "vocabulary[0].word: must be a string",
            ]
        );
    }

    #[test]
    fn validate_str_reports_a_repeated_typed_field() {
        let doc = r#"{"schemaVersion":2,"exportDate":"x","appVersion":"1","platform":"macos","exportDate":"y",
            "settings":{"general":{},"general":{},"custom":1,"custom":2},
            "modes":[{"id":"a","id":"b","name":"n","extra":1,"extra":2}],
            "vocabulary":[{"id":"v","word":"w","sortOrder":1,"sortOrder":2}],
            "apiKeys":{"k":1,"k":2},"unknown":1,"unknown":2}"#;
        let mut found: Vec<String> = validate_str(doc).iter().map(|e| e.to_string()).collect();
        found.sort();
        assert_eq!(
            found,
            [
                "exportDate: duplicate key",
                "modes[0].id: duplicate key",
                "settings.general: duplicate key",
                "vocabulary[0].sortOrder: duplicate key",
            ]
        );
    }
}
