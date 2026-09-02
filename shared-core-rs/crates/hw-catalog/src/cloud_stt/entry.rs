//! Catalog domain types — the typed shape a parsed `cloud-stt-catalog.json`
//! row takes. Public API surface only; the decoding that builds these lives in
//! [`super::raw`], and the catalog that holds them in [`super`].

use serde::Deserialize;

/// Tri-state custom-vocabulary support, mirroring macOS `CustomVocabulary.Support`.
/// The catalog stores either a bool or the literal string `"unverified"`. Any
/// unrecognized string falls back to [`VocabSupport::No`] (a single typo must
/// not brick the catalog), matching both platforms' lenient decoders.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VocabSupport {
    Yes,
    No,
    Unverified,
}

impl VocabSupport {
    pub(super) fn from_value(v: &serde_json::Value) -> VocabSupport {
        match v {
            serde_json::Value::Bool(true) => VocabSupport::Yes,
            serde_json::Value::Bool(false) => VocabSupport::No,
            serde_json::Value::String(s) if s == "unverified" => VocabSupport::Unverified,
            // Any other string (a catalog typo) → conservative No, matching the
            // macOS `default: self = .no` and Windows `BoolOrStringConverter`.
            _ => VocabSupport::No,
        }
    }
}

/// Custom-vocabulary affordance for a provider. `field_name` is the upstream API
/// parameter the vocabulary list is sent through (e.g. `keyterm`, `prompt`).
#[derive(Debug, Clone, PartialEq)]
pub struct CustomVocabulary {
    pub supported: VocabSupport,
    pub field_name: Option<String>,
    pub caveats: Option<String>,
}

/// Cloud-tier display metadata: the accuracy bucket (`"medium"` / `"high"` /
/// `"highest"`) and the display-only credits-per-minute.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudTier {
    pub accuracy: String,
    pub credits_per_minute: f64,
}

/// Access flags: whether the provider appears under the HyperWhisper Cloud
/// accuracy dropdown (`cloud_tier_eligible`) and/or the BYOK list
/// (`byok_eligible`). Both can be true.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Access {
    pub cloud_tier_eligible: bool,
    pub byok_eligible: bool,
}

/// Per-provider capability flags. Every field defaults to `false` when an older
/// catalog omits it, which is the conservative answer for client affordances.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Features {
    #[serde(default)]
    pub word_timestamps: bool,
    #[serde(default)]
    pub diarization: bool,
    #[serde(default)]
    pub streaming: bool,
    #[serde(default)]
    pub code_switching: bool,
    #[serde(default)]
    pub endpointing: bool,
    #[serde(default)]
    pub context_bias: bool,
    #[serde(default)]
    pub language_bias: bool,
    #[serde(default)]
    pub turn_timestamps: bool,
}

/// The provider's `languages` block (catalog v7). Every field is polymorphic in
/// the same way: the real value, or the documented `"unverified"` literal, which
/// reduces to `None` so callers fall back to conservative defaults rather than
/// to an empty-but-typed value.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Languages {
    /// Upstream's own count of supported languages. May disagree with
    /// `codes.len()` (Deepgram declares 64 and enumerates 88 locale rows).
    pub count: Option<i64>,
    pub auto_detect: Option<bool>,
    /// Human-readable description of the code space `codes` is written in
    /// (e.g. `"ISO-639-1"`, `"BCP-47"`).
    pub code_format: Option<String>,
    pub notes: Option<String>,
    /// Raw upstream language codes, in whatever mixed format the upstream
    /// declares. `None` when the catalog leaves the set `"unverified"`.
    pub codes: Option<Vec<String>>,
}

/// A single routable model within a provider. `id` is the `X-STT-Model` header
/// value (may be `""` for single-model providers like Grok).
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SttModel {
    pub id: String,
    pub display_name: String,
    #[serde(default)]
    pub credits_per_minute: Option<f64>,
    #[serde(default)]
    pub is_default: Option<bool>,
    #[serde(default)]
    pub preview_status: Option<bool>,
    #[serde(default)]
    pub supports_custom_vocabulary: Option<bool>,
    /// Whether HyperWhisper Cloud serves a **live WebSocket route** for this
    /// specific model.
    ///
    /// Deliberately narrower than the entry-level `features.streaming` hint,
    /// which merely records that the *vendor* offers streaming — it is `true`
    /// for six vendors we have no backend WS route for, so using it to build a
    /// picker would ship a 404 at dictation time. This flag means "we route it".
    ///
    /// `#[serde(default)]`, so today's catalog file (which has no `streaming`
    /// key on any model) still decodes unchanged and every model reads `false`.
    #[serde(default)]
    pub streaming: Option<bool>,
}

impl SttModel {
    /// Whether this specific model supports custom vocabulary, defaulting to
    /// false on a missing flag — matches Windows `ModelSupportsCustomVocabulary`.
    pub fn supports_custom_vocabulary(&self) -> bool {
        self.supports_custom_vocabulary.unwrap_or(false)
    }

    /// Whether HyperWhisper Cloud serves a live WebSocket route for this model.
    /// Missing flag ⇒ false.
    pub fn streaming(&self) -> bool {
        self.streaming.unwrap_or(false)
    }
}

/// One cloud STT provider row. Mirrors macOS `CloudSTTCatalog.Entry` /
/// Windows `CloudSttCatalogEntry`.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SttEntry {
    pub id: String,
    pub display_name: String,
    #[serde(default)]
    pub display_model: Option<String>,
    pub vendor: String,
    /// Plain company name shown in the Provider dropdown ("Deepgram", "xAI").
    /// Catalog v7+ — carries no model family or version, unlike `display_name`
    /// ("Deepgram Nova 3"). `None` on an older catalog; callers fall back to
    /// `display_name` (see [`SttEntry::vendor_label`]).
    #[serde(default)]
    pub vendor_display_name: Option<String>,
    /// The `X-STT-Provider` header value the backend routes on (catalog v6+).
    #[serde(default)]
    pub stt_provider: Option<String>,
    #[serde(default)]
    pub access: Option<Access>,
    #[serde(default)]
    pub models: Vec<SttModel>,
    #[serde(default)]
    pub cloud_tier: Option<CloudTier>,
    /// Capability flags (catalog v7). Defaults to all-false when absent.
    #[serde(default)]
    pub features: Features,
    /// Upload size ceiling in MB, or `None` when `"unverified"`. Fractional —
    /// Google Chirp's inline limit is 9.5 MB.
    #[serde(default)]
    pub max_file_size_mb: Option<f64>,
    /// Per-request audio-duration ceiling in minutes, or `None` when absent or
    /// `"unverified"`.
    #[serde(default)]
    pub max_duration_minutes: Option<i64>,
    /// Container/codec extensions the upstream accepts, in catalog order.
    #[serde(default)]
    pub accepted_formats: Vec<String>,
    /// Parsed separately (the `supported` field is bool-or-string). Set during
    /// [`CloudSttCatalog::parse`](super::CloudSttCatalog::parse); `serde(skip)` keeps the derived `Deserialize`
    /// from choking on the polymorphic field.
    #[serde(skip)]
    pub custom_vocabulary: Option<CustomVocabulary>,
    /// The provider's `languages` block. Every field inside is polymorphic
    /// (real value or the `"unverified"` literal), so this too is reduced during
    /// [`CloudSttCatalog::parse`](super::CloudSttCatalog::parse) rather than by the derived `Deserialize`.
    #[serde(skip)]
    pub languages: Languages,
    #[serde(default)]
    pub preview_status: Option<bool>,
    #[serde(default)]
    pub migrate_from: Option<Vec<String>>,
    #[serde(default)]
    pub legacy_cloud_provider_aliases: Option<Vec<String>>,
}

impl SttEntry {
    /// The plain company name for the Provider dropdown — `vendorDisplayName`,
    /// falling back to `displayName` on an older catalog. Matches macOS
    /// `first.vendorDisplayName ?? first.displayName` and Windows'
    /// `IsNullOrEmpty(VendorDisplayName) ? DisplayName : VendorDisplayName`
    /// (an empty string falls back too, which the Swift `??` did not do).
    pub fn vendor_label(&self) -> &str {
        match self.vendor_display_name.as_deref() {
            Some(v) if !v.is_empty() => v,
            _ => &self.display_name,
        }
    }

    /// Raw upstream language codes, or `None` when the catalog leaves the set
    /// `"unverified"`.
    pub fn language_codes(&self) -> Option<&[String]> {
        self.languages.codes.as_deref()
    }

    /// Whether the provider supports custom vocabulary through OUR backend.
    /// Matches Windows `SupportsCustomVocabulary` (`supported == "true"`): only
    /// an explicit `Yes` counts — `Unverified` and `No` are both false.
    pub fn supports_custom_vocabulary(&self) -> bool {
        matches!(
            self.custom_vocabulary.as_ref().map(|cv| cv.supported),
            Some(VocabSupport::Yes)
        )
    }

    /// Display-only credits/min from the cloud-tier block, or `0.0` when absent.
    /// Matches Windows `CreditsPerMinute`.
    pub fn credits_per_minute(&self) -> f64 {
        self.cloud_tier.as_ref().map(|t| t.credits_per_minute).unwrap_or(0.0)
    }

    /// The default model — `isDefault: true`, else the first listed, else `None`.
    pub fn default_model(&self) -> Option<&SttModel> {
        self.models
            .iter()
            .find(|m| m.is_default == Some(true))
            .or_else(|| self.models.first())
    }

    /// The default model id (`X-STT-Model` value), or `None` when the provider
    /// lists no models. Note: the id may legitimately be `""` (Grok).
    pub fn default_model_id(&self) -> Option<&str> {
        self.default_model().map(|m| m.id.as_str())
    }

    /// Look up a model by id (case-insensitive), matching the catalog convention.
    pub fn model(&self, model_id: &str) -> Option<&SttModel> {
        self.models
            .iter()
            .find(|m| m.id.eq_ignore_ascii_case(model_id))
    }

    /// Credits/min for a specific model, falling back to the tier cost, then
    /// `0.0` — matches Windows `CreditsPerMinuteForModel`.
    pub fn credits_per_minute_for_model(&self, model_id: &str) -> f64 {
        self.model(model_id)
            .and_then(|m| m.credits_per_minute)
            .unwrap_or_else(|| self.credits_per_minute())
    }
}
