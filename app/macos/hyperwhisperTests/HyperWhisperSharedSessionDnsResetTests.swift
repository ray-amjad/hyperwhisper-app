//
//  HyperWhisperSharedSessionDnsResetTests.swift
//  hyperwhisperTests
//
//  Locks the one 60 s window on `HyperWhisperRoutedTranscription.sharedSession`
//  DNS resets (#962). A reset cancels every in-flight upload on the session, so
//  the HyperWhisper Cloud warmup, the Cloud transcribe retry and the routed
//  (Azure MAI, Google Chirp) transcribe retry must all go through one gate.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite("Shared-session DNS reset window (#962)", .serialized)
struct HyperWhisperSharedSessionDnsResetTests {

    private static let cloudProviderPath = "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/HyperWhisperCloudProvider.swift"
    private static let routedPath = "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/HyperWhisperRoutedTranscription.swift"
    private static let azurePath = "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/AzureMAIProvider.swift"
    private static let chirpPath = "app/macos/hyperwhisper/Managers/Transcription/Providers/Cloud/GoogleChirpProvider.swift"

    /// A fake clock and a reset counter. The real session is never reset.
    private final class ResetHarness {
        var now = Date(timeIntervalSinceReferenceDate: 1_000_000)
        var resets = 0
        var resetSessions: [URLSession] = []
    }

    /// Points the window at a fake clock and a counting reset action, starts it
    /// from "never reset", runs `body`, then puts the production seams back.
    private func withHarness(_ body: (ResetHarness) async throws -> Void) async rethrows {
        let savedLastReset = HyperWhisperRoutedTranscription.lastDnsResetAt
        let savedClock = HyperWhisperRoutedTranscription.dnsResetClock
        let savedAction = HyperWhisperRoutedTranscription.dnsResetAction
        defer {
            HyperWhisperRoutedTranscription.lastDnsResetAt = savedLastReset
            HyperWhisperRoutedTranscription.dnsResetClock = savedClock
            HyperWhisperRoutedTranscription.dnsResetAction = savedAction
        }

        let harness = ResetHarness()
        HyperWhisperRoutedTranscription.lastDnsResetAt = .distantPast
        HyperWhisperRoutedTranscription.dnsResetClock = { harness.now }
        HyperWhisperRoutedTranscription.dnsResetAction = { session in
            harness.resets += 1
            harness.resetSessions.append(session)
        }
        try await body(harness)
    }

    private static func occurrences(of needle: String, in haystack: String) -> Int {
        haystack.components(separatedBy: needle).count - 1
    }

    // MARK: - The window

    @Test func twoCallsInsideTheWindowResetOnce() async {
        await withHarness { harness in
            let firstAt = harness.now
            #expect(await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure())
            #expect(harness.resets == 1)
            #expect(harness.resetSessions.allSatisfy { $0 === HyperWhisperRoutedTranscription.sharedSession })

            harness.now = firstAt.addingTimeInterval(59)
            #expect(await !HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure(), "the second call inside 60 s is a no-op")
            #expect(harness.resets == 1)
            #expect(HyperWhisperRoutedTranscription.lastDnsResetAt == firstAt, "an absorbed call must not move the window")
        }
    }

    @Test func aCallAfterTheWindowResetsAgain() async {
        await withHarness { harness in
            let firstAt = harness.now
            await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure()
            #expect(harness.resets == 1)

            harness.now = firstAt.addingTimeInterval(HyperWhisperRoutedTranscription.minDnsResetInterval)
            #expect(await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure())
            #expect(harness.resets == 2)
            #expect(HyperWhisperRoutedTranscription.lastDnsResetAt == harness.now)
        }
    }

    @Test func theWindowIsSixtySeconds() {
        #expect(HyperWhisperRoutedTranscription.minDnsResetInterval == 60)
    }

    // MARK: - Cloud and routed share it

    /// A Cloud-path flush (warmup) followed by a routed-path flush (Azure MAI /
    /// Google Chirp retry) 10 s later: one reset, not two. Both paths call the
    /// same function; `everyResetCallSiteGoesThroughTheSharedWindow` below
    /// proves they do.
    @Test func aCloudCallAndARoutedCallShareTheWindow() async {
        await withHarness { harness in
            let cloudAt = harness.now
            #expect(await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure())

            harness.now = cloudAt.addingTimeInterval(10)
            #expect(await !HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure())
            #expect(harness.resets == 1)
        }
    }

    /// The warmup, the Cloud `onTransportError` and the routed `onTransportError`
    /// all reach the session through the gated function, and nothing else in
    /// those files resets the session directly.
    @Test func everyResetCallSiteGoesThroughTheSharedWindow() throws {
        let warmup = try ProductionSource.slice(
            of: Self.cloudProviderPath,
            from: "private func sendWarmup() {",
            to: "private static let maxRetryAttempts = 4"
        )
        #expect(warmup.contains("await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure()"))

        let cloud = try ProductionSource.code(of: Self.cloudProviderPath)
        #expect(cloud.contains("await HyperWhisperRoutedTranscription.resetSharedSessionAfterDnsFailure()"), "the Cloud transcribe retry")
        #expect(Self.occurrences(of: "resetSharedSessionAfterDnsFailure()", in: cloud) == 2, "warmup + transcribe retry")

        let routed = try ProductionSource.code(of: Self.routedPath)
        #expect(routed.contains("await Self.resetSharedSessionAfterDnsFailure()"), "the routed transcribe retry")
        #expect(Self.occurrences(of: ".reset {", in: routed) == 1, "only the gated reset action touches the session")

        for path in [Self.cloudProviderPath, Self.azurePath, Self.chirpPath] {
            let code = try ProductionSource.code(of: path)
            #expect(!code.contains(".reset {"), "\(path) resets the shared session around the window")
            #expect(!code.contains(".reset("), "\(path) resets the shared session around the window")
        }
    }

    // MARK: - isDnsError

    @Test func onlyDnsShapedErrorsCountAsDnsErrors() {
        #expect(HyperWhisperRoutedTranscription.isDnsError(URLError(.dnsLookupFailed)))
        #expect(HyperWhisperRoutedTranscription.isDnsError(URLError(.cannotFindHost)))
        #expect(HyperWhisperRoutedTranscription.isDnsError(URLError(.cannotConnectToHost)))
        #expect(!HyperWhisperRoutedTranscription.isDnsError(URLError(.timedOut)))
        #expect(!HyperWhisperRoutedTranscription.isDnsError(URLError(.networkConnectionLost)))
    }
}
