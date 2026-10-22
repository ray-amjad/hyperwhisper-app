//
//  ModelLibrarySearchTests.swift
//  hyperwhisperTests
//
//  #1447: the Model Library search box matches every word of a multi-word
//  query (AND), each in the name, provider, tag or model id, and matches the
//  model id the Local API shows (`base.en`, `gpt-4o-transcribe`).
//

import Foundation
import Testing
@testable import HyperWhisper

struct ModelLibrarySearchTests {

    private static func row(
        id: String,
        displayName: String,
        providerKey: LibraryProviderKey,
        kind: LibraryModelKind = .voice,
        tag: String? = nil
    ) -> LibraryModel {
        LibraryModel(
            id: id,
            displayName: displayName,
            providerKey: providerKey,
            kind: kind,
            location: .offline(sizeDescription: nil, installed: false, downloadProgress: nil),
            speed: 3,
            accuracy: 3,
            tag: tag,
            status: .downloadable(progress: nil),
            supportsCustomVocabulary: false,
            availableViaHyperWhisperCloud: false
        )
    }

    // Row ids are built the way ModelLibraryManager builds them.
    private static let nemotronLatin = row(
        id: "nemotron-nemotron-asr-3.5-latin", displayName: "Nemotron 3.5 (Latin)",
        providerKey: .nemotron, tag: "Latin")
    private static let nemotronMulti = row(
        id: "nemotron-nemotron-asr-3.5-multilingual", displayName: "Nemotron 3.5 (Multilingual)",
        providerKey: .nemotron, tag: "Multilingual")
    private static let whisperBaseEn = row(
        id: "whisper-base.en", displayName: "Base (English-only)", providerKey: .localWhisper)
    private static let whisperSmall = row(
        id: "whisper-small", displayName: "Small", providerKey: .localWhisper)
    private static let gpt4o = row(
        id: "cloud-tx-openai-gpt-4o-transcribe", displayName: "GPT-4o Transcribe",
        providerKey: .cloud(.openai))
    private static let ppModel = row(
        id: "pp-openai-gpt-4.1-mini", displayName: "GPT-4.1 Mini",
        providerKey: .postProcessing(.openai), kind: .text)

    private static let all = [nemotronLatin, nemotronMulti, whisperBaseEn, whisperSmall, gpt4o, ppModel]

    private static func search(_ query: String) -> [String] {
        all.filter { ModelLibrarySearch.matches($0, query: query) }.map(\.id)
    }

    @Test func everyWordMustMatchSomewhere() {
        #expect(Self.search("NEMOTRON latin") == [Self.nemotronLatin.id])
        #expect(Self.search("  latin   nemotron ") == [Self.nemotronLatin.id])
        // Words may match different fields: provider + tag.
        #expect(Self.search("nvidia multilingual") == [Self.nemotronMulti.id])
        #expect(Self.search("nemotron whisper").isEmpty)
    }

    @Test func modelIdMatchesForLocalAndCloud() {
        #expect(Self.search("base.en") == [Self.whisperBaseEn.id])
        #expect(Self.search("gpt-4o-transcribe") == [Self.gpt4o.id])
        #expect(Self.search("gpt-4.1-mini") == [Self.ppModel.id])
        #expect(Self.search("asr-3.5-latin") == [Self.nemotronLatin.id])
    }

    @Test func syntheticRowIdPrefixesDoNotMatch() {
        #expect(Self.search("cloud-tx").isEmpty)
        #expect(Self.search("pp-").isEmpty)
        #expect(ModelLibrarySearch.modelId(of: Self.whisperBaseEn) == "base.en")
        #expect(ModelLibrarySearch.modelId(of: Self.gpt4o) == "gpt-4o-transcribe")
        #expect(ModelLibrarySearch.modelId(of: Self.nemotronLatin) == "nemotron-asr-3.5-latin")
    }

    @Test func singleWordAndEmptyQueriesKeepWorking() {
        #expect(Self.search("NEMOTRON") == [Self.nemotronLatin.id, Self.nemotronMulti.id])
        #expect(Self.search("   ") == Self.all.map(\.id))
        #expect(Self.search("").count == Self.all.count)
        #expect(Self.search("zzqx").isEmpty)
    }
}
