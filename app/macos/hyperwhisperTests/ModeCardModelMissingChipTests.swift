//
//  ModeCardModelMissingChipTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #2036.
//
//  A mode whose on-device model was not downloaded (removed on disk, or a
//  failed restore) kept the green "Offline" chip on its Modes card, so it read
//  as ready while a transcription with it failed with "model not downloaded".
//  The chip now comes from `ModeData.cardStatusChip(isLocalModelDownloaded:)`:
//  a missing local model shows an orange "Model missing" chip instead.
//
//  The decision is a pure `ModeData` helper, tested by calling it. The card
//  needs live model managers, so its wiring is pinned by reading the source.
//

import Foundation
import Testing

@testable import HyperWhisper

@Suite("Mode card model-missing chip (#2036)")
struct ModeCardModelMissingChipTests {

    private static let cardPath = "app/macos/hyperwhisper/Views/Modes/Components/ModeCard.swift"
    private static let key = "modes.badge.modelMissing"

    private func mode(model: String, postProcessing: PostProcessingMode) -> ModeData {
        ModeData(
            name: "W15",
            language: "en",
            model: model,
            punctuation: true,
            capitalization: true,
            profanityFilter: false,
            postProcessingMode: postProcessing
        )
    }

    // MARK: - Decision

    @Test(arguments: [PostProcessingMode.off, .local])
    func anOfflineModeWithItsModelDownloadedStillShowsOffline(_ postProcessing: PostProcessingMode) {
        let data = mode(model: "tiny.en", postProcessing: postProcessing)
        #expect(data.cardStatusChip(isLocalModelDownloaded: true) == .offline)
    }

    @Test(arguments: [PostProcessingMode.off, .local])
    func anOfflineModeWithItsModelMissingShowsModelMissing(_ postProcessing: PostProcessingMode) {
        let data = mode(model: "tiny.en", postProcessing: postProcessing)
        #expect(data.cardStatusChip(isLocalModelDownloaded: false) == .modelMissing)
    }

    @Test func aLocalModeWithCloudPostProcessingFlagsAMissingModelToo() {
        // No Offline chip either way, but the transcription fails the same.
        let data = mode(model: "tiny.en", postProcessing: .cloud)
        #expect(data.cardStatusChip(isLocalModelDownloaded: false) == .modelMissing)
        #expect(data.cardStatusChip(isLocalModelDownloaded: true) == ModeCardStatusChip.none)
    }

    @Test(arguments: ["cloud", "Cloud", " CLOUD ", "", "  "])
    func aCloudModeShowsNoChip(_ model: String) {
        // The card's local-model lookup returns false for a cloud id; that
        // must not read as a missing model, nor as Offline. The router reads
        // any casing, and an empty id, as cloud.
        for postProcessing in [PostProcessingMode.off, .local, .cloud] {
            for downloaded in [false, true] {
                let data = mode(model: model, postProcessing: postProcessing)
                #expect(data.cardStatusChip(isLocalModelDownloaded: downloaded) == ModeCardStatusChip.none,
                        "\(model) \(postProcessing) \(downloaded)")
            }
        }
    }

    // MARK: - Wiring

    @Test func theCardDrawsTheChipFromTheHelperWithTheSameLookupAsTheModelName() throws {
        let source = try ProductionSource.code(of: Self.cardPath)
        #expect(source.contains("modeData.cardStatusChip(isLocalModelDownloaded: isModelDownloaded(modeData.model))"))
        #expect(source.contains("key: \"modes.badge.modelMissing\", color: .orange"))
        #expect(source.contains("key: \"modes.badge.offline\", color: .green"))
        // The lookup reads the id like the router: trimmed, lowercased, and
        // through the legacy Parakeet / Nemotron aliases.
        let lookup = try ProductionSource.slice(
            of: Self.cardPath,
            from: "private func isModelDownloaded(_ rawId: String) -> Bool {",
            to: "@State private var isHovered"
        )
        #expect(lookup.contains("rawId.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()"))
        #expect(lookup.contains("ParakeetModelManager.Constants.canonicalModelId(for: id)"))
        #expect(lookup.contains("NemotronModelManager.Constants.canonicalModelId(for: id)"))
        // The old unconditional gate is gone.
        #expect(!source.contains("if modeData.isOfflineCapable {"))
    }

    // MARK: - Copy

    @Test func everyLocaleHasTheModelMissingTextRightAfterOffline() throws {
        let localizations = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(
            at: localizations,
            includingPropertiesForKeys: nil
        ).filter { $0.pathExtension == "lproj" }
        #expect(locales.count == 40)

        for locale in locales {
            let lines = try ProductionSource.text(of: locale.appendingPathComponent("Localizable.strings"))
                .components(separatedBy: .newlines)
            let matches = lines.indices.filter { lines[$0].hasPrefix("\"\(Self.key)\" = \"") }
            #expect(matches.count == 1, "\(locale.lastPathComponent) needs exactly one \(Self.key)")
            guard let index = matches.first else { continue }
            let line = lines[index]
            #expect(line.trimmingCharacters(in: .whitespaces).hasSuffix("\";"), "\(locale.lastPathComponent)")
            #expect(!line.contains("%"), "\(locale.lastPathComponent) takes no format arguments")
            #expect(line != "\"\(Self.key)\" = \"\";", "\(locale.lastPathComponent) is empty")
            #expect(index > 0 && lines[index - 1].hasPrefix("\"modes.badge.offline\" = \""),
                    "\(locale.lastPathComponent) places it after modes.badge.offline")
        }
    }
}
