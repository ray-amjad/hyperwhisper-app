//
//  RecordingsDirectoryTests.swift
//  hyperwhisperTests
//

import Foundation
import Testing

@testable import HyperWhisper

struct RecordingsDirectoryTests {
    @Test func configuredPathResolvesAsDirectoryURL() {
        let path = "/tmp/hyperwhisper-recordings"

        #expect(
            RecordingsDirectory.resolve(configuredPath: path)
                == URL(fileURLWithPath: path, isDirectory: true)
        )
    }

    @Test func nilPathUsesLegacyDocumentsFallback() {
        #expect(RecordingsDirectory.resolve(configuredPath: nil) == legacyFallback)
    }

    @Test func emptyPathUsesLegacyDocumentsFallback() {
        #expect(RecordingsDirectory.resolve(configuredPath: "") == legacyFallback)
    }

    @Test func whitespacePathRemainsConfigured() {
        let path = "   "

        #expect(
            RecordingsDirectory.resolve(configuredPath: path)
                == URL(fileURLWithPath: path, isDirectory: true)
        )
    }

    @Test func relativePathKeepsURLInitializerBehavior() {
        let path = "relative/recordings"

        #expect(
            RecordingsDirectory.resolve(configuredPath: path)
                == URL(fileURLWithPath: path, isDirectory: true)
        )
    }

    private var legacyFallback: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Recordings", isDirectory: true)
    }
}
