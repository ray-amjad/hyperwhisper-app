//! Legacy `cloudProvider` storage-value normalization.
//!
//! Folds a legacy standalone-provider alias onto its cloud tier, and lowercases
//! everything else so both platforms' parsers accept the persisted spelling.

use super::CloudSttCatalog;

impl CloudSttCatalog {
    /// Normalize a persisted `cloudProvider` storage value. If `value` is a
    /// legacy standalone-provider alias for a provider now surfaced as a cloud
    /// tier (e.g. `microsoftazurespeech` → `azureMaiTranscribe`), returns
    /// `(Some("hyperwhisper"), Some(<tier id>))`. Otherwise the value passes
    /// through — **ASCII-lowercased** — with `accuracy_tier == None`. Critically,
    /// BYOK provider names (`"deepgram"`, `"groq"`) pass through rather than
    /// folding onto the cloud tier, even though they appear in `migrateFrom`.
    /// Mirrors macOS/Windows `normalizeCloudProvider`.
    ///
    /// ## Why the pass-through is lowercased
    ///
    /// `cloudProvider` is a cross-platform storage value, and the two platforms
    /// do NOT write it the same way. Windows' `GetIdentifier` emits camelCase
    /// (`geminiTranscribe`, `googleSpeech`, `microsoftAzureSpeech`); macOS'
    /// `CloudProvider` enum raw values are ALL lowercase and are parsed with a
    /// case-SENSITIVE `CloudProvider(rawValue:)` whose miss silently falls back
    /// to `.hyperwhisper`. So a Windows→macOS restore of a camelCase id used to
    /// land the user on HyperWhisper Cloud — billed credits — with no error and
    /// no visible UI change.
    ///
    /// That asymmetry was latent until catalog v8: the only camelCase ids
    /// Windows could write were `googleSpeech` and `microsoftAzureSpeech`, and
    /// both are `legacyCloudProviderAliases`, so they were rewritten to
    /// `"hyperwhisper"` by the branch above and never reached the pass-through.
    /// `geminiTranscribe` is the first camelCase id that is `byokEligible` and
    /// is NOT a legacy alias, so it is the first one the pass-through has to
    /// carry — and the first that can silently move a BYOK user onto paid
    /// credits. Lowercasing here fixes the whole class, not just that one id:
    /// every value either platform can persist is a lowercase enum raw value on
    /// macOS, and Windows' `FromIdentifier` matches on `ToLowerInvariant()`, so
    /// the lowercase spelling is the one form BOTH parsers accept.
    ///
    /// An id this catalog does not know is lowercased too. It fails to parse on
    /// either platform whichever way it is spelled, so nothing is lost, and
    /// keeping the rule unconditional means a future camelCase provider id
    /// cannot reintroduce the bug by simply not being in a table yet.
    pub fn normalize_cloud_provider(&self, value: Option<&str>) -> NormalizedCloudProvider {
        let Some(value) = value.filter(|v| !v.is_empty()) else {
            return NormalizedCloudProvider {
                provider: value.map(|s| s.to_string()),
                accuracy_tier: None,
            };
        };
        if let Some(entry) = self.entry_by_legacy_cloud_provider_alias(value) {
            return NormalizedCloudProvider {
                provider: Some("hyperwhisper".to_string()),
                accuracy_tier: Some(entry.id.clone()),
            };
        }
        NormalizedCloudProvider {
            provider: Some(value.to_ascii_lowercase()),
            accuracy_tier: None,
        }
    }
}

/// Result of [`CloudSttCatalog::normalize_cloud_provider`]. `accuracy_tier` is
/// `Some` only when `provider` was folded onto `"hyperwhisper"`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NormalizedCloudProvider {
    pub provider: Option<String>,
    pub accuracy_tier: Option<String>,
}
