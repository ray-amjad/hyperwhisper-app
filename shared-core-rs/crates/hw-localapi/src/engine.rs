//! `POST /transcribe`'s `engine` field: one alias table, and the canonical id
//! each spelling means.
//!
//! Issue #356 item 3. Four hand-kept `switch`es answer this today — two of them
//! on macOS alone (`TranscribeEndpoint.applyEngineModel` and
//! `TranscriptionProviderRouter.resolveProvider`, whose in-file comments already
//! admit they are hand-synced), one on Windows
//! (`TranscribeEndpoints.ApplyEngineModel`) and one on the portable head
//! (`ApplicationLocalApiBackend.ApplyTranscriptionOverrides`). They disagree, and
//! the disagreements are client-visible:
//!
//! * Windows and the portable head **emit** `qwen3_asr` as the `engine` label on
//!   a response and macOS does not **accept** it, so feeding one head's answer
//!   back to another is an `Unknown engine` error. `qwen` has the same shape.
//! * `nemotron*` and `applespeech`/`apple*`/`speech-analyzer` are macOS-only, and
//!   the other two heads answer `Unknown engine '…'` rather than
//!   `ENGINE_UNAVAILABLE`-with-a-reason.
//! * Only the portable head trims, so `engine: " openai"` works on Linux and
//!   fails on the other two.
//!
//! # This resolves an id. It does not decide availability.
//!
//! [`resolve_engine_alias`] returns *what the caller asked for*, canonically
//! spelled. Whether this build can serve it is the head's question and stays
//! there: macOS has Nemotron and Apple Speech and the .NET heads do not, so a
//! shared table that answered "is this engine usable" would be wrong on two
//! platforms in opposite directions. A head that cannot serve a resolved id
//! answers `ENGINE_UNAVAILABLE` — already one of the closed 14, so item 3 adds
//! no error code.
//!
//! Keeping capability out is the same rule [`crate::validate_mode`] follows when
//! it refuses the "an enabled `postProcessingMode` needs a provider" cross-check.
//!
//! # The cloud half is deliberately absent
//!
//! `openapi.yaml` documents the field as
//! `whisperLocal | parakeet | nemotron | qwen3Asr | appleSpeech | <CloudProvider rawValue>`.
//! The first five are here. The sixth is **not**, and must not be: cloud
//! provider folding already has a shared home in `hw-catalog`'s
//! `CloudSttCatalog::normalize_cloud_provider`, backed by
//! `shared-app-classification/cloud-stt-catalog.json`, and macOS and Windows
//! already call it. Re-implementing it here would give the app two catalogs of
//! the same thing, which is the failure #356 exists to stop.
//!
//! So `None` from [`resolve_engine_alias`] means "not one of the five" — the
//! head's next step is the cloud catalog, exactly as it is today, and only then
//! `ENGINE_UNAVAILABLE`. `cloud` and `hyperwhisper` resolve to `None` here for
//! that reason; they are cloud selectors, not local engines.

/// A canonical transcription engine id — one of the five `openapi.yaml` names
/// outside the cloud-provider set.
///
/// The heads store engine identity in different shapes (macOS has no local
/// engine field at all and carries it in `mode.model`; the .NET heads use
/// `ProviderType` + `LocalEngine`). This enum is the *request's* engine, which is
/// the only part a client sees, so it is the only part that has to be one thing.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum EngineId {
    /// Local `whisper.cpp`. Wire label `whisperLocal`.
    WhisperLocal,
    /// Local Parakeet. Wire label `parakeet`.
    Parakeet,
    /// Local Nemotron. **macOS only** today; the .NET heads resolve the alias
    /// and then answer `ENGINE_UNAVAILABLE`, which is a better answer than
    /// `Unknown engine`.
    Nemotron,
    /// Local Qwen3-ASR. Wire label `qwen3Asr`.
    Qwen3Asr,
    /// Apple's `SpeechAnalyzer`. **macOS only**, same as [`EngineId::Nemotron`].
    AppleSpeech,
}

/// Every engine id, in the order `openapi.yaml` lists them.
pub const ALL_ENGINE_IDS: [EngineId; 5] = [
    EngineId::WhisperLocal,
    EngineId::Parakeet,
    EngineId::Nemotron,
    EngineId::Qwen3Asr,
    EngineId::AppleSpeech,
];

impl EngineId {
    /// The spelling a response's `engine` field carries, and the one
    /// `openapi.yaml` publishes.
    ///
    /// macOS already emits these. Windows and the portable head emit
    /// `qwen3_asr` for [`EngineId::Qwen3Asr`], which is the round-trip break
    /// above: their own answer is not a request macOS accepts. Both should read
    /// the label from here.
    #[must_use]
    pub fn wire_label(self) -> &'static str {
        match self {
            EngineId::WhisperLocal => "whisperLocal",
            EngineId::Parakeet => "parakeet",
            EngineId::Nemotron => "nemotron",
            EngineId::Qwen3Asr => "qwen3Asr",
            EngineId::AppleSpeech => "appleSpeech",
        }
    }

    /// Every accepted spelling of this id, already lowercased.
    ///
    /// The union of all four native tables. Adding one here is what makes a
    /// spelling work on every head at once, which is the whole of item 3.
    fn aliases(self) -> &'static [&'static str] {
        match self {
            // `whisperLocal` lowercases into this list, so the canonical label
            // is accepted too.
            EngineId::WhisperLocal => &["whisperlocal", "whisper", "libwhisper"],
            EngineId::Parakeet => &["parakeet"],
            // `NemotronModelManager.Constants.engineAliases`, which
            // `NemotronLocalAPIEngineTests.swift` pins as an exact set and as
            // all-lowercase.
            EngineId::Nemotron => &[
                "nemotron",
                "nemotronlocal",
                "nemotron-local",
                "nemotron-asr",
            ],
            // `qwen3_asr` and `qwen` come from the .NET heads; `qwen3`,
            // `qwen3asr` and `qwen3-asr` are on all three. macOS gains the first
            // two here, which is what closes the round-trip break.
            EngineId::Qwen3Asr => &["qwen3asr", "qwen3", "qwen3-asr", "qwen3_asr", "qwen"],
            EngineId::AppleSpeech => &[
                "applespeech",
                "apple",
                "apple-speech",
                "apple-speech-analyzer",
                "speech-analyzer",
            ],
        }
    }
}

/// Resolve a caller's `engine` string to a canonical id, or `None` when it is
/// not one of the five.
///
/// Normalisation is **trim, then lowercase**. Lowercasing is what all four
/// tables do; the trim is only the portable head's today, so ` openai` works on
/// Linux and fails elsewhere. Taking the trim closes that, and it can only
/// accept strings that are refused today — no caller loses a spelling.
///
/// `None` is not a rejection. It means "not a local engine": the head's next
/// step is `CloudSttCatalog::normalize_cloud_provider`, which owns the
/// `<CloudProvider rawValue>` half of the documented field, and only after that
/// does an unrecognised string become `ENGINE_UNAVAILABLE`. `cloud` and
/// `hyperwhisper` land here for that reason — see the module docs.
///
/// **Call sites: all three heads, four switches.**
/// `TranscribeEndpoint.applyEngineModel` and
/// `TranscriptionProviderRouter.resolveProvider` on macOS — both, or the drift
/// this closes just moves inside one platform —
/// `TranscribeEndpoints.ApplyEngineModel` on Windows, and
/// `ApplicationLocalApiBackend.ApplyTranscriptionOverrides` on the portable head.
#[must_use]
pub fn resolve_engine_alias(alias: &str) -> Option<EngineId> {
    let normalized = alias.trim().to_lowercase();
    if normalized.is_empty() {
        return None;
    }
    ALL_ENGINE_IDS
        .into_iter()
        .find(|id| id.aliases().contains(&normalized.as_str()))
}

#[cfg(test)]
mod tests {
    use super::{resolve_engine_alias, EngineId, ALL_ENGINE_IDS};

    /// Every alias the four native tables accept, and the id each must produce.
    /// This table *is* item 3 — it is the union, written down once.
    const UNION: [(&str, EngineId); 18] = [
        ("whisper", EngineId::WhisperLocal),
        ("whisperlocal", EngineId::WhisperLocal),
        ("libwhisper", EngineId::WhisperLocal),
        ("parakeet", EngineId::Parakeet),
        ("nemotron", EngineId::Nemotron),
        ("nemotronlocal", EngineId::Nemotron),
        ("nemotron-local", EngineId::Nemotron),
        ("nemotron-asr", EngineId::Nemotron),
        ("qwen3", EngineId::Qwen3Asr),
        ("qwen3asr", EngineId::Qwen3Asr),
        ("qwen3-asr", EngineId::Qwen3Asr),
        ("qwen3_asr", EngineId::Qwen3Asr),
        ("qwen", EngineId::Qwen3Asr),
        ("applespeech", EngineId::AppleSpeech),
        ("apple", EngineId::AppleSpeech),
        ("apple-speech", EngineId::AppleSpeech),
        ("apple-speech-analyzer", EngineId::AppleSpeech),
        ("speech-analyzer", EngineId::AppleSpeech),
    ];

    #[test]
    fn every_native_alias_resolves_to_its_id() {
        for (alias, expected) in UNION {
            assert_eq!(resolve_engine_alias(alias), Some(expected), "{alias}");
        }
        // No alias is claimed by two ids.
        let mut all: Vec<&str> = ALL_ENGINE_IDS
            .into_iter()
            .flat_map(|id| id.aliases().iter().copied())
            .collect();
        assert_eq!(all.len(), UNION.len());
        all.sort_unstable();
        let mut unique = all.clone();
        unique.dedup();
        assert_eq!(all, unique, "an alias is claimed by two engine ids");
    }

    /// The alias tables must already be lowercase, because matching happens
    /// after `to_lowercase`. An upper-case entry would be unreachable.
    /// `NemotronLocalAPIEngineTests.swift` pins the same property on the macOS
    /// side.
    #[test]
    fn every_alias_is_stored_lowercase() {
        for id in ALL_ENGINE_IDS {
            for alias in id.aliases() {
                assert_eq!(*alias, alias.to_lowercase(), "{alias}");
                assert_eq!(*alias, alias.trim(), "{alias}");
            }
        }
    }

    /// The wire labels are `openapi.yaml`'s, and each one resolves back to its
    /// own id.
    ///
    /// This is the round-trip Windows and the portable head break today: they
    /// answer `qwen3_asr`, macOS does not accept that spelling, so a client that
    /// echoes one head's `engine` back to another gets `Unknown engine`.
    #[test]
    fn every_wire_label_round_trips() {
        assert_eq!(
            ALL_ENGINE_IDS.map(EngineId::wire_label).to_vec(),
            vec![
                "whisperLocal",
                "parakeet",
                "nemotron",
                "qwen3Asr",
                "appleSpeech"
            ]
        );
        for id in ALL_ENGINE_IDS {
            assert_eq!(resolve_engine_alias(id.wire_label()), Some(id));
        }
        // And the spelling the .NET heads emit today resolves to the same id,
        // so accepting their old label costs nothing.
        assert_eq!(resolve_engine_alias("qwen3_asr"), Some(EngineId::Qwen3Asr));
    }

    /// Trim then lowercase. The trim is new on macOS and Windows and can only
    /// accept strings they refuse today.
    #[test]
    fn the_input_is_trimmed_then_lowercased() {
        for spelling in [
            "Parakeet",
            "PARAKEET",
            "  parakeet",
            "parakeet  ",
            "\t parakeet \n",
            "\u{00A0}parakeet\u{3000}",
        ] {
            assert_eq!(
                resolve_engine_alias(spelling),
                Some(EngineId::Parakeet),
                "{spelling:?}"
            );
        }
        // `MetaMuseBYOKTests.swift` pins that `engine: "MeTa"` is
        // case-insensitive. Meta is a cloud provider, so it is not one of the
        // five — but the *normalisation* it relies on has to be the same here.
        assert_eq!(
            resolve_engine_alias("WhisperLOCAL"),
            Some(EngineId::WhisperLocal)
        );
    }

    /// `None` means "not one of the five", not "rejected". The cloud half of the
    /// documented field belongs to `hw-catalog`'s `CloudSttCatalog`, and a head
    /// tries that next.
    #[test]
    fn cloud_selectors_are_not_resolved_here() {
        for cloud in [
            "cloud",
            "hyperwhisper",
            "openai",
            "groq",
            "deepgram",
            "assemblyai",
            "elevenlabs",
            "mistral",
            "soniox",
            "gemini",
            "geminitranscribe",
            "grok",
            "meta",
            "microsoftazurespeech",
            "googlespeech",
        ] {
            assert_eq!(resolve_engine_alias(cloud), None, "{cloud}");
        }
    }

    #[test]
    fn nothing_else_resolves() {
        for junk in [
            "",
            "   ",
            "\t\n",
            "whisper local",
            "whisper-local",
            "nemotron_local",
            "qwen4",
            "applespeechanalyzer",
            "parakeet-v3",
            "base",
            "large-v3",
        ] {
            assert_eq!(resolve_engine_alias(junk), None, "{junk:?}");
        }
    }

    /// The empty string takes the early return rather than the table walk, and
    /// must not match an id whose alias list is somehow empty.
    #[test]
    fn an_empty_engine_is_never_an_id() {
        assert_eq!(resolve_engine_alias(""), None);
        assert_eq!(resolve_engine_alias(" \u{3000}\t"), None);
        for id in ALL_ENGINE_IDS {
            assert!(!id.aliases().is_empty(), "{id:?} has no spelling");
        }
    }
}
