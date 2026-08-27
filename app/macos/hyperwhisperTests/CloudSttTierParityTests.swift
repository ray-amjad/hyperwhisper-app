//
//  CloudSttTierParityTests.swift
//  hyperwhisperTests
//
//  Guards the one seam where `shared-app-classification/cloud-stt-catalog.json`
//  stops being data-only: the Provider dropdown is built straight from the
//  catalog's `cloudTierEligible` entries, but the selection is PERSISTED and
//  routed through the hand-written `CloudAccuracyTier` enum. A catalog-only edge
//  (a 12th cloud-tier entry with no matching enum case) would therefore ship a
//  selectable row that silently resolves to `fromStorageValue`'s Deepgram
//  default — wrong X-STT-Provider, wrong credits, wrong vocabulary support.
//
//  The Windows counterpart lives in HyperWhisper.SmokeTests/Program.cs
//  ("Every cloudTierEligible catalog id has a CloudAccuracyTier case").
//

import Foundation
import Testing
@testable import HyperWhisper

struct CloudSttTierParityTests {

    /// The catalog as the shared Rust core reads it. This used to decode the
    /// repo's JSON file directly through a second, macOS-only decoder — that
    /// decoder is gone (issue #280), and the core `include_str!`s the same file
    /// a catalog edit touches, so this still asserts against the source of
    /// truth and not against a stale bundled copy.
    private func repoCatalog() -> CloudSTTCatalog { CloudSTTCatalog.shared }

    @Test("Every cloudTierEligible catalog id has a CloudAccuracyTier case")
    func everyCloudTierIdHasAnEnumCase() throws {
        let entries = repoCatalog().cloudTierEntries
        #expect(!entries.isEmpty, "catalog exposed no cloudTierEligible providers")

        for entry in entries {
            #expect(
                CloudAccuracyTier(rawValue: entry.id) != nil,
                """
                catalog id '\(entry.id)' has no CloudAccuracyTier case, so its Provider row \
                would be visible but unselectable on macOS and would route as Deepgram on \
                Windows. Add the case to ModeModels.swift (and Models/CloudAccuracyTier.cs) \
                in the same change as the catalog entry.
                """
            )
        }
    }

    @Test("Every CloudAccuracyTier case resolves to a cloud-tier catalog entry")
    func everyEnumCaseHasACatalogEntry() throws {
        let catalog = repoCatalog()
        let ids = Set(catalog.cloudTierEntries.map(\.id))

        for tier in CloudAccuracyTier.allCases {
            #expect(
                ids.contains(tier.rawValue),
                """
                CloudAccuracyTier.\(tier.rawValue) has no cloudTierEligible catalog entry, so \
                the tier can be persisted but never offered — and its credits, models and \
                vocabulary flags all resolve to nothing.
                """
            )
        }
    }

    /// Grok's API takes no `model` parameter, so its single registry entry is
    /// stored under the empty id. The Model row is now a one-item dropdown like
    /// every other provider's, so that id has to resolve through the
    /// provider-scoped lookup or the name, the description and the mode card all
    /// render blank. The Windows counterpart lives in HyperWhisper.SmokeTests
    /// ("Grok's empty model id resolves through a provider-scoped lookup").
    @Test("Grok's empty model id resolves through a provider-scoped lookup")
    func grokEmptyModelIdResolves() {
        let grok = CloudTranscriptionModels.model(withId: "", provider: .grok)
        #expect(grok != nil, "the Grok Model row would render blank")
        #expect(grok?.displayName == "Grok Speech-to-Text")
        #expect(
            CloudTranscriptionModels.displayName(for: "", provider: .grok) == "Grok Speech-to-Text")

        // Unscoped, "" stays ambiguous: any provider left without a model would
        // otherwise resolve to Grok.
        #expect(CloudTranscriptionModels.model(withId: "") == nil)
    }

    @Test("Retired cloud model ids resolve to selectable canonical models")
    func retiredCloudModelIdsResolve() {
        let cases: [(String, CloudProvider, String)] = [
            ("slam-1", .assemblyAI, "universal-3-5-pro"),
            ("universal-3-pro", .assemblyAI, "universal-3-5-pro"),
            ("universal-3-pro-medical", .assemblyAI, "universal-3-5-pro-medical"),
            ("stt-async-v4", .soniox, "stt-async-v5"),
            ("gemini-3.1-flash-lite-preview", .gemini, "gemini-3.1-flash-lite"),
        ]

        for (oldId, provider, replacement) in cases {
            #expect(CloudTranscriptionModels.resolveModelAlias(oldId, provider: provider) == replacement)
            #expect(CloudTranscriptionModels.model(withId: replacement, provider: provider) != nil)
        }
    }

    /// Gemini 3.5 Transcribe (`geminiTranscribe`) is a separate provider from
    /// `gemini`: different API, different key slot, different default model. It
    /// has no legacy aliases, so what needs pinning is that its default model id
    /// survives the alias dispatcher untouched (it relies on the `default:` arm,
    /// scoped AND unscoped) and that the WebSocket-only live model never leaks
    /// into the pre-recorded registry.
    ///
    /// NOTE: no row was added to `retiredCloudModelIdsResolve` above, and none
    /// belongs there — that table pins *retired* ids resolving to their
    /// replacements, and this provider is new, so it has no retired ids.
    @Test("Gemini 3.5 Transcribe's model id passes through the alias dispatcher")
    func geminiTranscribeModelIdPassesThrough() {
        let defaultId = CloudTranscriptionModels.defaultModel(for: .geminiTranscribe)
        #expect(defaultId == "gemini-3.5-transcribe")

        // The default must actually be selectable, or the Mode editor's model
        // dropdown is empty and the provider cannot be used.
        #expect(
            CloudTranscriptionModels.model(withId: defaultId, provider: .geminiTranscribe) != nil,
            "the default model must exist in availableModels"
        )
        #expect(
            CloudTranscriptionModels.resolveModelAlias(defaultId, provider: .geminiTranscribe) == defaultId)
        #expect(CloudTranscriptionModels.resolveModelAlias(defaultId, provider: nil) == defaultId)

        // `gemini-3.5-transcribe-live` speaks BidiGenerateContent over a
        // WebSocket; the core's REST builder rejects it with a 400. Offering it
        // as a pre-recorded model would ship a routable-but-unserved id.
        #expect(
            CloudTranscriptionModels.availableModels.allSatisfy { $0.id != "gemini-3.5-transcribe-live" },
            "the live model is WebSocket-only and must not be selectable for pre-recorded transcription"
        )
    }

    /// Catalog v8 retired `googleChirp3` and put `geminiTranscribe` in its array
    /// slot as Google's cloud tier. Every persisted Chirp value in the wild has
    /// to land there — and the failure mode is silent: `fromStorageValue`'s
    /// final `return` is `.deepgramNova3`, so a missing `migrateFrom` alias
    /// moves the user to a different vendor, at different credits, with no error
    /// and no UI change they'd notice until the bill.
    ///
    /// `PersistenceController.migrateGoogleChirp3TierIfNeeded` rewrites the
    /// stored rows once at launch, but this read path is what protects backups,
    /// Local API writes and any row the one-shot missed.
    @Test("A persisted googleChirp3 tier migrates onto geminiTranscribe, never Deepgram")
    func retiredChirpTierMigratesOntoGeminiTranscribe() {
        let legacy = [
            "googleChirp3", "googlechirp3", "GOOGLECHIRP3", " googleChirp3 ",
            "googlechirp", "google-chirp", "chirp", "chirp_3",
            "googlespeech", "googleSpeech",
        ]

        for value in legacy {
            let tier = CloudAccuracyTier.fromStorageValue(value)
            #expect(
                tier == .geminiTranscribe,
                """
                legacy tier '\(value)' resolved to .\(tier.rawValue). It must land on \
                .geminiTranscribe — .deepgramNova3 is fromStorageValue's silent fallback, \
                which would move every Chirp 3 user to a different vendor. Fix the \
                `migrateFrom` list on the geminiTranscribe entry in \
                shared-app-classification/cloud-stt-catalog.json.
                """
            )
        }

        // The old raw value must NOT come back as a case: the canonical loop at
        // the top of `fromStorageValue` runs before the catalog alias lookup, so
        // a re-added case would shadow the migration and strand the user on a
        // tier with no catalog entry.
        #expect(
            CloudAccuracyTier(rawValue: "googleChirp3") == nil,
            "re-adding the googleChirp3 case would shadow the catalog migration"
        )
        #expect(CloudSTTCatalog.shared.entry(byId: "googleChirp3") == nil)

        // And the tier the user lands on has to be usable, not just parseable.
        #expect(CloudAccuracyTier.geminiTranscribe.defaultModelId == "gemini-3.5-transcribe")
        #expect(CloudAccuracyTier.geminiTranscribe.sttProvider == "gemini-transcribe")
        #expect(
            CloudAccuracyTier.defaultTier(forVendorKey: "google")?.rawValue == "geminiTranscribe",
            """
            the merged Google Provider row defaults to its FIRST catalog entry, so \
            geminiTranscribe must keep Chirp's array index — otherwise picking "Google" \
            selects the BYOK `gemini` LLM tier.
            """
        )
    }

    @Test("The streaming cloud-tier picker offers exactly the routed vendors")
    func streamingEligibleTiersAreExactlyTheRoutedVendors() {
        // The guard that stops someone flipping a `models[].streaming` flag on a
        // vendor HyperWhisper Cloud serves no WebSocket route for and shipping a
        // 404 at dictation time. The STT catalog has no `enabled` gate to hide a
        // half-finished vendor behind, and the client derives its route as
        // `/ws/streaming-{sttProvider}` with no allow-list of its own — so this
        // list IS the allow-list. Widen it only alongside the backend route.
        #expect(
            CloudAccuracyTier.streamingEligibleTiers.map(\.rawValue) == ["deepgramNova3", "geminiTranscribe"]
        )

        // Every offered tier must render a label and derive a route.
        for tier in CloudAccuracyTier.streamingEligibleTiers {
            #expect(!tier.sttProvider.isEmpty)
            #expect(CloudSTTCatalog.shared.entry(byId: tier.rawValue)?.access?.cloudTierEligible == true)
        }

        // Route derivation, including the fallback. deepgramNova3 must reproduce
        // the literal the strategy hard-coded before the picker existed, because
        // every already-installed client persists no tier at all.
        #expect(HyperWhisperCloudStrategy.resolveSttProvider("deepgramNova3") == "deepgram")
        #expect(HyperWhisperCloudStrategy.resolveSttProvider("geminiTranscribe") == "gemini-transcribe")
        for bogus in [nil, "", "   ", "notATier", "groqWhisper"] as [String?] {
            #expect(HyperWhisperCloudStrategy.resolveSttProvider(bogus) == "deepgram")
        }

        // The same clamp the settings Picker binds through. A value it cannot
        // render as a tag shows a BLANK row, and a backup restore or a Local API
        // write can both put one there, so the clamp has to hold on the id too
        // and not only on the derived route.
        #expect(HyperWhisperCloudStrategy.normalizedCloudTier(" geminiTranscribe ") == "geminiTranscribe")
        #expect(HyperWhisperCloudStrategy.normalizedCloudTier("GEMINITRANSCRIBE") == "geminiTranscribe")
        for bogus in [nil, "", "   ", "notATier", "groqWhisper"] as [String?] {
            #expect(HyperWhisperCloudStrategy.normalizedCloudTier(bogus) == "deepgramNova3")
        }
        #expect(
            CloudAccuracyTier.streamingEligibleTiers.map(\.rawValue)
                .contains(HyperWhisperCloudStrategy.defaultCloudTier),
            "the fallback must itself be offered, or the picker renders blank by default"
        )

        // The auto-detect vocabulary gate is Deepgram's constraint alone.
        #expect(HyperWhisperCloudStrategy.tierRequiresLanguageForVocabulary("deepgramNova3"))
        #expect(!HyperWhisperCloudStrategy.tierRequiresLanguageForVocabulary("geminiTranscribe"))
    }

    @Test("Retired cloud models are not selectable")
    func retiredCloudModelsAreNotSelectable() {
        let retired = Set([
            "universal-3-pro", "universal-3-pro-medical",
            "stt-async-v4", "gemini-3.1-flash-lite-preview",
        ])
        #expect(CloudTranscriptionModels.availableModels.allSatisfy { !retired.contains($0.id) })
    }
}
