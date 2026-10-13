//
//  HistoryRetryMissingAudioTests.swift
//  hyperwhisperTests
//
//  #1455: on a History row whose audio file was moved or deleted, the context
//  menu still offered "Retry with…", and choosing a mode did nothing. The menu
//  was gated on the path string alone, and the handler's missing-file guard
//  set `lastError`, which no History view reads, then returned.
//
//  The menu itself only shows in a real window, so these tests pin the source:
//  the context-menu retry items are disabled when the file is not on disk, and
//  every retry guard that can fail on a menu click shows the error.
//

import Foundation
import Testing

@testable import HyperWhisper

struct HistoryRetryMissingAudioTests {

    private static let historyView = "app/macos/hyperwhisper/Views/HistoryView.swift"
    private static let actionHandler =
        "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift"

    @Test func theContextMenuDisablesRetryWhenTheAudioFileIsGone() throws {
        let menu = try ProductionSource.slice(
            of: Self.historyView,
            from: "private func contextMenuItems(for item: HistoryItemSnapshot)",
            to: "private var searchBar"
        )
        #expect(menu.contains("let audioOnDisk = audioFileIsOnDisk(item)"), "\(menu)")

        let retryWith = try #require(menu.range(of: "history.context.retryWith"))
        let afterRetryWith = menu[retryWith.upperBound...]
        let copy = try #require(afterRetryWith.range(of: "history.context.copy"))
        #expect(afterRetryWith[..<copy.lowerBound].contains(".disabled(!audioOnDisk)"), "\(menu)")

        let retry = try #require(menu.range(of: "history.context.retry\""))
        #expect(menu[retry.upperBound..<retryWith.lowerBound].contains("!audioOnDisk"), "\(menu)")

        let check = try ProductionSource.slice(
            of: Self.historyView,
            from: "private func audioFileIsOnDisk(_ item: HistoryItemSnapshot) -> Bool",
            to: "private func isRetrying"
        )
        #expect(check.contains("FileManager.default.fileExists(atPath:"), "\(check)")
    }

    @Test func retryWithAMissingFileShowsTheError() throws {
        let retryWith = try ProductionSource.slice(
            of: Self.actionHandler,
            from: "func retryTranscription(_ transcript: Transcript, with mode: Mode) async -> Bool",
            to: "let newTranscript = persistenceController.createProcessingTranscript("
        )
        let missingGuard = try #require(retryWith.range(of: "FileManager.default.fileExists(atPath: originalAudioPath) else {"))
        let afterGuard = retryWith[missingGuard.upperBound...]
        let firstReturn = try #require(afterGuard.range(of: "return false"))
        #expect(afterGuard[..<firstReturn.lowerBound].contains("await showRetryErrorAlert("), "\(retryWith)")
        #expect(!retryWith.contains("transcripts.error.audioNotFound"), "key is not in any Localizable.strings")
        #expect(!retryWith.contains("transcripts.error.fileCopyFailed"), "key is not in any Localizable.strings")

        let copyFailure = try #require(retryWith.range(of: "} catch {"))
        #expect(retryWith[copyFailure.upperBound...].contains("await showRetryErrorAlert("), "\(retryWith)")
    }

    @Test func plainRetryThatCannotRunShowsTheError() throws {
        let retry = try ProductionSource.slice(
            of: Self.actionHandler,
            from: "func retryTranscription(_ transcript: Transcript) async -> Bool",
            to: "guard let transcriptId = transcript.id,"
        )
        #expect(retry.contains("\"transcripts.error.cannotRetry\".localized"), "\(retry)")
        #expect(retry.contains("await showRetryErrorAlert("), "\(retry)")
    }
}
