//! Decoding `cloud-stt-catalog.json` into the typed rows in [`super::entry`].
//!
//! Kept apart from the lookup surface because the catalog's polymorphic fields
//! (`number`-or-`"unverified"`, `bool`-or-`"unverified"`,
//! `string[]`-or-`"unverified"`) force a second set of `Raw*` shapes that
//! nothing outside this module should ever see.

use serde::Deserialize;

use super::{
    Access, CloudTier, CustomVocabulary, Features, Languages, SttEntry, SttModel, VocabSupport,
};

/// Error parsing the cloud-stt catalog JSON.
#[derive(thiserror::Error, Debug)]
pub enum CloudSttError {
    #[error("cloud-stt-catalog.json failed to decode: {0}")]
    Decode(#[from] serde_json::Error),
}

/// Raw deserialization shape. The polymorphic `customVocabulary.supported` and
/// `languages.codes` fields are captured as `serde_json::Value` and reduced in
/// [`CloudSttCatalog::parse`].
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawFile {
    #[serde(default)]
    version: i64,
    #[serde(default)]
    updated: String,
    #[serde(default)]
    providers: Vec<RawEntry>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawEntry {
    id: String,
    #[serde(default)]
    display_name: String,
    #[serde(default)]
    display_model: Option<String>,
    #[serde(default)]
    vendor: String,
    #[serde(default)]
    vendor_display_name: Option<String>,
    #[serde(default)]
    stt_provider: Option<String>,
    #[serde(default)]
    access: Option<Access>,
    #[serde(default)]
    models: Vec<SttModel>,
    #[serde(default)]
    cloud_tier: Option<CloudTier>,
    #[serde(default)]
    features: Features,
    /// `number` or the `"unverified"` literal.
    #[serde(default)]
    max_file_size_mb: serde_json::Value,
    /// `number` or the `"unverified"` literal.
    #[serde(default)]
    max_duration_minutes: serde_json::Value,
    #[serde(default)]
    accepted_formats: Vec<String>,
    #[serde(default)]
    custom_vocabulary: Option<RawCustomVocabulary>,
    #[serde(default)]
    languages: Option<RawLanguages>,
    #[serde(default)]
    preview_status: Option<bool>,
    #[serde(default)]
    migrate_from: Option<Vec<String>>,
    #[serde(default)]
    legacy_cloud_provider_aliases: Option<Vec<String>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawCustomVocabulary {
    #[serde(default)]
    supported: serde_json::Value,
    #[serde(default)]
    field_name: Option<String>,
    #[serde(default)]
    caveats: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawLanguages {
    #[serde(default)]
    count: serde_json::Value,
    #[serde(default)]
    auto_detect: serde_json::Value,
    #[serde(default)]
    code_format: Option<String>,
    #[serde(default)]
    notes: Option<String>,
    #[serde(default)]
    codes: serde_json::Value,
}

/// Reduce a `number`-or-`"unverified"` field to `Some(n)`. Any non-number
/// (`"unverified"`, a typo, or absent) is `None` — the same fall-through both
/// platforms' converters use, so one bad row never fails the whole decode.
fn number_or_none(v: &serde_json::Value) -> Option<f64> {
    v.as_f64()
}

/// Reduce a `bool`-or-`"unverified"` field. Non-bool → `None`.
fn bool_or_none(v: &serde_json::Value) -> Option<bool> {
    v.as_bool()
}

/// Reduce a `string[]`-or-`"unverified"` field. Non-array → `None`; non-string
/// elements inside an array are dropped.
fn string_array_or_none(v: serde_json::Value) -> Option<Vec<String>> {
    match v {
        serde_json::Value::Array(arr) => Some(
            arr.into_iter()
                .filter_map(|v| match v {
                    serde_json::Value::String(s) => Some(s),
                    _ => None,
                })
                .collect(),
        ),
        _ => None,
    }
}

/// A decoded catalog file, before it is wrapped in a [`super::CloudSttCatalog`].
pub(super) struct DecodedCatalog {
    pub(super) version: i64,
    pub(super) updated: String,
    pub(super) providers: Vec<SttEntry>,
}

/// Decode a cloud-stt-catalog JSON string into its typed rows.
pub(super) fn decode(json: &str) -> Result<DecodedCatalog, CloudSttError> {
    let raw: RawFile = serde_json::from_str(json)?;
    let providers = raw.providers.into_iter().map(entry_from_raw).collect();
    Ok(DecodedCatalog {
        version: raw.version,
        updated: raw.updated,
        providers,
    })
}

/// Reduce one raw provider row to its typed [`SttEntry`].
fn entry_from_raw(r: RawEntry) -> SttEntry {
    let custom_vocabulary = r.custom_vocabulary.map(|cv| CustomVocabulary {
        supported: VocabSupport::from_value(&cv.supported),
        field_name: cv.field_name,
        caveats: cv.caveats,
    });
    // Every field of `languages` is "the real value or the literal
    // "unverified"" — anything of the wrong JSON type reduces to
    // None, matching Swift's ArrayOrString/IntOrString/BoolOrString
    // and the Windows *OrStringConverter family.
    let languages = r
        .languages
        .map(|l| Languages {
            count: number_or_none(&l.count).map(|n| n as i64),
            auto_detect: bool_or_none(&l.auto_detect),
            code_format: l.code_format,
            notes: l.notes,
            codes: string_array_or_none(l.codes),
        })
        .unwrap_or_default();
    SttEntry {
        id: r.id,
        display_name: r.display_name,
        display_model: r.display_model,
        vendor: r.vendor,
        vendor_display_name: r.vendor_display_name,
        stt_provider: r.stt_provider,
        access: r.access,
        models: r.models,
        cloud_tier: r.cloud_tier,
        features: r.features,
        max_file_size_mb: number_or_none(&r.max_file_size_mb),
        max_duration_minutes: number_or_none(&r.max_duration_minutes)
            .map(|n| n as i64),
        accepted_formats: r.accepted_formats,
        custom_vocabulary,
        languages,
        preview_status: r.preview_status,
        migrate_from: r.migrate_from,
        legacy_cloud_provider_aliases: r.legacy_cloud_provider_aliases,
    }
}
