//
//  SentryRecentLogsThreadTests.swift
//  hyperwhisperTests
//
//  An Event's `recent_logs` come from a `log show` subprocess that blocks
//  until it exits. Reached from the main thread, that parked the UI inside
//  error reporting (#991). `SentryService.withRecentLogs` now skips the fetch
//  on the main thread and runs `send` (where `SentrySDK.capture` lives) inline
//  on the caller's thread, so the event keeps the call site's stack. These
//  tests inject the fetch, so no subprocess and no SDK start.
//

import Foundation
import os
import Testing
@testable import HyperWhisper

@Suite("Sentry recent logs are never fetched on the main thread")
struct SentryRecentLogsThreadTests {

    /// What one `withRecentLogs` call did, recorded on the thread that made it.
    struct Recorded: Sendable {
        var callerWasMainThread = false
        var fetched = false
        var sendCount = 0
        var sentOnCallerThread = false
        var recentLogs: String?
        var skipped: String?
    }

    /// Off the main thread: the fetch runs, and `send` runs on the same
    /// thread, before `withRecentLogs` returns.
    @Test func offMainThreadFetchesAndSendsOnTheCallersThread() async {
        let result = OSAllocatedUnfairLock(initialState: Recorded())
        let done = DispatchSemaphore(value: 0)

        Thread.detachNewThread {
            let caller = Thread.current
            var recorded = Recorded(callerWasMainThread: Thread.isMainThread)
            SentryService.withRecentLogs(true, fetch: {
                recorded.fetched = true
                return "logs"
            }) { extras in
                recorded.sendCount += 1
                recorded.sentOnCallerThread = Thread.current === caller
                recorded.recentLogs = extras["recent_logs"] as? String
                recorded.skipped = extras["recent_logs_skipped"] as? String
            }
            let snapshot = recorded
            result.withLock { $0 = snapshot }
            done.signal()
        }

        #expect(await waitOffThePool(for: done, seconds: 10))
        let recorded = result.withLock { $0 }
        #expect(recorded.callerWasMainThread == false)
        #expect(recorded.fetched == true)
        #expect(recorded.sendCount == 1)
        #expect(recorded.sentOnCallerThread == true)
        #expect(recorded.recentLogs == "logs")
        #expect(recorded.skipped == nil)
    }

    /// On the main thread: the fetch never runs, `send` runs at once on the
    /// main thread, and the event says why it has no `recent_logs`.
    @MainActor
    @Test func onMainThreadSkipsTheFetchAndSendsOnTheMainThread() {
        #expect(Thread.isMainThread)
        let caller = Thread.current
        var fetched = false
        var sendCount = 0
        var sentOnCallerThread = false
        var extras: [String: Any] = [:]

        SentryService.withRecentLogs(true, fetch: {
            fetched = true
            return "logs"
        }) {
            sendCount += 1
            sentOnCallerThread = Thread.current === caller
            extras = $0
        }

        #expect(fetched == false)
        #expect(sendCount == 1)
        #expect(sentOnCallerThread == true)
        #expect(extras["recent_logs"] == nil)
        #expect(extras["recent_logs_skipped"] as? String == "main_thread")
    }

    /// With recent logs off there is nothing to fetch or skip: `send` runs
    /// inline with no extras.
    @MainActor
    @Test func noRecentLogsSendsInlineWithoutFetching() {
        var fetched = false
        var sendCount = 0
        var extras: [String: Any] = ["unset": true]

        SentryService.withRecentLogs(false, fetch: {
            fetched = true
            return "logs"
        }) {
            sendCount += 1
            extras = $0
        }

        #expect(fetched == false)
        #expect(sendCount == 1)
        #expect(extras.isEmpty)
    }
}
