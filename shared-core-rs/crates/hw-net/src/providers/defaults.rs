//! The BYOK default model id, read from the shared cloud-STT catalog.
//!
//! Every provider module used to carry its own `pub const DEFAULT_MODEL` string
//! literal. That was the fourth copy of a fact that also lived in
//! `shared-app-classification/cloud-stt-catalog.json`, in Windows
//! `CloudTranscriptionModels.GetDefault` and in macOS
//! `CloudTranscriptionModels.defaultModel(for:)` — and for OpenAI the four
//! copies disagreed (issue #580): the catalog said `gpt-4o-transcribe` while the
//! three hand-written tables said `whisper-1`, so byte-identical audio and a
//! byte-identical request body transcribed on a different model depending on
//! which head served the request.
//!
//! There is now ONE table. A provider module names the catalog ENTRY it belongs
//! to (a key, not a duplicated value) and asks for that entry's default here.
//! `shared-conformance/default-model-vectors.json` pins the answer and all four
//! heads replay it through their own resolver, so a head that reintroduces a
//! literal fails CI.

use std::sync::OnceLock;

use hw_catalog::CloudSttCatalog;

/// The embedded catalog, parsed once.
///
/// The JSON is `include_str!`-embedded at compile time, so a parse failure is a
/// build-time invariant violation rather than a runtime condition. It is still
/// modelled as `Option` (matching [`crate::live`]'s reader) so a malformed
/// catalog degrades to "no default" instead of panicking inside a request
/// builder; [`tests::every_provider_resolves_a_default`] rules the case out.
/// `pub(crate)` rather than private because [`crate::live`] needs the same
/// parsed catalog to derive its relay route. Two `OnceLock`s in one dylib would
/// hold two full parses of the same embedded JSON for no benefit.
pub(crate) fn catalog() -> Option<&'static CloudSttCatalog> {
    static CATALOG: OnceLock<Option<CloudSttCatalog>> = OnceLock::new();
    CATALOG
        .get_or_init(|| CloudSttCatalog::embedded().ok())
        .as_ref()
}

/// The default model id for a catalog entry — the value a BYOK builder puts in
/// its own request body when the caller leaves `params.model` empty.
///
/// `""` is a legitimate answer (Grok STT exposes one implicit model and takes no
/// `model` parameter), which is why this returns a `&str` rather than an
/// `Option`: the callers that matter cannot tell "no such entry" from "the empty
/// model id" apart in any useful way, and the conformance vector pins both.
pub fn default_model(catalog_entry_id: &str) -> &'static str {
    catalog()
        .and_then(|catalog| catalog.default_model_id(catalog_entry_id))
        .unwrap_or("")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every provider module that resolves a default through this table must
    /// find its catalog entry. A typo in a `CATALOG_ENTRY_ID` would otherwise
    /// silently post an empty `model` field.
    #[test]
    fn every_provider_resolves_a_default() {
        let catalog = catalog().expect("the embedded catalog parses");
        for entry_id in [
            crate::providers::assemblyai::CATALOG_ENTRY_ID,
            crate::providers::deepgram::CATALOG_ENTRY_ID,
            crate::providers::elevenlabs::CATALOG_ENTRY_ID,
            crate::providers::gemini::CATALOG_ENTRY_ID,
            crate::providers::gemini_transcribe::CATALOG_ENTRY_ID,
            crate::providers::groq::CATALOG_ENTRY_ID,
            crate::providers::meta::CATALOG_ENTRY_ID,
            crate::providers::mistral::CATALOG_ENTRY_ID,
            crate::providers::openai::CATALOG_ENTRY_ID,
            crate::providers::soniox::CATALOG_ENTRY_ID,
        ] {
            assert!(
                catalog.entry(entry_id).is_some(),
                "no catalog entry for {entry_id}"
            );
            assert!(
                !default_model(entry_id).is_empty(),
                "{entry_id} resolved an empty default model"
            );
        }
    }

    #[test]
    fn an_unknown_entry_resolves_to_the_empty_model_id() {
        assert_eq!(default_model("noSuchProvider"), "");
    }
}
