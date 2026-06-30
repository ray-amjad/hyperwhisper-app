//
//  LlamaProcessIdentityTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing
@testable import HyperWhisper

struct LlamaProcessIdentityTests {

    @Test func pidFileParserAcceptsIdentityRecord() throws {
        let record = LlamaServerPIDRecord(
            pid: 123,
            executablePath: "/Applications/HyperWhisper.app/Contents/Resources/Runtime/llama-server",
            startTime: LlamaProcessStartTime(seconds: 1_772_000_000, microseconds: 123_456)
        )

        let data = try LlamaProcessIdentity.encodePIDRecord(record)

        #expect(LlamaProcessIdentity.parsePIDFileData(data) == .record(record))
    }

    @Test func pidFileParserTreatsBarePIDAsLegacyOnly() {
        let data = Data("123\n".utf8)

        #expect(LlamaProcessIdentity.parsePIDFileData(data) == .legacyPID(123))
    }

    @Test func pidFileParserRejectsInvalidContents() {
        let data = Data("not-a-pid".utf8)

        #expect(LlamaProcessIdentity.parsePIDFileData(data) == .invalid)
    }

    @Test func canonicalizedPathNormalizesDotSegments() {
        let path = "/tmp/hyperwhisper/runtime/../runtime/./llama-server"

        #expect(LlamaProcessIdentity.canonicalizedPath(path) == "/tmp/hyperwhisper/runtime/llama-server")
    }

    @Test func knownRuntimePathRejectsUnrelatedExecutable() {
        #expect(!LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath("/bin/sleep"))
    }

    @Test func currentRuntimePathRejectsMovedBundlePath() {
        let movedBundlePath = "/Users/example/Downloads/HyperWhisper.app/Contents/Resources/Runtime/llama-server"

        #expect(!LlamaProcessIdentity.isKnownHyperWhisperLlamaServerPath(movedBundlePath))
    }

    @Test func trackedRuntimePathAcceptsMovedBundlePIDRecordPath() {
        let movedBundlePath = "/Users/example/Downloads/HyperWhisper.app/Contents/Resources/Runtime/llama-server"

        #expect(LlamaProcessIdentity.isTrackedHyperWhisperLlamaServerPath(movedBundlePath))
    }

    @Test func trackedRuntimePathRejectsUnrelatedLlamaServer() {
        #expect(!LlamaProcessIdentity.isTrackedHyperWhisperLlamaServerPath("/opt/homebrew/bin/llama-server"))
    }
}
