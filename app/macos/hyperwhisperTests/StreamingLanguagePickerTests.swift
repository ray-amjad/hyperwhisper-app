//
//  StreamingLanguagePickerTests.swift
//  hyperwhisperTests
//
//  Issue #832: the Streaming settings language picker used to key every cloud
//  provider but xAI on an invented ("hyperwhisper", "nova-3") pair, so an
//  ElevenLabs or OpenAI streaming user was offered Deepgram Nova-3's list.
//  The picker now resolves the selected provider to its shared-catalog entry
//  id and asks the catalog. These tests drive that exact path: the provider →
//  entry mapping, then `LanguageSelectionView.allowedLanguageInfos` with the
//  same arguments `StreamingView.languageSection` passes.
//

import Testing
@testable import HyperWhisper

struct StreamingLanguagePickerTests {

    /// The codes the Streaming settings picker offers for `provider`, built
    /// the way `StreamingView` builds them.
    private func pickerCodes(
        _ provider: StreamingTranscriptionProvider,
        cloudTier: String? = nil
    ) -> [String] {
        LanguageSelectionView.allowedLanguageInfos(
            provider: .cloud,
            model: "cloud",
            cloudProviderId: nil,
            cloudModelId: nil,
            cloudTierId: provider.languageCatalogEntryId(cloudTier: cloudTier)
        ).map(\.code)
    }

    @Test("ElevenLabs streaming offers Amharic and Swahili; Deepgram does not")
    func elevenLabsOffersItsOwnSetAndDeepgramDoesNot() {
        let elevenLabs = Set(pickerCodes(.elevenLabs))
        #expect(elevenLabs.contains("am"), "ElevenLabs Scribe v2 transcribes Amharic")
        #expect(elevenLabs.contains("sw"), "ElevenLabs Scribe v2 transcribes Swahili")

        let deepgram = Set(pickerCodes(.deepgram))
        #expect(!deepgram.contains("am"), "Deepgram Nova-3 does not declare Amharic")
        #expect(!deepgram.contains("sw"), "Deepgram Nova-3 does not declare Swahili")
    }

    @Test("Each cloud streaming provider maps to its own catalog entry")
    func providerMapsToItsCatalogEntry() {
        #expect(StreamingTranscriptionProvider.deepgram.languageCatalogEntryId(cloudTier: nil) == "deepgramNova3")
        #expect(StreamingTranscriptionProvider.elevenLabs.languageCatalogEntryId(cloudTier: nil) == "elevenLabsScribeV2")
        #expect(StreamingTranscriptionProvider.openAI.languageCatalogEntryId(cloudTier: nil) == "openaiWhisper")
        #expect(StreamingTranscriptionProvider.gemini.languageCatalogEntryId(cloudTier: nil) == "geminiTranscribe")
        #expect(StreamingTranscriptionProvider.xai.languageCatalogEntryId(cloudTier: nil) == "grokStt")
        #expect(StreamingTranscriptionProvider.parakeetLocal.languageCatalogEntryId(cloudTier: nil) == nil)
        #expect(StreamingTranscriptionProvider.nemotronLocal.languageCatalogEntryId(cloudTier: nil) == nil)
    }

    @Test("HyperWhisper Cloud follows the selected live tier, clamped like the route")
    func hyperWhisperCloudFollowsTheLiveTier() {
        for entry in CloudSTTCatalog.shared.streamingCloudTierEntries {
            #expect(
                StreamingTranscriptionProvider.hyperwhisperCloud.languageCatalogEntryId(cloudTier: entry.id) == entry.id,
                "\(entry.id): an eligible live tier must reach the picker unchanged"
            )
        }
        #expect(
            StreamingTranscriptionProvider.hyperwhisperCloud.languageCatalogEntryId(cloudTier: "noSuchTier")
                == StreamingCloudTier.defaultCloudTier
        )
    }

    @Test("A provider the catalog leaves unverified keeps the full list")
    func unverifiedProviderKeepsTheFullList() {
        #expect(CloudSTTCatalog.shared.pickerLanguageCodes(forEntryId: "geminiTranscribe") == nil)
        #expect(pickerCodes(.gemini).count == LanguageData.allLanguages.count)
    }

    @Test("Region rows survive by primary subtag, and Automatic stays first")
    func regionRowsSurviveAndAutomaticIsFirst() {
        let codes = pickerCodes(.elevenLabs)
        #expect(codes.first == LanguageData.automaticCode)
        for regional in ["en-GB", "en-US", "pt-BR"]
        where LanguageData.allLanguages.contains(where: { $0.code == regional }) {
            #expect(codes.contains(regional), "\(regional) must survive a tier that declares its primary subtag")
        }
    }
}
