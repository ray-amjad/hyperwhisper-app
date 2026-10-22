//! Small JSON helpers shared by the settings adapters.

use serde_json::{Map, Value};

pub(super) fn get<'a>(v: &'a Value, key: &str) -> Option<&'a Value> {
    v.as_object().and_then(|o| o.get(key))
}

pub(super) fn copy_keys(src: &Value, keys: &[&str], dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for k in keys {
            if let Some(v) = o.get(*k) {
                dst.insert((*k).to_string(), v.clone());
            }
        }
    }
}

pub(super) fn copy_except(src: &Value, skip: &[&str], dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for (k, v) in o {
            if !skip.contains(&k.as_str()) {
                dst.insert(k.clone(), v.clone());
            }
        }
    }
}

pub(super) fn copy_all(src: &Value, dst: &mut Map<String, Value>) {
    if let Some(o) = src.as_object() {
        for (k, v) in o {
            dst.insert(k.clone(), v.clone());
        }
    }
}

/// Object form of [`copy_except`]: every key of `src` except `skip`.
pub(super) fn object_except(src: &Value, skip: &[&str]) -> Map<String, Value> {
    let mut m = Map::new();
    copy_except(src, skip, &mut m);
    m
}

/// Object form of [`copy_all`]: every key of `src`.
pub(super) fn object_all(src: &Value) -> Map<String, Value> {
    let mut m = Map::new();
    copy_all(src, &mut m);
    m
}

/// Insert `cat` under `key` in `dst`, but only when `cat` is non-empty (keeps the
/// category-keyed extension blob free of empty `{}` sub-objects).
pub(super) fn insert_category(dst: &mut Map<String, Value>, key: &str, cat: Map<String, Value>) {
    if !cat.is_empty() {
        dst.insert(key.to_string(), Value::Object(cat));
    }
}

/// Read a nested object sub-category from the macOS settings extension blob,
/// returning an empty map when the category is absent or not an object.
pub(super) fn sub_object(blob: &Map<String, Value>, key: &str) -> Map<String, Value> {
    blob.get(key)
        .and_then(|v| v.as_object())
        .cloned()
        .unwrap_or_default()
}

pub(super) fn non_empty(m: Map<String, Value>) -> Option<Value> {
    if m.is_empty() {
        None
    } else {
        Some(Value::Object(m))
    }
}
