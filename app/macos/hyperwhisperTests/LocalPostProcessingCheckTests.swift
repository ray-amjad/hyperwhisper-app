//
//  LocalPostProcessingCheckTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1858: the Mode editor saved Post Processing
//  Local with no on-device LLM (`postProcessingMode = 2`, empty
//  `languageModel`), and the next open silently showed Off. Save is now
//  disabled while `LocalPostProcessingCheck.isBroken` holds, the same rule
//  the editor's onAppear repair uses.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Local post-processing check")
struct LocalPostProcessingCheckTests {

    private let modelId = "qwen3-1.7b"

    private func isBroken(
        _ mode: PostProcessingMode,
        languageModel: String,
        downloaded: Set<String> = [],
        downloading: Set<String> = [],
        checksumInvalid: Set<String> = [],
        isAppleSilicon: Bool = true
    ) -> Bool {
        LocalPostProcessingCheck.isBroken(
            mode: mode,
            languageModel: languageModel,
            downloadedIds: downloaded,
            downloadingIds: downloading,
            checksumInvalidIds: checksumInvalid,
            isAppleSilicon: isAppleSilicon
        )
    }

    @Test("Local with no model id is broken (the #1858 row)")
    func localEmptyIdIsBroken() {
        #expect(isBroken(.local, languageModel: ""))
        #expect(isBroken(.local, languageModel: "  \n"))
    }

    @Test("Local with a model that is not downloaded is broken")
    func localNotDownloadedIsBroken() {
        #expect(isBroken(.local, languageModel: modelId, downloaded: ["other-model"]))
    }

    @Test("Local with a model mid-download keeps the user's intent")
    func localDownloadingIsNotBroken() {
        #expect(!isBroken(.local, languageModel: modelId, downloading: [modelId]))
    }

    @Test("Local with a downloaded model is not broken")
    func localDownloadedIsNotBroken() {
        #expect(!isBroken(.local, languageModel: modelId, downloaded: [modelId]))
        #expect(!isBroken(.local, languageModel: " \(modelId) ", downloaded: [modelId]))
    }

    @Test("Local with a downloaded model that failed its checksum is broken")
    func localChecksumInvalidIsBroken() {
        #expect(isBroken(.local, languageModel: modelId, downloaded: [modelId], checksumInvalid: [modelId]))
    }

    @Test("Cloud and Off are never broken, whatever the local state")
    func cloudAndOffNeverBroken() {
        for mode in [PostProcessingMode.cloud, .off] {
            #expect(!isBroken(mode, languageModel: ""))
            #expect(!isBroken(mode, languageModel: modelId, isAppleSilicon: false))
        }
    }

    @Test("Local on Intel hardware is broken even with a downloaded model")
    func localOnIntelIsBroken() {
        #expect(isBroken(.local, languageModel: modelId, downloaded: [modelId], isAppleSilicon: false))
    }
}
