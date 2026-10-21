//
//  Diag2345LoadTests.swift — THROWAWAY probe for issue #2345, never merged.
//
//  Runs the real flaky tests while every Swift concurrency pool thread is
//  kept busy (as the full suite's CPU-heavy tests do on a 3-core runner),
//  or while the main actor is held (as a waiting @MainActor test does).
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite(.serialized) @MainActor struct Diag2345LoadTests {
    /// Spinners on the pool at the tests' own priority, yielding often.
    private func busyPool(for seconds: Double) -> [Task<Void, Never>] {
        let end = ContinuousClock.now.advanced(by: .seconds(seconds))
        return (0..<(ProcessInfo.processInfo.activeProcessorCount * 2)).map { _ in
            Task.detached(priority: .medium) {
                while ContinuousClock.now < end {
                    let burst = ContinuousClock.now.advanced(by: .milliseconds(100))
                    var x = 0
                    while ContinuousClock.now < burst { x &+= 1 }
                    await Task.yield()
                }
            }
        }
    }

    private func drain(_ tasks: [Task<Void, Never>]) async {
        for t in tasks { await t.value }
    }

    @Test func saveTakesLocksBeforeWritingWhilePoolBusy() async throws {
        let spinners = busyPool(for: 15)
        try await Task.sleep(for: .milliseconds(500))
        let started = Date()
        do {
            try await BackupImportSQLiteRefusalTests().saveTakesLocksBeforeWriting()
            print("PROBE save passed in \(Date().timeIntervalSince(started))")
        } catch {
            Issue.record("PROBE save threw after \(Date().timeIntervalSince(started)) s: \(error)")
        }
        await drain(spinners)
    }

    @Test func releasedWriteLockPassesWhilePoolBusy() async throws {
        let spinners = busyPool(for: 15)
        try await Task.sleep(for: .milliseconds(500))
        let started = Date()
        do {
            try await BackupImportSQLiteRefusalTests().releasedWriteLockPasses()
        } catch {
            Issue.record("PROBE released threw after \(Date().timeIntervalSince(started)) s: \(error)")
        }
        await drain(spinners)
    }

    @Test func freePersistedPortWhilePoolBusy() async throws {
        let spinners = busyPool(for: 15)
        try await Task.sleep(for: .milliseconds(500))
        try await LocalAPIServerPortPreferenceTests().aFreePersistedPortIsTheOneTheServerListensOn()
        await drain(spinners)
    }

    @Test func freePersistedPortWhileMainHeld10s() async throws {
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(30))
            Thread.sleep(forTimeInterval: 10)
        }
        try await LocalAPIServerPortPreferenceTests().aFreePersistedPortIsTheOneTheServerListensOn()
    }
}
