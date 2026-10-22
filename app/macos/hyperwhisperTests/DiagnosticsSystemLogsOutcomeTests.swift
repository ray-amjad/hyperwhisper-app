//
//  DiagnosticsSystemLogsOutcomeTests.swift
//  hyperwhisperTests
//
//  Export Diagnostics zipped a 0-byte system-logs.txt and still said every
//  log was exported (#1480): a build that may not read the log store gets
//  `log show` exit 77, an empty stdout and the reason on stderr, and the
//  reader ignored all three. These tests point the reader at fake children
//  through the `LogShowCommand` seam and check what it reports and writes.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Export Diagnostics reports a failed or empty log show")
struct DiagnosticsSystemLogsOutcomeTests {

    static func sh(_ script: String) -> AppLogger.LogShowCommand {
        AppLogger.LogShowCommand(executableURL: URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", script])
    }

    static func run(_ script: String) throws -> (AppLogger.SystemLogsOutcome, String) {
        let logFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("system-logs-1480-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: logFile) }
        let outcome = try AppLogger.writeSystemLogs(to: logFile, command: sh(script))
        return (outcome, try String(contentsOf: logFile, encoding: .utf8))
    }

    @Test func aDeniedLogStoreIsReportedWithItsReason() throws {
        let (outcome, file) = try Self.run(
            "echo 'log: Could not open local log store: Operation not permitted' >&2; exit 77"
        )
        guard case .unavailable(let reason) = outcome else {
            Issue.record("exit 77 was reported as written")
            return
        }
        #expect(reason.contains("77"))
        #expect(reason.contains("Operation not permitted"))
        #expect(file.contains("Operation not permitted"), "system-logs.txt must say why it holds no logs")
    }

    @Test func aFailedRunWithOutputIsStillAFailure() throws {
        let (outcome, _) = try Self.run("echo '[{\"eventMessage\":\"x\"}]'; exit 1")
        #expect(outcome != .written)
    }

    @Test func anEmptySuccessIsNotReportedAsWritten() throws {
        let (outcome, file) = try Self.run("exit 0")
        #expect(outcome != .written)
        #expect(!file.isEmpty)
    }

    @Test func anEmptyJSONArrayIsNotReportedAsWritten() throws {
        let (outcome, _) = try Self.run("printf '[\\n\\n]\\n'")
        #expect(outcome != .written)
    }

    @Test func realLogLinesAreWrittenAsIs() throws {
        let json = "[{\"eventMessage\":\"hyperwhisper-1480\"}]"
        let (outcome, file) = try Self.run("printf '%s' '\(json)'; echo 'a warning' >&2")
        #expect(outcome == .written)
        #expect(file == json)
    }

    @Test func stderrPastThePipeBufferDoesNotHang() async throws {
        // stderr is now a pipe; an undrained one would block the child.
        let logFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("system-logs-1480-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: logFile) }
        let done = DispatchSemaphore(value: 0)
        let command = Self.sh("/usr/bin/yes stderr-1480 | /usr/bin/head -c 300000 >&2; exit 3")

        Thread.detachNewThread {
            _ = try? AppLogger.writeSystemLogs(to: logFile, command: command)
            done.signal()
        }

        let finished = await waitOffThePool(for: done, seconds: 10)
        #expect(finished, "the reader hung on a child that writes more stderr than a pipe holds")
    }
}
