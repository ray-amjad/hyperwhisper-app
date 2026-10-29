//
//  Qwen3NemotronMissingModelPreparationTests.swift
//  hyperwhisperTests
//
//  Pins issue #1988, the sibling of #1985 — a mode whose Qwen3 ASR or
//  Nemotron 3.5 weights are not on disk failed preparation as a load fault:
//  both providers throw `TranscriptionError.modelNotDownloaded`, but the
//  `prepareModel` arms let it fall into their generic `catch`, so the status
//  bar showed "No Model" with no Download button, and the Qwen3 arm could send
//  a "Qwen3 ASR prepare failed" Sentry event. Parakeet and Whisper map the
//  same error to `.unavailable(name:)`, which `ModelStatusBar` draws with a
//  Download button into Model Library.
//
//  The providers are concrete types cast inside `prepareModel`, so a fake
//  cannot be injected; like `WhisperMissingModelPreparationTests`, this reads
//  the production source.
//

import Foundation
import Testing
@testable import HyperWhisper

struct Qwen3NemotronMissingModelPreparationTests {
    private static let file =
        "app/macos/hyperwhisper/Managers/Transcription/Coordinators/TranscriptionModelManager.swift"

    private static func qwen3Arm() throws -> String {
        try ProductionSource.slice(
            of: file,
            from: "isQwen3AsrModel(modelId),",
            to: "isNemotronModel(modelId),"
        )
    }

    private static func nemotronArm() throws -> String {
        try ProductionSource.slice(
            of: file,
            from: "isNemotronModel(modelId),",
            to: "guard let target = mapModelIdToWhisperModel(modelId) else {"
        )
    }

    /// The `catch TranscriptionError.modelNotDownloaded` arm of `body`, up to
    /// the generic `catch` that follows it.
    private static func missingModelCatch(in body: String) throws -> Substring {
        let specific = try #require(body.range(of: "} catch TranscriptionError.modelNotDownloaded {"))
        let rest = body[specific.upperBound...]
        let generic = try #require(rest.range(of: "} catch {"))
        return rest[..<generic.lowerBound]
    }

    @Test func missingQwen3ModelShowsTheDownloadPromptAndSendsNoEvent() throws {
        let arm = try Self.missingModelCatch(in: Self.qwen3Arm())
        #expect(arm.contains("modelReadyState = .unavailable(name: displayName)"))
        #expect(!arm.contains("SentryService"))
        #expect(!arm.contains(".error("))
        #expect(!arm.contains("modelReadyState = .none"))
    }

    @Test func missingNemotronModelShowsTheDownloadPrompt() throws {
        let arm = try Self.missingModelCatch(in: Self.nemotronArm())
        #expect(arm.contains("modelReadyState = .unavailable(name: displayName)"))
        #expect(!arm.contains("SentryService"))
        #expect(!arm.contains(".error("))
        #expect(!arm.contains("modelReadyState = .none"))
    }

    @Test func theMissingModelArmsComeBeforeTheGenericCatch() throws {
        for body in [try Self.qwen3Arm(), try Self.nemotronArm()] {
            let specific = try #require(body.range(of: "catch TranscriptionError.modelNotDownloaded"))
            let generic = try #require(body.range(of: "} catch {"))
            #expect(specific.lowerBound < generic.lowerBound)
        }
    }

    @Test func aRealLoadFailureStillTakesTheErrorPath() throws {
        let qwen3 = try Self.qwen3Arm()
        let qwen3Generic = try #require(qwen3.range(of: "} catch {"))
        let qwen3Rest = qwen3[qwen3Generic.upperBound...]
        #expect(qwen3Rest.contains("modelReadyState = .none"))
        #expect(qwen3Rest.contains("SentryService.capture(error: error, message: \"Qwen3 ASR prepare failed\""))

        // A broken Nemotron variant (#1632) must still leave Loading for "No Model".
        let nemotron = try Self.nemotronArm()
        let nemotronGeneric = try #require(nemotron.range(of: "} catch {"))
        #expect(nemotron[nemotronGeneric.upperBound...].contains("modelReadyState = .none"))
    }
}
