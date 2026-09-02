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
//     Mode. `resolveProvider` and `applyEngineModel` are two hand-copied
//     switches over the same spellings, and drifting them is exactly the bug
//     class this file exists for.
//  2. `engineLabel(forMode:)` has to keep saying "nemotron", including on the
//     `mode_id` path that never goes near an `engine=` string. That path is
//     the issue's first comment, where a saved Nemotron mode reported
//     `"engine": "whisperLocal"`.
//
//  `resolveProvider` and `HealthEndpoint.handle` are NOT tested here: both are
//  `@MainActor` and want live providers / `Bundle.main`, and no test in this
//  target stands up that fixture. The router arm is covered by inspection
//  against the `parakeet` arm it copies.
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

    // MARK: - TranscribeEndpoint.applyEngineModel(to:engine:model:)

    /// Every accepted spelling of `engine=`, including the mixed case the
    /// switch normalizes away. `"nemotron"` and `"nemotronLocal"` are the two
    /// the issue's repro actually sent.
    @Test func everyNemotronEngineAliasSelectsTheDefaultVariant() {
        let persistence = PersistenceController(inMemory: true)
        let aliases = [
            "nemotron", "Nemotron", "NEMOTRON",
            "nemotronlocal", "nemotronLocal",
            "nemotron-local", "Nemotron-Local",
            "nemotron-asr", "NEMOTRON-ASR",
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
