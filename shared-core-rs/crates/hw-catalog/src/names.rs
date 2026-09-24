//! The one rule for how a model and a company are NAMED, and the tests that
//! hold every catalog to it.
//!
//! Before this module the same model could carry 2 names in one build: the
//! Windows registry said `Whisper Large V3` and the macOS registry said
//! `Whisper Large v3`; `stt-async-v5` was `Async v5` in the catalog and
//! `STT Async v5` on both heads; 4 models had `(Preview)` glued onto the name
//! on the heads while the catalog carried a `previewStatus` boolean beside it.
//! Nothing failed — the copies just drifted, because nothing compared them.
//!
//! # Who owns a name
//!
//! | Name | Owner |
//! |---|---|
//! | A model | `shared-models/models-catalog.json` → `displayName` |
//! | A company | `shared-app-classification/cloud-stt-catalog.json` → `vendorDisplayName` |
//! | A Cloud tier | `shared-app-classification/cloud-stt-catalog.json` → `displayName` |
//!
//! `cloud-stt-catalog.json` also names its own model rows, because it carries 1
//! model `models-catalog.json` deliberately does not: `gemini-3.5-transcribe-live`
//! is WebSocket-only and is not a Model Library row. Where both files hold a
//! row, [`tests::the_two_catalogs_agree_on_every_shared_model_name`] requires the
//! 2 names to be identical, so a model still has exactly 1 name.
//!
//! No other file may hold a model name or a company name — not a `.swift`, not
//! a `.cs`, not a `.resx`, not a `.strings`. A name is a proper noun, so it is
//! never translated either.
//!
//! # The style rule
//!
//! A model name is the vendor's own name for that model, with:
//!
//! - no company prefix — the Provider row above it already says the company;
//! - no status suffix — `previewStatus` is a field and the UI draws a badge;
//! - no domain suffix in brackets — `Universal-2 Medical`, not `Universal-2 (Medical)`;
//! - no version padding — `Grok Voice Transcribe 1`, not `… 1.0`.
//!
//! [`style_violation`] is that rule as code. Both catalogs are held to it.

/// Why a display name breaks the style rule, or `None` when it is fine.
///
/// The check is deliberately narrow: it catches the 4 shapes that actually
/// drifted, and it says nothing about names it has no rule for. A lint that
/// guesses would be argued with and then switched off.
pub fn style_violation(name: &str) -> Option<&'static str> {
    if name.contains("(Preview)") || name.contains("(preview)") {
        return Some("status belongs in `previewStatus`, and the UI draws the badge");
    }
    if name.contains("(Medical)") || name.contains("(medical)") {
        return Some("write the domain as a word: `Universal-2 Medical`");
    }
    if name.ends_with(".0") {
        return Some("drop the padding: `Grok Voice Transcribe 1`, not `… 1.0`");
    }
    if name.contains(" V") && name.split(" V").nth(1).is_some_and(starts_with_digit) {
        return Some("a version is lower-case `v`: `Whisper Large v3`");
    }
    None
}

fn starts_with_digit(rest: &str) -> bool {
    rest.chars().next().is_some_and(|c| c.is_ascii_digit())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::cloud_stt::CloudSttCatalog;
    use crate::models::{Kind, ModelsCatalog};

    fn models() -> ModelsCatalog {
        ModelsCatalog::embedded().expect("models-catalog.json must parse")
    }

    fn cloud_stt() -> CloudSttCatalog {
        CloudSttCatalog::embedded().expect("cloud-stt-catalog.json must parse")
    }

    /// The gate that makes `models-catalog.json` the owner: a head that reads a
    /// name through the core gets a name for every row it can list. A miss here
    /// is a model the picker would label with its raw id.
    #[test]
    fn every_real_voice_row_is_named() {
        let catalog = models();
        let unnamed: Vec<_> = catalog
            .all_entries()
            .filter(|e| e.kind() == Kind::Voice && e.id != "*")
            .filter(|e| e.display_name.as_deref().unwrap_or("").is_empty())
            .map(|e| format!("{}|{}", e.provider, e.id))
            .collect();
        assert!(unnamed.is_empty(), "voice rows with no displayName: {unnamed:?}");
    }

    /// A wildcard row stands for a whole family, so it cannot carry one name.
    /// Local model names come from each head's own on-device registry.
    #[test]
    fn a_wildcard_row_carries_no_name() {
        let catalog = models();
        let named: Vec<_> = catalog
            .all_entries()
            .filter(|e| e.id == "*" && e.display_name.is_some())
            .map(|e| e.provider.clone())
            .collect();
        assert!(named.is_empty(), "wildcard rows must not be named: {named:?}");
    }

    /// The 2 catalogs overlap on 29 models. A model has 1 name, so where both
    /// hold a row the strings must be identical — this is what stops the pair
    /// drifting the way the native registries did.
    #[test]
    fn the_two_catalogs_agree_on_every_shared_model_name() {
        let models = models();
        let stt = cloud_stt();
        let mut disagreements = Vec::new();
        for entry in stt.providers() {
            let Some(provider) = entry.stt_provider.as_deref() else {
                continue;
            };
            for model in &entry.models {
                // Resolve by id across every models-catalog provider key: the
                // 2 files key providers differently (`grok` vs `grokStt`), and
                // an id is unique inside the voice kind.
                let Some(row) = models
                    .all_entries()
                    .find(|e| e.kind() == Kind::Voice && e.id == model.id)
                else {
                    continue;
                };
                let Some(name) = row.display_name.as_deref() else {
                    continue;
                };
                if name != model.display_name {
                    disagreements.push(format!(
                        "{provider}/{}: cloud-stt `{}` vs models-catalog `{name}`",
                        model.id, model.display_name
                    ));
                }
            }
        }
        assert!(
            disagreements.is_empty(),
            "a model must have exactly 1 name: {disagreements:?}"
        );
    }

    #[test]
    fn every_catalog_name_follows_the_style_rule() {
        let mut broken = Vec::new();
        for entry in models().all_entries() {
            if let Some(name) = entry.display_name.as_deref() {
                if let Some(reason) = style_violation(name) {
                    broken.push(format!("models-catalog `{name}`: {reason}"));
                }
            }
        }
        for entry in cloud_stt().providers() {
            for model in &entry.models {
                if let Some(reason) = style_violation(&model.display_name) {
                    broken.push(format!("cloud-stt `{}`: {reason}", model.display_name));
                }
            }
        }
        assert!(broken.is_empty(), "{broken:?}");
    }

    #[test]
    fn the_style_rule_catches_the_shapes_that_actually_drifted() {
        assert!(style_violation("MAI-Transcribe 2 (Preview)").is_some());
        assert!(style_violation("Universal-2 (Medical)").is_some());
        assert!(style_violation("Muse Voice Transcribe 1.0").is_some());
        assert!(style_violation("Whisper Large V3").is_some());
        assert!(style_violation("Scribe V2").is_some());

        assert!(style_violation("MAI-Transcribe 2").is_none());
        assert!(style_violation("Universal-2 Medical").is_none());
        assert!(style_violation("Grok Voice Transcribe 1").is_none());
        assert!(style_violation("Whisper Large v3").is_none());
        // A capital V that is not a version must pass, or the rule bans a real
        // vendor name the day one arrives.
        assert!(style_violation("Voxtral Mini").is_none());
        assert!(style_violation("Nova 3 Voice").is_none());
    }

    /// Every company name in the tier catalog is a company, not a product. The
    /// Provider row on all 3 heads prints this string.
    #[test]
    fn a_vendor_display_name_is_a_company() {
        for entry in cloud_stt().providers() {
            let vendor = entry
                .vendor_display_name
                .as_deref()
                .unwrap_or_else(|| panic!("{} has no vendorDisplayName", entry.id));
            assert!(
                style_violation(vendor).is_none(),
                "{}: vendorDisplayName `{vendor}` breaks the style rule",
                entry.id
            );
        }
    }
}
