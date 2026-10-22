//
//  ModeEditorByokVocabularyWarningTests.swift
//  hyperwhisperTests
//
//  Boundary test for issue #681: the Mode editor's BYOK "custom vocabulary not
//  supported" notice used to re-derive the answer from hard-coded model ids
//  (ElevenLabs scribe_v1, Deepgram base*/whisper*), a second copy of what
//  `shared-models/models-catalog.json` already says. Every id those literals
//  named had been retired and aliased away, so the notice was dead and nothing
//  noticed. `ModeEditorView.byokVocabularyUnsupported(provider:modelId:)` now
//  reads the catalog; these tests pin it to the catalog for every BYOK model
//  the picker can offer.
//
//  The Windows counterpart lives in HyperWhisper.SmokeTests/Program.cs
//  ("the Mode editor's BYOK vocabulary warning reads the shared catalog").
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Mode editor BYOK vocabulary warning")
struct ModeEditorByokVocabularyWarningTests {

    /// Every BYOK (non-HyperWhisper-Cloud) row in the registry the Mode editor
    /// picker is built from (`CloudTranscriptionModels.models(for:)` filters
    /// this list by provider and availability).
    private static var byokModels: [CloudTranscriptionModel] {
        CloudTranscriptionModels.availableModels.filter { $0.provider != .hyperwhisper }
    }

    @MainActor
    @Test("Every BYOK picker model has a catalog row, so none silently loses its warning")
    func everyByokModelIsCatalogued() {
        #expect(!Self.byokModels.isEmpty)
        for model in Self.byokModels {
            let key = SharedModelsCatalog.providerKey(model.provider)
            #expect(
                SharedModelsCatalog.entry(provider: key, kind: .voice, id: model.id) != nil,
                "\(key)/\(model.id) has no voice row in shared-models/models-catalog.json"
            )
        }
    }

    @MainActor
    @Test("The BYOK warning agrees with the catalog for every BYOK picker model")
    func warningMatchesCatalogForEveryByokModel() {
        for model in Self.byokModels {
            let key = SharedModelsCatalog.providerKey(model.provider)
            let catalogSupports = SharedModelsCatalog.supportsCustomVocabulary(
                provider: key, kind: .voice, id: model.id
            )
            #expect(
                ModeEditorView.byokVocabularyUnsupported(provider: model.provider, modelId: model.id)
                    == !catalogSupports,
                "\(key)/\(model.id): the Mode editor warning disagrees with the catalog"
            )
        }
    }

    @MainActor
    @Test("Known catalog rows: scribe_v1 warns, scribe_v2 and nova-3-general do not")
    func knownRows() {
        #expect(ModeEditorView.byokVocabularyUnsupported(provider: .elevenLabs, modelId: "scribe_v1"))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .elevenLabs, modelId: "scribe_v2"))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .deepgram, modelId: "nova-3-general"))
    }

    @MainActor
    @Test("An unknown or empty model id shows no warning instead of guessing")
    func unknownOrEmptyIdDoesNotWarn() {
        // `supportsCustomVocabulary` answers false on a catalog miss; the
        // predicate must not turn that miss into a warning.
        #expect(!SharedModelsCatalog.supportsCustomVocabulary(provider: "elevenLabs", kind: .voice, id: "not-a-model"))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .elevenLabs, modelId: "not-a-model"))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .deepgram, modelId: "not-a-model"))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .elevenLabs, modelId: ""))
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .deepgram, modelId: ""))
    }

    @MainActor
    @Test("HyperWhisper Cloud is never a BYOK warning")
    func hyperWhisperCloudIsNotByok() {
        // The HW Cloud branch has its own tier/model check; this predicate
        // must stay out of it even for a model id the catalog says lacks vocab.
        #expect(!ModeEditorView.byokVocabularyUnsupported(provider: .hyperwhisper, modelId: "scribe_v1"))
    }
}
