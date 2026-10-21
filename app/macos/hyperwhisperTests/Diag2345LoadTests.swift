//
//  Diag2345LoadTests.swift — THROWAWAY probe for issue #2345, never merged.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

/// Keeps every pool thread busy at the tests' own priority for 40 s, yielding
/// often, the way the full suite's CPU-heavy tests do on a 3-core runner.
@Suite struct Diag2345LoadTests {
    @Test func busyPoolFor40Seconds() async {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let end = ContinuousClock.now.advanced(by: .seconds(40))
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<(cores * 2) {
                group.addTask(priority: .medium) {
                    while ContinuousClock.now < end {
                        let burst = ContinuousClock.now.advanced(by: .milliseconds(100))
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

/// What a main actor held for `seconds` right after start() does to a free
/// persisted port (old saveTakesLocksBeforeWriting held it up to 10 s).
@Suite(.serialized) @MainActor struct Diag2345MainBlockTests {
    private func run(blockMainFor seconds: Double) async throws -> String {
        let suite = "Diag2345.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Diag2345-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        // A free port.
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        var a = sockaddr_in(); a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size); a.sin_family = sa_family_t(AF_INET); a.sin_addr.s_addr = inet_addr("127.0.0.1")
        _ = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        var n = sockaddr_in(); var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        _ = withUnsafeMutablePointer(to: &n) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.getsockname(fd, $0, &len) } }
        _ = Darwin.close(fd)
        let port = UInt16(bigEndian: n.sin_port)
        defaults.set(Int(port), forKey: LocalAPIServerPersistedPortKey)
        let server = LocalAPIServer(defaults: defaults, portFileURL: dir.appendingPathComponent("p.json"), tokenLoader: { "t" })
        server.start()
        // Let the token task run and reach bindAndRun.
        for _ in 0..<50 { await Task.yield() }
        try await Task.sleep(for: .milliseconds(200))
        Thread.sleep(forTimeInterval: seconds)
        let deadline = ContinuousClock.now.advanced(by: .seconds(30))
        while !server.isRunning, ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(20)) }
        let out = "block=\(seconds) asked=\(port) got=\(server.listeningPort) running=\(server.isRunning) persisted=\(defaults.integer(forKey: LocalAPIServerPersistedPortKey))"
        server.stop()
        try? await Task.sleep(for: .seconds(1))
        return out
    }

    @Test func mainBlocked0() async throws { let r = try await run(blockMainFor: 0); #expect(Bool(false), "PROBE \(r)") }
    @Test func mainBlocked6() async throws { let r = try await run(blockMainFor: 6); #expect(Bool(false), "PROBE \(r)") }
    @Test func mainBlocked10() async throws { let r = try await run(blockMainFor: 10); #expect(Bool(false), "PROBE \(r)") }
}
