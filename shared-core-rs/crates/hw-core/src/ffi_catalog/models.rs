//! `models-catalog.json` — the mirrored types and the exported lookups.

use super::catalogs::models;

/// Model family. Mirrors `hw_catalog::Kind`.
#[derive(uniffi::Enum)]
pub enum HwKind {
    Voice,
    Text,
}

impl From<HwKind> for hw_catalog::Kind {
    fn from(k: HwKind) -> Self {
        match k {
            HwKind::Voice => hw_catalog::Kind::Voice,
            HwKind::Text => hw_catalog::Kind::Text,
        }
    }
}

/// Language support for a model. Mirrors `hw_catalog::LanguageSupport` with the
/// `BTreeSet` flattened to a sorted `Vec`.
#[derive(uniffi::Record)]
pub struct HwLanguageSupport {
    pub codes: Vec<String>,
    pub supports_all: bool,
}

impl From<hw_catalog::LanguageSupport> for HwLanguageSupport {
    fn from(l: hw_catalog::LanguageSupport) -> Self {
        HwLanguageSupport {
            codes: l.codes.into_iter().collect(), // BTreeSet iterates sorted
            supports_all: l.supports_all,
        }
    }
}

/// One row of `models-catalog.json`. Owned mirror of `hw_catalog::Entry`.
#[derive(uniffi::Record)]
pub struct ModelsEntry {
    pub provider: String,
    pub id: String,
    /// The raw `kind` string (`"voice"` / `"text"`). Anything unrecognized is
    /// keyed as voice, matching both platforms' loaders.
    pub kind: String,
    pub supports_custom_vocabulary: bool,
    pub available_via_hyper_whisper_cloud: bool,
    pub platforms: Vec<String>,
    pub display_name: Option<String>,
    pub notes: Option<String>,
    /// Base ISO codes, empty on rows that carry none. Prefer
    /// `models_language_support`, which resolves the wildcard fallback and the
    /// "uncatalogued ⇒ every language" rule.
    pub supported_languages: Vec<String>,
    pub is_english_only: Option<bool>,
    pub supports_all_languages: Option<bool>,
}

impl From<&hw_catalog::Entry> for ModelsEntry {
    fn from(e: &hw_catalog::Entry) -> Self {
        ModelsEntry {
            provider: e.provider.clone(),
            id: e.id.clone(),
            kind: e.kind.clone(),
            supports_custom_vocabulary: e.supports_custom_vocabulary,
            available_via_hyper_whisper_cloud: e.available_via_hyper_whisper_cloud,
            platforms: e.platforms.clone(),
            display_name: e.display_name.clone(),
            notes: e.notes.clone(),
            supported_languages: e.supported_languages.clone().unwrap_or_default(),
            is_english_only: e.is_english_only,
            supports_all_languages: e.supports_all_languages,
        }
    }
}

// ---------------------------------------------------------------------------
// models catalog
// ---------------------------------------------------------------------------

/// Whether a model supports custom vocabulary.
#[uniffi::export]
pub fn models_supports_custom_vocabulary(provider: String, kind: HwKind, id: String) -> bool {
    models().supports_custom_vocabulary(&provider, kind.into(), &id)
}

/// Whether a model is available via HyperWhisper Cloud.
#[uniffi::export]
pub fn models_available_via_hw_cloud(provider: String, kind: HwKind, id: String) -> bool {
    models().available_via_hyper_whisper_cloud(&provider, kind.into(), &id)
}

/// The language support of a model (codes sorted; `supports_all` for any-language).
#[uniffi::export]
pub fn models_language_support(provider: String, kind: HwKind, id: String) -> HwLanguageSupport {
    models()
        .language_support(&provider, kind.into(), &id)
        .into()
}

/// A single catalogued model row, resolving the `(provider, kind, "*")` wildcard
/// when the exact id is not catalogued. `None` on a miss.
#[uniffi::export]
pub fn models_entry(provider: String, kind: HwKind, id: String) -> Option<ModelsEntry> {
    models()
        .entry(&provider, kind.into(), &id)
        .map(ModelsEntry::from)
}

/// Every catalogued model row, ordered by `(provider, kind, id)`. Replaces the
/// per-platform catalog decoders that scanned the file for parity checks and for
/// the unified capability list.
#[uniffi::export]
pub fn models_all_entries() -> Vec<ModelsEntry> {
    models().all_entries().map(ModelsEntry::from).collect()
}
