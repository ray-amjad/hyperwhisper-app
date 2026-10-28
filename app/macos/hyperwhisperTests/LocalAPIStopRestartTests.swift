//
//  LocalAPIStopRestartTests.swift
//  hyperwhisperTests
//
//  Switching the Local API off and on again while a connection is held open
//  mid-request (issue #1464), over a real socket.
//
//  Before the fix, `stop()` fired the old server's close into a task nobody
//  waited for and reported the server disabled on the same line, before that
//  task had even run: Settings said "No network sockets bound" while the port
//  was still in LISTEN, and the start that followed rebound the same port with
//  no wait for it. Ray's done-when (inbox ask #265): hold one connection open
//  mid-request, switch Off then On, see a listening port within 5 s, never see
//  the row report "disabled" while the old port is bound, and quit returns.
//
//  `LocalAPIServer.start()` reads the Keychain and this target links no
//  FlyingFox, so the switch runs through `LocalAPISwitchProbe`: a server from
//  the production `serverConfiguration`, stopped through the same
//  `LocalAPIListenerShutdown` `stop()` uses and restarted after the same
//  `releaseWait` `start()` uses. The row is the production
//  `APIServerSettingsSection.statusRow`. Quit is `stop()` from
//  `applicationWillTerminate`; what this can check is that the switch Off
//  returns at once with the connection still held, and never waits on it.
//
//  The blocking socket calls are loopback and immediate (connect, bind), so
//  they run inline.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

@Suite(.serialized)
@MainActor
struct LocalAPIStopRestartTests {

    // MARK: - The switch, end to end

    /// Off, with one connection held mid-request: the row says "Stopping…"
    /// for as long as the old port is bound and "disabled" only after, and
    /// On then listens again on the same port.
    @Test func offWithAConnectionHeldMidRequestNeverReportsDisabledWhileBound() async throws {
        let probe = LocalAPISwitchProbe(headReadDeadline: 120)
        let oldPort = try await probe.switchOn()
        let held = try await Self.holdOneConnectionMidRequest(port: oldPort)
        defer { held.close() }

        // Off. Like stop(), it returns at once: quit calls it, and a close
        // that waited on the held connection would take 120 s. `switchOff()`
        // is synchronous and the close it starts runs on this actor's next
        // turn, so the first sample below, taken before this test yields,
        // seeing the old socket still bound is the proof that it returned
        // without waiting. No wall-clock bound: on a loaded runner this test
        // has taken 41-49 s with no code at fault (#2526).
        probe.switchOff()

        // Until the old socket is gone, the row never says disabled while the
        // port is still bound. The wait is counted in samples, not seconds,
        // so a runner that stalls this test spends one sample, not the budget.
        var sawBoundWhileStopping = false
        var samples = 0
        while true {
            let bound = PortCheck.isListening(oldPort)
            let row = Self.row(enabled: false, probe: probe)
            if bound {
                #expect(row == ("Stopping…", "127.0.0.1:\(oldPort)"), "the row read \"\(row.title) · \(row.detail)\" while 127.0.0.1:\(oldPort) was still listening")
                sawBoundWhileStopping = true
            }
            if !bound && probe.shutdown.closingPorts.isEmpty { break }
            guard samples < Self.releaseSamples else {
                Issue.record("the old server still \(bound ? "listens" : "reports closing") after \(samples) samples since the switch went off")
                return
            }
            samples += 1
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(sawBoundWhileStopping, "switching off waited on the close: the first sample, before any yield, must still see the old socket bound")
        #expect(Self.row(enabled: false, probe: probe) == ("Server disabled", "No network sockets bound."))

        // On again, asking for the same port, as start() does with the
        // persisted one: it is free, so it is bound again.
        let newPort = try await probe.switchOn(preferredPort: oldPort)
        #expect(newPort == oldPort, "the released port should be bound again, got \(newPort)")
        #expect(Self.row(enabled: true, probe: probe) == ("Server enabled", "127.0.0.1:\(newPort) · token ••••"))

        held.close()
        probe.switchOff()
        #expect(try await Self.released(probe.shutdown))
    }

    /// Off then straight back On, with one connection held mid-request, as
    /// the issue's repro does it: a listening port within 5 s.
    @Test func offThenOnAtOnceWithAConnectionHeldMidRequestListensWithinFiveSeconds() async throws {
        let probe = LocalAPISwitchProbe(headReadDeadline: 120)
        let oldPort = try await probe.switchOn()
        let held = try await Self.holdOneConnectionMidRequest(port: oldPort)
        defer { held.close() }

        probe.switchOff()
        #expect(Self.row(enabled: false, probe: probe).title == "Stopping…")

        let onStarted = ContinuousClock.now
        let newPort = try await probe.switchOn(preferredPort: oldPort)
        let onTook = ContinuousClock.now - onStarted
        #expect(onTook < .seconds(5), "switching on took \(onTook)")
        #expect(newPort > 0)
        #expect(PortCheck.isListening(newPort), "nothing listens on the port the switch On reported")
        #expect(Self.row(enabled: true, probe: probe) == ("Server enabled", "127.0.0.1:\(newPort) · token ••••"))

        held.close()
        probe.switchOff()
        #expect(try await Self.released(probe.shutdown))
    }

    /// A close that never finishes must not keep a switch On from listening:
    /// the wait gives up at the deadline, and the row keeps saying "Stopping…"
    /// for the socket that is still bound.
    @Test func aStuckCloseLetsTheWaiterGoAtTheDeadline() async throws {
        let shutdown = LocalAPIListenerShutdown()
        let gate = Gate()
        let stuck = Task<Void, Never> { await gate.wait() }

        shutdown.begin(server: nil, runTask: stuck, port: 4242)
        #expect(shutdown.closingPorts == [4242])
        #expect(
            APIServerSettingsSection.statusRow(
                enabled: false, isRunning: false, listeningPort: 0,
                closingPorts: shutdown.closingPorts, tokenSuffix: ""
            ) == ("Stopping…", "127.0.0.1:4242")
        )

        // The close cannot end before `gate.open()` below, so this returning
        // at all proves the wait ended at its deadline, not with the close.
        // No wall-clock bound on top (#2526).
        let released = await shutdown.awaitRelease(within: .milliseconds(300))
        #expect(!released, "a close that has not finished must not count as released")
        #expect(shutdown.closingPorts == [4242], "the stuck socket is still reported closing")

        await gate.open()
        #expect(await shutdown.awaitRelease(within: .seconds(5)))
        #expect(shutdown.closingPorts.isEmpty)
    }

    /// With nothing closing, a start does not wait at all: a wait with no
    /// time to spare still reports released, with no wall-clock bound (#2526).
    @Test func nothingClosingIsReleasedAtOnce() async {
        let shutdown = LocalAPIListenerShutdown()
        #expect(await shutdown.awaitRelease(within: .zero))
    }

    // MARK: - The row

    @Test func theRowSaysStoppingOnlyWhileAnOldSocketIsBound() {
        func row(_ enabled: Bool, running: Bool = false, port: UInt16 = 0, closing: [UInt16] = []) -> (String, String) {
            let r = APIServerSettingsSection.statusRow(
                enabled: enabled, isRunning: running, listeningPort: port,
                closingPorts: closing, tokenSuffix: "abcd"
            )
            return (r.title, r.detail)
        }
        #expect(row(false) == ("Server disabled", "No network sockets bound."))
        #expect(row(false, closing: [50554]) == ("Stopping…", "127.0.0.1:50554"))
        #expect(row(false, closing: [0]) == ("Stopping…", "127.0.0.1"))
        #expect(row(false, closing: [0, 50554, 49776]) == ("Stopping…", "127.0.0.1:49776, 127.0.0.1:50554"))
        // Switched back on while the old one closes: the new server's state.
        #expect(row(true, closing: [50554]) == ("Server enabled", "Starting…"))
        #expect(row(true, running: true, port: 61000, closing: [50554]) == ("Server enabled", "127.0.0.1:61000 · token ••••abcd"))
        #expect(row(true, running: true, port: 61000) == ("Server enabled", "127.0.0.1:61000 · token ••••abcd"))
    }

    // MARK: - Helpers

    /// How many 10 ms samples a close gets to release its port: 30 s on an
    /// idle machine. Counted rather than timed, so a runner that stalls this
    /// test for longer still gives the close its samples (#2526).
    private static let releaseSamples = 3_000

    /// True once every close `shutdown` has begun has released its port,
    /// within `releaseSamples` samples.
    private static func released(_ shutdown: LocalAPIListenerShutdown) async throws -> Bool {
        for _ in 0..<releaseSamples {
            if shutdown.closingPorts.isEmpty { return true }
            try await Task.sleep(for: .milliseconds(10))
        }
        return shutdown.closingPorts.isEmpty
    }

    /// One connection, half-way through its request head, given time to be
    /// accepted. A head deadline longer than the test keeps it mid-request.
    private static func holdOneConnectionMidRequest(port: UInt16) async throws -> HeldConnection {
        let held = try HeldConnection(port: port)
        try held.send("GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Partial: ")
        try await Task.sleep(for: .milliseconds(100))
        return held
    }

    private static func row(enabled: Bool, probe: LocalAPISwitchProbe) -> (title: String, detail: String) {
        APIServerSettingsSection.statusRow(
            enabled: enabled,
            isRunning: probe.port > 0,
            listeningPort: probe.port,
            closingPorts: probe.shutdown.closingPorts,
            tokenSuffix: ""
        )
    }
}

/// Opens once, from any task. Stands in for a server whose close never ends.
private actor Gate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func open() {
        isOpen = true
        waiters.forEach { $0.resume() }
        waiters = []
    }
}

private enum PortCheck {
    /// True when a listening socket holds 127.0.0.1:`port`.
    ///
    /// A bind with SO_REUSEADDR, as FlyingFox binds: it fails with EADDRINUSE
    /// only while something is in LISTEN on that address. An open connection
    /// on the port does not stop it, so this asks exactly "could a new server
    /// bind here".
    static func isListening(_ port: UInt16) -> Bool {
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { _ = Darwin.close(fd) }
        var on: Int32 = 1
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in.testLoopback(port: port)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        return result != 0 && errno == EADDRINUSE
    }
}

private enum HeldConnectionError: Error {
    case syscall(String, Int32)
}

/// One raw TCP connection to 127.0.0.1, written to and never read.
private final class HeldConnection {
    private var fd: Int32

    init(port: UInt16) throws {
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw HeldConnectionError.syscall("socket", errno) }
        var on: Int32 = 1
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in.testLoopback(port: port)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard result == 0 else {
            let code = errno
            _ = Darwin.close(fd)
            throw HeldConnectionError.syscall("connect", code)
        }
        self.fd = fd
    }

    func send(_ text: String) throws {
        let bytes = Array(text.utf8)
        let sent = bytes.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
        guard sent == bytes.count else { throw HeldConnectionError.syscall("write", errno) }
    }

    func close() {
        guard fd >= 0 else { return }
        _ = Darwin.close(fd)
        fd = -1
    }
}

private extension sockaddr_in {
    static func testLoopback(port: UInt16) -> sockaddr_in {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return address
    }
}
