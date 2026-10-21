//
//  LocalAPIServerPortPreferenceTests.swift
//  hyperwhisperTests
//
//  The rules that keep the Local API port stable across launches, run against
//  a real `LocalAPIServer` on a real loopback socket (issue #1838).
//
//  A script that talks to `localhost:<port>` keeps working because a bind
//  persists the port it got and the next start asks for it again. When that
//  port is taken, the start clears the preference and binds an ephemeral one,
//  and persists that instead; without the clear, the retry asks for the same
//  taken port forever. And waking the Mac starts the server only when the
//  user left it switched on.
//
//  Each test makes its own server on a scratch `UserDefaults` suite, a temp
//  discovery file and a fixed token, through the init `shared` also uses, so
//  nothing here reads the real defaults, the real local-api.json or the
//  Keychain. A taken port is held by a plain listening socket.
//
//  The server's own run task starts on the main actor, which another suite in
//  the test host can hold for seconds, so every wait has a generous deadline.
//

import Darwin
import Foundation
import Testing
@testable import HyperWhisper

@Suite(.serialized)
@MainActor
struct LocalAPIServerPortPreferenceTests {

    // MARK: - 1. A free persisted port is reused

    @Test func aFreePersistedPortIsTheOneTheServerListensOn() async throws {
        let fixture = try Fixture()
        defer { fixture.removeFiles() }
        let port = try PortHolder.freePort()
        fixture.defaults.set(Int(port), forKey: LocalAPIServerPersistedPortKey)

        fixture.server.start()
        try await until("the server listens") { fixture.server.isRunning }

        #expect(fixture.server.listeningPort == port)
        #expect(fixture.persistedPort == port)
        #expect(try fixture.portFilePort() == port)
        #expect(fixture.server.lastError == nil)
        await fixture.stop()
    }

    // MARK: - 2. A taken persisted port falls back to an ephemeral one

    @Test func aTakenPersistedPortIsGivenUpForAnEphemeralOneThatIsThenPersisted() async throws {
        let fixture = try Fixture()
        defer { fixture.removeFiles() }
        let holder = try PortHolder()
        defer { holder.close() }
        fixture.defaults.set(Int(holder.port), forKey: LocalAPIServerPersistedPortKey)

        fixture.server.start()
        try await until("the server listens on a port other than the taken one") {
            fixture.server.isRunning && fixture.server.listeningPort != holder.port
        }

        let bound = fixture.server.listeningPort
        #expect(bound != 0)
        #expect(bound != holder.port)
        #expect(fixture.persistedPort == bound, "the ephemeral port must replace the taken one in the preference")
        #expect(try fixture.portFilePort() == bound)
        #expect(fixture.server.lastError == nil, "a retry that bound must not leave the failed bind's error showing")
        await fixture.stop()
    }

    // MARK: - 3. A first bind persists its port

    @Test func aServerWithNoPersistedPortPersistsTheOneItGets() async throws {
        let fixture = try Fixture()
        defer { fixture.removeFiles() }
        #expect(fixture.defaults.object(forKey: LocalAPIServerPersistedPortKey) == nil)

        fixture.server.start()
        try await until("the server listens") { fixture.server.isRunning }

        let bound = fixture.server.listeningPort
        #expect(bound != 0)
        #expect(fixture.persistedPort == bound)
        #expect(try fixture.portFilePort() == bound)
        await fixture.stop()
        #expect(!FileManager.default.fileExists(atPath: fixture.portFile.path), "stop() must delete this server's own discovery file")
    }

    // MARK: - 4. Wake starts the server only when it is switched on

    @Test func wakeLeavesASwitchedOffServerOff() async throws {
        let fixture = try Fixture()
        defer { fixture.removeFiles() }
        fixture.defaults.set(false, forKey: LocalAPIServerEnabledKey)

        fixture.server.handleSystemDidWake()
        // Long enough for a start that was wrongly issued to reach its token read.
        try await Task.sleep(for: .seconds(1))

        #expect(await fixture.tokenCalls.count == 0, "wake started a Local API the user switched off")
        #expect(!fixture.server.isRunning)
        #expect(!FileManager.default.fileExists(atPath: fixture.portFile.path))
        await fixture.stop()
    }

    @Test func wakeStartsASwitchedOnServer() async throws {
        let fixture = try Fixture()
        defer { fixture.removeFiles() }
        fixture.defaults.set(true, forKey: LocalAPIServerEnabledKey)

        fixture.server.handleSystemDidWake()
        try await until("the server listens after wake") { fixture.server.isRunning }

        #expect(await fixture.tokenCalls.count == 1)
        #expect(try fixture.portFilePort() == fixture.server.listeningPort)
        await fixture.stop()
    }
}

// MARK: - Fixture

@MainActor
private struct Fixture {
    let suiteName = "LocalAPIServerPortPreferenceTests.\(UUID().uuidString)"
    let defaults: UserDefaults
    let directory: URL
    let portFile: URL
    let tokenCalls = TokenCalls()
    let server: LocalAPIServer

    init() throws {
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            throw FixtureError.noDefaults
        }
        self.defaults = defaults
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("LocalAPIServerPortPreferenceTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        portFile = directory.appendingPathComponent("local-api.json")
        let calls = tokenCalls
        server = LocalAPIServer(
            defaults: defaults,
            portFileURL: portFile,
            tokenLoader: {
                await calls.hit()
                return "test-token"
            }
        )
    }

    var persistedPort: UInt16 {
        UInt16(defaults.integer(forKey: LocalAPIServerPersistedPortKey))
    }

    func portFilePort() throws -> UInt16 {
        let data = try Data(contentsOf: portFile)
        return try LocalAPIResponder.decoder.decode(LocalAPIPortFile.self, from: data).port
    }

    /// Stop the server and wait for its listening socket to close, so the next
    /// test starts with nothing of this one still bound.
    func stop() async {
        server.stop()
        try? await until("the stopped server releases its port") { server.closingPorts.isEmpty }
    }

    func removeFiles() {
        defaults.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: directory)
    }
}

private enum FixtureError: Error {
    case noDefaults
    case timedOut(String)
    case syscall(String, Int32)
}

private actor TokenCalls {
    private(set) var count = 0
    func hit() { count += 1 }
}

@MainActor
private func until(
    _ what: String,
    within timeout: Duration = .seconds(30),
    _ condition: () -> Bool
) async throws {
    let deadline = ContinuousClock.now.advanced(by: timeout)
    while !condition() {
        guard ContinuousClock.now < deadline else { throw FixtureError.timedOut(what) }
        try await Task.sleep(for: .milliseconds(20))
    }
}

// MARK: - A taken port

/// A plain listening socket on 127.0.0.1, holding a port the server will ask for.
private final class PortHolder {

    let port: UInt16
    private let fd: Int32

    init() throws {
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw FixtureError.syscall("socket", errno) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = 0
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, Darwin.listen(fd, 1) == 0 else {
            let code = errno
            _ = Darwin.close(fd)
            throw FixtureError.syscall("bind/listen", code)
        }
        var name = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &name) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.getsockname(fd, $0, &length)
            }
        }
        guard named == 0 else {
            let code = errno
            _ = Darwin.close(fd)
            throw FixtureError.syscall("getsockname", code)
        }
        self.fd = fd
        self.port = UInt16(bigEndian: name.sin_port)
    }

    func close() {
        _ = Darwin.close(fd)
    }

    /// A port nothing is listening on right now.
    static func freePort() throws -> UInt16 {
        let holder = try PortHolder()
        holder.close()
        return holder.port
    }
}
