//
//  WhisperMissingModelPreparationTests.swift
//  hyperwhisperTests
//
//  Pins issue #1985 — a mode whose on-device Whisper model is not on disk
//  failed preparation as a crash: `loadWhisperModel` let
//  `TranscriptionError.modelNotDownloaded` fall into its generic `catch`, so
//  the status bar showed "Failed to load tiny.en." and every preparation sent a
//  "Model switch failed" Sentry event (HYPERWHISPER-110). The Parakeet path
//  maps the same error to `.unavailable(name:)`, which `ModelStatusBar` draws
//  with a Download button into Model Library.
//
//  `localProvider` is the concrete `LibWhisperProvider`, so a fake cannot be
//  injected; like `ModePreparationKeyTests`, this reads the production source.
//

import Foundation
import Testing
@testable import HyperWhisper

struct WhisperMissingModelPreparationTests {
    private static let file =
        "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionModelManager.swift"

    private static func loadWhisperModelBody() throws -> String {
        try ProductionSource.slice(
            of: file,
            from: "private func loadWhisperModel(_ target: WhisperModel, mode: Mode?) async {",
            to: "private func extractLanguage(from mode: Mode?) -> String? {"
        )
    }

    @Test func missingWhisperModelShowsTheDownloadPromptAndSendsNoEvent() throws {
        let body = try Self.loadWhisperModelBody()
        let arm = try ProductionSource.slice(
            of: Self.file,
            from: "} catch TranscriptionError.modelNotDownloaded {\n            AppLogger.models.warning(\"Whisper",
            to: "} catch {"
        )
        #expect(body.contains("catch TranscriptionError.modelNotDownloaded"))
        #expect(arm.contains("modelReadyState = .unavailable(name: target.name)"))
        #expect(!arm.contains("SentryService"))
        #expect(!arm.contains(".error("))
        #expect(!arm.contains("modelReadyState = .none"))
    }

    @Test func theMissingModelArmComesBeforeTheGenericCatch() throws {
        let body = try Self.loadWhisperModelBody()
        let specific = try #require(body.range(of: "catch TranscriptionError.modelNotDownloaded"))
        let generic = try #require(body.range(of: "} catch {"))
        #expect(specific.lowerBound < generic.lowerBound)
    }

    @Test func aRealLoadFailureStillReportsModelSwitchFailed() throws {
        let body = try Self.loadWhisperModelBody()
        let generic = try #require(body.range(of: "} catch {"))
        let rest = body[generic.upperBound...]
        #expect(rest.contains("modelReadyState = .none"))
        #expect(rest.contains("SentryService.capture(error: error, message: \"Model switch failed\""))
    }
}
