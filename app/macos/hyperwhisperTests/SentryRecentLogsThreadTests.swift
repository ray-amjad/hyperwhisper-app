//
//  SentryRecentLogsThreadTests.swift
//  hyperwhisperTests
//
//  An Event's `recent_logs` come from a `log show` subprocess that blocks
//  until it exits. Reached from the main thread, that parked the UI inside
//  error reporting (#991). `SentryService.withRecentLogs` now keeps the fetch
//  off the main thread. These tests pin where the fetch runs; they inject the
//  thread, the fetch and the queue, so no subprocess and no SDK start.
//

import Foundation
import Testing
@testable import HyperWhisper

@Suite("Sentry recent logs stay off the main thread")
struct SentryRecentLogsThreadTests {

    /// Off the main thread nothing changes: fetch and send run inline, so the
    /// event is out before `capture` returns.
    @Test func offMainThreadFetchesAndSendsInline() {
        var hopped = false
        var sent: (String?, Bool)?
        SentryService.withRecentLogs(
            true,
            isMainThread: false,
            fetch: { "logs" },
            background: { _ in hopped = true },
            send: { sent = ($0, $1) }
        )
        #expect(hopped == false)
        #expect(sent?.0 == "logs")
        #expect(sent?.1 == false)
    }

    /// On the main thread the fetch must not run inline. It runs only when the
    /// background queue runs it, and the event says it was deferred.
    @Test func onMainThreadDefersFetchToBackground() {
        var fetched = false
        var queued: (() -> Void)?
        var sent: (String?, Bool)?
        SentryService.withRecentLogs(
            true,
            isMainThread: true,
            fetch: { fetched = true; return "logs" },
            background: { queued = $0 },
            send: { sent = ($0, $1) }
        )
        #expect(fetched == false)
        #expect(sent == nil)

        queued?()
        #expect(fetched == true)
        #expect(sent?.0 == "logs")
        #expect(sent?.1 == true)
    }

    /// With recent logs off there is no subprocess to avoid, so the event is
    /// sent inline even from the main thread.
    @Test func noRecentLogsSendsInlineWithoutFetching() {
        var fetched = false
        var hopped = false
        var sent: (String?, Bool)?
        SentryService.withRecentLogs(
            false,
            isMainThread: true,
            fetch: { fetched = true; return "logs" },
            background: { _ in hopped = true },
            send: { sent = ($0, $1) }
        )
        #expect(fetched == false)
        #expect(hopped == false)
        #expect(sent != nil)
        #expect(sent?.0 == nil)
        #expect(sent?.1 == false)
    }
}
