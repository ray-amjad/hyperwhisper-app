//
//  NemotronLocalAPIEngineTests.swift
//  hyperwhisperTests
//
//  Issue #376: Nemotron 3.5 was implemented end to end — a real
//  `NemotronProvider` is injected into `TranscriptionProviderRouter` and
//  `selectLocalProvider` already routes `nemotron-asr-3.5-*` to it — but the
//  Local API could not name it. `engine=nemotron` fell through the
//  `default:` arm of both hand-maintained engine switches, and a Mode whose
//  `model` was already a Nemotron id fell through `engineLabel`'s final
//  `return "whisperLocal"`.
//
//  So there are two properties worth pinning here, and they are different:
//
//  1. The engine ALIASES have to keep landing a canonical Nemotron id on the
//     Mode. `resolveProvider` and `applyEngineModel` were two hand-copied
//     switches over the same spellings; they now match the one
//     `NemotronModelManager.Constants.engineAliases` set, so they cannot
//     drift, and `theSharedEngineAliasSetIsPinned` guards the set itself.
//  2. `engineLabel(forMode:)` has to keep saying "nemotron", including on the
//     `mode_id` path that never goes near an `engine=` string. That path is
//     the issue's first comment, where a saved Nemotron mode reported
//     `"engine": "whisperLocal"`.
//  3. The mixed `mode_id` + `engine` form must not swap the variant the
//     caller saved — review round 1's regression, where re-asserting
//     `engine=nemotron` on a Latin mode silently selected multilingual.
//
//  `resolveProvider` and `HealthEndpoint.handle` are NOT tested here: both are
//  `@MainActor` and want live providers / `Bundle.main`, and no test in this
//  target stands up that fixture. The router arm's model handling is covered
//  by inspection against the `parakeet` arm it copies; its alias handling is
//  covered structurally, by reading the same constant this file pins.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct NemotronLocalAPIEngineTests {

    // MARK: - Constants.canonicalModelId(for:)

    @Test func canonicalIdentifiersRoundTripBothVariants() {
        #expect(
            NemotronModelManager.Constants.canonicalModelId(
                for: NemotronModelManager.Constants.latinModelId
            ) == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.canonicalModelId(
                for: NemotronModelManager.Constants.multilingualModelId
            ) == NemotronModelManager.Constants.multilingualModelId
        )
        // The literals, so a rename of either constant cannot quietly rewrite
        // the wire values `Mode.model` already holds on disk.
        #expect(NemotronModelManager.Constants.latinModelId == "nemotron-asr-3.5-latin")
        #expect(NemotronModelManager.Constants.multilingualModelId == "nemotron-asr-3.5-multilingual")
    }

    @Test func canonicalIdentifiersToleratePersistedCaseAndWhitespace() {
        #expect(
            NemotronModelManager.Constants.canonicalModelId(for: "  NEMOTRON-ASR-3.5-LATIN  ")
                == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.canonicalModelId(for: " Nemotron-ASR-3.5-Multilingual ")
                == NemotronModelManager.Constants.multilingualModelId
        )
    }

    /// The anti-coercion property Parakeet's equivalent documents: an unknown
    /// id must NOT resolve to a real variant, or a typo becomes a silent run
    /// against the wrong model.
    @Test func unknownAndTypoIdentifiersAreNotCoercedToARealVariant() {
        let unknown = [
            "nemotron-3.5-latin",     // the spelling in the issue's own curl; exists nowhere
            "nemotron-3.5-ml-560ms",  // the Windows/Linux/.NET id for a different model
            "nemotron-asr-3.5-",      // the bare prefix the router matches on
            "nemotron",               // the engine alias, not a model id
            "typo",
            "",
        ]
        for id in unknown {
            #expect(
                NemotronModelManager.Constants.canonicalModelId(for: id) == nil,
                "'\(id)' must not resolve to an installable Nemotron variant"
            )
        }
    }

    // MARK: - Constants.modelIdForSelection(_:)

    @Test func missingAndBlankSelectionModelIdsDefaultToMultilingual() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(nil)
                == NemotronModelManager.Constants.multilingualModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("")
                == NemotronModelManager.Constants.multilingualModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("   ")
                == NemotronModelManager.Constants.multilingualModelId
        )
    }

    @Test func knownSelectionIdentifiersNormalizeToTheCanonicalId() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(
                NemotronModelManager.Constants.latinModelId
            ) == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("  NEMOTRON-ASR-3.5-LATIN ")
                == NemotronModelManager.Constants.latinModelId
        )
        #expect(
            NemotronModelManager.Constants.modelIdForSelection(
                NemotronModelManager.Constants.multilingualModelId
            ) == NemotronModelManager.Constants.multilingualModelId
        )
    }

    /// An unknown explicit value survives unchanged rather than being replaced
    /// by the default, so the router can reject the id the caller actually
    /// sent instead of transcribing with a model nobody asked for.
    @Test func unknownSelectionIdentifiersSurviveUnchanged() {
        #expect(
            NemotronModelManager.Constants.modelIdForSelection("nemotron-3.5-latin")
                == "nemotron-3.5-latin"
        )
        #expect(NemotronModelManager.Constants.modelIdForSelection("  typo  ") == "typo")
    }

    // MARK: - Constants.engineAliases

    /// Review round 1. `resolveProvider` and `applyEngineModel` used to hold
    /// literal copies of these four spellings, and only `applyEngineModel` is
    /// reachable from this target — deleting `"nemotron-asr"` from the router
    /// left every test green while `POST /transcribe {"engine":"nemotron-asr"}`
    /// started answering `Unknown engine`. Both switches now match this one
    /// set, so they cannot drift; this test pins the set itself, which is the
    /// only thing left that can change silently.
    @Test func theSharedEngineAliasSetIsPinned() {
        #expect(
            NemotronModelManager.Constants.engineAliases == [
                "nemotron",
                "nemotronlocal",
                "nemotron-local",
                "nemotron-asr",
            ],
            "an engine spelling was added or removed — the OpenAPI contract's `engine` description must match"
        )

        // Both switches lowercase before matching, so an entry that is not
        // already lowercase is dead and would silently accept nothing.
        for alias in NemotronModelManager.Constants.engineAliases {
            #expect(alias == alias.lowercased(), "'\(alias)' can never be matched")
        }

        // The label `/transcribe` reports back must itself be an accepted
        // spelling, so a client can feed a response's `engine` straight into
        // the next request.
        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.model = NemotronModelManager.Constants.multilingualModelId
        let label = TranscribeEndpoint.engineLabel(forMode: mode)
        #expect(
            NemotronModelManager.Constants.engineAliases.contains(label.lowercased()),
            "the response label '\(label)' is not an engine spelling the API accepts back"
        )
    }

    // MARK: - TranscribeEndpoint.applyEngineModel(to:engine:model:)

    /// Every accepted spelling of `engine=`, including the mixed case the
    /// switch normalizes away. `"nemotron"` and `"nemotronLocal"` are the two
    /// the issue's repro actually sent. Driven off `Constants.engineAliases`
    /// so a spelling added to the shared list is exercised here without
    /// anyone remembering to widen this array.
    @Test func everyNemotronEngineAliasSelectsTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)
        let aliases = Array(NemotronModelManager.Constants.engineAliases) + [
            "Nemotron", "NEMOTRON",
            "nemotronLocal",
            "Nemotron-Local",
            "NEMOTRON-ASR",
        ]

        for alias in aliases {
            let mode = Mode(context: persistence.container.viewContext)
            TranscribeEndpoint.applyEngineModel(to: mode, engine: alias, model: nil)
            #expect(
                mode.model == NemotronModelManager.Constants.multilingualModelId,
                """
                engine '\(alias)' left mode.model as '\(mode.model ?? "nil")'. An alias that \
                misses the switch falls through `default:`, which with no `model` leaves the \
                Mode untouched and reports the wrong engine on the response.
                """
            )
            #expect(TranscribeEndpoint.engineLabel(forMode: mode) == "nemotron")
        }
    }

    /// An explicit `model=` beats the default, in either direction, and is
    /// canonicalised on the way in.
    @Test func anExplicitModelBeatsTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)

        let latin = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(
            to: latin,
            engine: "nemotron",
            model: NemotronModelManager.Constants.latinModelId
        )
        #expect(latin.model == NemotronModelManager.Constants.latinModelId)
        #expect(TranscribeEndpoint.engineLabel(forMode: latin) == "nemotron")

        let multilingual = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(
            to: multilingual,
            engine: "nemotron-asr",
            model: "  NEMOTRON-ASR-3.5-MULTILINGUAL  "
        )
        #expect(multilingual.model == NemotronModelManager.Constants.multilingualModelId)
        #expect(TranscribeEndpoint.engineLabel(forMode: multilingual) == "nemotron")

        // A blank model is not an override — it falls back to the default.
        let blank = Mode(context: persistence.container.viewContext)
        TranscribeEndpoint.applyEngineModel(to: blank, engine: "nemotron", model: "   ")
        #expect(blank.model == NemotronModelManager.Constants.multilingualModelId)
    }

    /// Review round 1, the mixed `mode_id` + `engine` form. `makeTransientMode`
    /// copies `baseline.model` onto the transient Mode BEFORE calling
    /// `applyEngineModel`, so on that path the Mode already carries the
    /// variant the user saved. Re-asserting `engine=nemotron` with no `model`
    /// must not swap it: a caller with only the Latin variant installed
    /// otherwise gets MODEL_NOT_INSTALLED for the multilingual default they
    /// never asked for.
    @Test func anInheritedNemotronVariantSurvivesAnEngineOnlyOverride() {
        let persistence = PersistenceController(inMemory: true)
        let aliases = ["nemotron", "nemotronLocal", "nemotron-local", "nemotron-asr"]

        for alias in aliases {
            for inherited in [
                NemotronModelManager.Constants.latinModelId,
                NemotronModelManager.Constants.multilingualModelId,
            ] {
                let mode = Mode(context: persistence.container.viewContext)
                mode.model = inherited
                TranscribeEndpoint.applyEngineModel(to: mode, engine: alias, model: nil)
                #expect(
                    mode.model == inherited,
                    "engine '\(alias)' with no model replaced the inherited '\(inherited)' with '\(mode.model ?? "nil")'"
                )

                // A blank/whitespace model is not an override either, so it
                // must inherit on exactly the same terms as nil.
                let blank = Mode(context: persistence.container.viewContext)
                blank.model = inherited
                TranscribeEndpoint.applyEngineModel(to: blank, engine: alias, model: "   ")
                #expect(blank.model == inherited)
            }
        }

        // An inherited id written by an older build in a different case is
        // still recognised, and is normalised to the canonical spelling.
        let shouted = Mode(context: persistence.container.viewContext)
        shouted.model = NemotronModelManager.Constants.latinModelId.uppercased()
        TranscribeEndpoint.applyEngineModel(to: shouted, engine: "nemotron", model: nil)
        #expect(shouted.model == NemotronModelManager.Constants.latinModelId)

        // An explicit model still beats the inherited one — inheriting is only
        // the no-model fallback, not a veto on the request.
        let overridden = Mode(context: persistence.container.viewContext)
        overridden.model = NemotronModelManager.Constants.latinModelId
        TranscribeEndpoint.applyEngineModel(
            to: overridden,
            engine: "nemotron",
            model: NemotronModelManager.Constants.multilingualModelId
        )
        #expect(overridden.model == NemotronModelManager.Constants.multilingualModelId)
    }

    /// The other half of the inherit rule: only a REAL Nemotron variant is
    /// inheritable. Anything else on the baseline Mode — the Core Data default
    /// "base", another engine's id, or a prefix-alike that names no variant —
    /// is not a Nemotron selection, so `engine=nemotron` falls back to the
    /// engine default rather than carrying a foreign id into the router.
    @Test func aNonNemotronInheritedModelFallsBackToTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)
        let foreign = [
            "base",                       // the Core Data default on a fresh Mode
            "large-v3-turbo",
            ParakeetModelManager.Constants.v2ModelId,
            Qwen3AsrModelManager.Constants.modelId,
            "apple-speech-analyzer",
            "cloud",
            "nemotron-asr-3.5-bogus",     // prefix-alike, names no real variant
            "",
        ]

        for inherited in foreign {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = inherited
            TranscribeEndpoint.applyEngineModel(to: mode, engine: "nemotron", model: nil)
            #expect(
                mode.model == NemotronModelManager.Constants.multilingualModelId,
                "inherited '\(inherited)' should not have been kept as a Nemotron selection"
            )
            #expect(TranscribeEndpoint.engineLabel(forMode: mode) == "nemotron")
        }
    }

    // MARK: - TranscribeEndpoint.engineLabel(forMode:)

    /// The direct regression test for the issue's first comment: a saved Mode
    /// whose `model` is a Nemotron id reported `"engine": "whisperLocal"`,
    /// because Nemotron had no branch and fell to the chain's fallback. Both
    /// variants must report `"nemotron"` — the label the alias list also
    /// accepts back, so a client can round-trip it.
    @Test func bothNemotronVariantsLabelAsNemotron() {
        let persistence = PersistenceController(inMemory: true)
        let ids = [
            NemotronModelManager.Constants.latinModelId,
            NemotronModelManager.Constants.multilingualModelId,
        ]

        for id in ids {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = id
            let label = TranscribeEndpoint.engineLabel(forMode: mode)
            #expect(
                label == "nemotron",
                "a Mode saved on '\(id)' reported engine '\(label)'"
            )
            // Case is normalized before the prefix test, so a Mode written by
            // an older build with a shouted id still labels correctly.
            let shouted = Mode(context: persistence.container.viewContext)
            shouted.model = id.uppercased()
            #expect(TranscribeEndpoint.engineLabel(forMode: shouted) == "nemotron")
        }
    }

    /// The guard on the four labels that were already there. The Nemotron
    /// branch was inserted into the middle of an if-chain whose last line is
    /// an unconditional `return "whisperLocal"`, so the fallback arm is the
    /// thing most at risk from a mis-ordered or over-broad prefix test.
    @Test func thePreExistingEngineLabelsAreUnchanged() {
        let persistence = PersistenceController(inMemory: true)

        func label(model: String, cloudProvider: String? = nil) -> String {
            let mode = Mode(context: persistence.container.viewContext)
            mode.model = model
            mode.cloudProvider = cloudProvider
            return TranscribeEndpoint.engineLabel(forMode: mode)
        }

        // Cloud: the provider name, or the literal when there is none.
        #expect(label(model: "cloud", cloudProvider: CloudProvider.openai.rawValue) == "openai")
        #expect(label(model: "cloud") == "cloud")
        #expect(label(model: "") == "cloud")

        #expect(label(model: ParakeetModelManager.Constants.v2ModelId) == "parakeet")
        #expect(label(model: ParakeetModelManager.Constants.v3ModelId) == "parakeet")
        #expect(label(model: "apple-speech-analyzer") == "appleSpeech")
        #expect(label(model: Qwen3AsrModelManager.Constants.modelId) == "qwen3Asr")

        // And the fallback still catches everything else, which on this path
        // means the whisper.cpp model names.
        #expect(label(model: "base") == "whisperLocal")
        #expect(label(model: "large-v3-turbo") == "whisperLocal")
        // Including a near-miss Nemotron spelling: the prefix test is exact,
        // so an unrecognised id must not be labelled as a Nemotron run.
        #expect(label(model: "nemotron-3.5-latin") == "whisperLocal")
    }
}
