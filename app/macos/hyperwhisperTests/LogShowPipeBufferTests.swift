//
//  LogShowPipeBufferTests.swift
//  hyperwhisperTests
//
//  A pipe holds about 64 KB. Both `log show` sites in AppLogger used to call
//  `waitUntilExit()` before reading the pipe, so a child that wrote more
//  blocked on write, never exited, and the caller hung (#991). The real log
//  store cannot be made to produce that much on demand, so these tests point
//  each site at a fake child that writes 300 000 bytes and fail if the read
//  does not finish within a few seconds. With the old wait-then-read order
//  both tests time out.
//

import Foundation
import os
import Testing
@testable import HyperWhisper

@Suite("log show output past the pipe buffer does not hang")
struct LogShowPipeBufferTests {

    static let byteCount = 300_000
    static let line = "hyperwhisper-991-pipe-fill"

    /// Writes `byteCount` bytes of `line` rows to stdout, then exits.
    static var fakeChild: AppLogger.LogShowCommand {
        AppLogger.LogShowCommand(
            executableURL: URL(fileURLWithPath: "/bin/sh"),
            arguments: ["-c", "/usr/bin/yes \(line) | /usr/bin/head -c \(byteCount)"]
        )
    }

    @Test func getRecentLogsReturnsOutputPastThePipeBuffer() async {
        let result = OSAllocatedUnfairLock<String?>(initialState: nil)
        let done = DispatchSemaphore(value: 0)
        let command = Self.fakeChild

        Thread.detachNewThread {
            let logs = AppLogger.getRecentLogs(minutes: 5, maxLines: 100, command: command)
            result.withLock { $0 = logs }
            done.signal()
        }

        let finished = await waitOffThePool(for: done, seconds: 10)
        #expect(finished, "getRecentLogs hung on a child that writes more than a pipe holds")
        let logs = result.withLock { $0 }
        #expect(logs?.contains(Self.line) == true)
        #expect(logs?.components(separatedBy: "\n").count == 100)
    }

    @Test func exportDiagnosticsReaderWritesOutputPastThePipeBuffer() async throws {
        let logFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("log-show-pipe-\(UUID().uuidString).txt")
        defer { try? FileManager.default.removeItem(at: logFile) }

        let failure = OSAllocatedUnfairLock<String?>(initialState: nil)
        let done = DispatchSemaphore(value: 0)
        let command = Self.fakeChild

        Thread.detachNewThread {
            do {
                try AppLogger.writeSystemLogs(to: logFile, command: command)
            } catch {
                let message = String(describing: error)
                failure.withLock { $0 = message }
            }
            done.signal()
        }

        let finished = await waitOffThePool(for: done, seconds: 10)
        #expect(finished, "the exportDiagnostics reader hung on a child that writes more than a pipe holds")
        #expect(failure.withLock { $0 } == nil)
        let data = try Data(contentsOf: logFile)
        #expect(data.count == Self.byteCount)
        #expect(String(decoding: data.prefix(64), as: UTF8.self).hasPrefix(Self.line))
    }
}
