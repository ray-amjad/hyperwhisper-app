//
//  DiagnosticLogStateKeysTests.swift
//  hyperwhisperTests
//
//  `AppLogger.getRecentLogs` publishes `diagnostic_logs_*` Sentry scope extras
//  once when the `log show` fetch starts and once when it ends.
//  `SentryService.setExtras` never removes a key, so the `running` publish used
//  to write 2 keys and an AppHang during a fetch carried the PREVIOUS fetch's
//  outcome, timing and line count; a `finished` publish with no exit code kept
//  the previous exit code (#1205). Every publish must write the same key set.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("diagnostic_logs_* publishes write every key")
struct DiagnosticLogStateKeysTests {

    static let allKeys: Set<String> = [
        "diagnostic_logs_state",
        "diagnostic_logs_on_main_thread",
        "diagnostic_logs_outcome",
        "diagnostic_logs_fetch_ms",
        "diagnostic_logs_line_count",
        "diagnostic_logs_exit_code",
        "diagnostic_logs_was_slow"
    ]

    static func running() -> [String: Any] {
        AppLogger.diagnosticLogStateExtras(onMainThread: false, result: nil)
    }

    static func finished(exitCode: Int?, fetchMs: Int = 120) -> [String: Any] {
        AppLogger.diagnosticLogStateExtras(
            onMainThread: true,
            result: AppLogger.DiagnosticLogFetchResult(
                outcome: "ok",
                fetchMs: fetchMs,
                lineCount: 42,
                exitCode: exitCode
            )
        )
    }

    @Test func runningAndFinishedWriteTheSameKeys() {
        let running = Set(Self.running().keys)
        #expect(running == Self.allKeys)
        #expect(Set(Self.finished(exitCode: 0).keys) == running)
        #expect(Set(Self.finished(exitCode: 1).keys) == running)
        #expect(Set(Self.finished(exitCode: nil).keys) == running)
    }

    /// A running fetch has no outcome, timing, line count or exit code yet;
    /// each is a sentinel, so nothing from the previous fetch survives.
    @Test func runningWritesSentinels() {
        let extras = Self.running()
        #expect(extras["diagnostic_logs_state"] as? String == "running")
        #expect(extras["diagnostic_logs_on_main_thread"] as? Bool == false)
        #expect(extras["diagnostic_logs_outcome"] as? String == "none")
        #expect(extras["diagnostic_logs_fetch_ms"] as? Int == -1)
        #expect(extras["diagnostic_logs_line_count"] as? Int == -1)
        #expect(extras["diagnostic_logs_exit_code"] as? Int == -1)
        #expect(extras["diagnostic_logs_was_slow"] as? Bool == false)
    }

    @Test func finishedWritesItsValues() {
        let extras = Self.finished(exitCode: 3)
        #expect(extras["diagnostic_logs_state"] as? String == "finished")
        #expect(extras["diagnostic_logs_on_main_thread"] as? Bool == true)
        #expect(extras["diagnostic_logs_outcome"] as? String == "ok")
        #expect(extras["diagnostic_logs_fetch_ms"] as? Int == 120)
        #expect(extras["diagnostic_logs_line_count"] as? Int == 42)
        #expect(extras["diagnostic_logs_exit_code"] as? Int == 3)
        #expect(extras["diagnostic_logs_was_slow"] as? Bool == false)
    }

    /// A fetch that never launched has no exit code; the sentinel overwrites
    /// the previous fetch's code.
    @Test func finishedWithoutExitCodeWritesSentinel() {
        #expect(Self.finished(exitCode: nil)["diagnostic_logs_exit_code"] as? Int == -1)
    }

    @Test func slowFetchIsMarkedSlow() {
        #expect(Self.finished(exitCode: 0, fetchMs: 2_000)["diagnostic_logs_was_slow"] as? Bool == true)
        #expect(Self.finished(exitCode: 0, fetchMs: 1_999)["diagnostic_logs_was_slow"] as? Bool == false)
    }
}
