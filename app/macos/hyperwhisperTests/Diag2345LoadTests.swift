//
//  Diag2345LoadTests.swift — THROWAWAY probe for issue #2345, never merged.
//
//  Keeps every Swift concurrency pool thread busy at high priority for 40 s,
//  yielding often, the way the full suite's CPU-heavy tests do on a 3-core
//  runner, while the two flaky suites run beside it.
//

import Foundation
import Testing

@Suite struct Diag2345LoadTests {
    @Test func busyPoolFor40Seconds() async {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let end = ContinuousClock.now.advanced(by: .seconds(40))
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<(cores * 2) {
                group.addTask(priority: .high) {
                    while ContinuousClock.now < end {
                        let burst = ContinuousClock.now.advanced(by: .milliseconds(200))
                        var x = 0
                        while ContinuousClock.now < burst { x &+= 1 }
                        await Task.yield()
                    }
                }
            }
        }
    }

    @Test func globalQueueLatencyWhilePoolBusy() async throws {
        try await Task.sleep(for: .seconds(3))
        let start = Date()
        let viaGlobal: Double = await withCheckedContinuation { c in
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) { c.resume(returning: Date().timeIntervalSince(start)) }
        }
        let start2 = Date()
        let viaThread: Double = await withCheckedContinuation { c in
            Thread.detachNewThread { Thread.sleep(forTimeInterval: 0.3); c.resume(returning: Date().timeIntervalSince(start2)) }
        }
        #expect(viaGlobal < 0, "LATENCY global=\(viaGlobal) thread=\(viaThread)")
    }
}
