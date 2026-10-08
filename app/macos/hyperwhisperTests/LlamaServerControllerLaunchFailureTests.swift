//
//  LlamaServerControllerLaunchFailureTests.swift
//  hyperwhisperTests
//
//  #1536 / #1537: a llama-server that exits (or is stopped) before it is ready.
//
//  `stop()` used to close both pipe read handles while a reader child of
//  `monitorTask` could still be waiting to start. That child then opened
//  `bytes.lines` on a closed NSFileHandle, Foundation raised an Objective-C
//  exception Swift cannot catch, and the main queue stopped draining (the Local
//  API hung, and Quit later crashed in `MainActor.assumeIsolated`).
//
//  The behavioural tests launch a real child process through the controller's
//  `executableOverride` (a tiny shell script standing in for llama-server) and
//  point the readiness probe at port 1, where nothing listens. On the old code
//  the reader raised after the failed start; here the start must only fail.
//
//  Each controller gets its own PID file in a temp directory
//  (`init(pidFileURL:)`), which also skips the launch-time orphan sweep, so
//  a test never overwrites, deletes or acts on the host app's (`TEST_HOST`)
//  record in Application Support.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite(.serialized)
struct LlamaServerControllerLaunchFailureTests {

    /// Writes an executable shell script into a fresh temp directory.
    private static func makeFakeRuntime(body: String, interpreter: String = "/bin/sh") throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("llama-launch-failure-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let script = directory.appendingPathComponent("fake-llama-server")
        try Data("#!\(interpreter)\n\(body)\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        return script
    }

    private static func configuration(executable: URL) -> LlamaServerController.Configuration {
        var configuration = LlamaServerController.Configuration()
        // Nothing listens on port 1, so the readiness probe gets a fast
        // connection refusal and never a 200.
        configuration.port = 1
        configuration.executableOverride = executable
        return configuration
    }

    /// A controller that tracks its process in a PID file beside `script`,
    /// never in the host app's shared one.
    private static func makeController(beside script: URL) -> LlamaServerController {
        LlamaServerController(pidFileURL: pidFile(beside: script))
    }

    private static func pidFile(beside script: URL) -> URL {
        script.deletingLastPathComponent().appendingPathComponent(".llama-server.pid")
    }

    /// Resumes only once the main dispatch queue runs a block. In #1536 the
    /// main queue stopped draining after the uncaught Objective-C exception.
    private static func mainQueueDrains() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    private static func isFailed(_ state: LlamaServerController.State) -> Bool {
        if case .failed = state { return true }
        return false
    }

    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func runtimeThatExitsBeforeReadinessOnlyFails() async throws {
        let script = try Self.makeFakeRuntime(body: "echo 'fake runtime: refusing to start' >&2\nexit 3")
        defer { try? FileManager.default.removeItem(at: script.deletingLastPathComponent()) }
        let configuration = Self.configuration(executable: script)
        let controller = Self.makeController(beside: script)

        // Two callers in the same moment, as at app launch (prewarm + a second
        // resolve): the second sees `.starting` and returns at once.
        let first = Task { @MainActor in
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        await Task.yield()
        _ = try? await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)

        let firstResult = await first.result
        #expect(throws: LlamaServerController.Error.self) { try firstResult.get() }
        // The real reason, not "Timed out waiting for runtime".
        #expect(controller.state == .failed("Exit code 3"))

        // Give the stdout/stderr readers and the termination handler time to
        // run. On the old code a reader opened a closed handle here.
        try await Task.sleep(nanoseconds: 1_000_000_000)
        await Self.mainQueueDrains()
        #expect(Self.isFailed(controller.state))

        // An on-demand retry fails the same way, and the app keeps draining.
        await #expect(throws: LlamaServerController.Error.self) {
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        await Self.mainQueueDrains()
        #expect(Self.isFailed(controller.state))

        #expect(controller.state == .failed("Exit code 3"))

        // A refresh that marks pending and then returns without starting must
        // not hide the failure behind "Warming Up".
        controller.markPending()
        #expect(controller.state == .failed("Exit code 3"))
        controller.stop()
        #expect(controller.state == .stopped)
        controller.markPending()
        #expect(controller.state == .pending)
        controller.stop()
        #expect(controller.state == .stopped)
    }

    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func stopDuringStartLeavesStoppedAndKeepsDraining() async throws {
        // A runtime that never becomes ready and keeps both pipes open, so the
        // readers are live (or about to start) when stop() runs.
        let script = try Self.makeFakeRuntime(body: "echo 'fake runtime: starting' >&2\nexec /bin/sleep 5")
        defer { try? FileManager.default.removeItem(at: script.deletingLastPathComponent()) }
        let configuration = Self.configuration(executable: script)
        let controller = Self.makeController(beside: script)

        let start = Task { @MainActor in
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        #expect(controller.state == .starting(modelId: "fake"))
        // The launch is tracked in this test's own PID file.
        let pidFile = Self.pidFile(beside: script)
        #expect(FileManager.default.fileExists(atPath: pidFile.path))

        // A mode change mid-start.
        controller.stop(reason: .providerDisabled)

        let result = await start.result
        #expect(throws: LlamaServerController.Error.self) { try result.get() }
        // The stop owns the state: the superseded start must not mark it failed.
        #expect(controller.state == .stopped)
        #expect(!FileManager.default.fileExists(atPath: pidFile.path))

        try await Task.sleep(nanoseconds: 1_000_000_000)
        await Self.mainQueueDrains()
        #expect(controller.state == .stopped)
    }

    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func launchThatCannotExecFailsAndRetries() async throws {
        // An executable file whose interpreter does not exist: the override is
        // accepted, and `process.run()` itself throws.
        let script = try Self.makeFakeRuntime(body: "exit 0", interpreter: "/nonexistent/hyperwhisper-test-sh")
        defer { try? FileManager.default.removeItem(at: script.deletingLastPathComponent()) }
        let configuration = Self.configuration(executable: script)
        let controller = Self.makeController(beside: script)

        await #expect(throws: LlamaServerController.Error.self) {
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        #expect(Self.isFailed(controller.state))

        // Not left `.starting`: a retry launches (and fails) again rather than
        // returning at once as "already starting".
        await #expect(throws: LlamaServerController.Error.self) {
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        #expect(Self.isFailed(controller.state))
        controller.stop()
    }

    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func grandchildHoldingThePipesDoesNotHoldTheStart() async throws {
        // The runtime exits, but a background child inherited stdout/stderr
        // and keeps them open for 3 s, so the readers see no EOF.
        let script = try Self.makeFakeRuntime(body: "/bin/sleep 3 &\necho 'fake runtime: exiting' >&2\nexit 3")
        defer { try? FileManager.default.removeItem(at: script.deletingLastPathComponent()) }
        let configuration = Self.configuration(executable: script)
        let controller = Self.makeController(beside: script)

        let started = Date()
        await #expect(throws: LlamaServerController.Error.self) {
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        #expect(Date().timeIntervalSince(started) < 2.5)
        #expect(controller.state == .failed("Exit code 3"))

        try await Task.sleep(nanoseconds: 500_000_000)
        await Self.mainQueueDrains()
        #expect(controller.state == .failed("Exit code 3"))
        controller.stop()
    }

    // MARK: - Wiring that cannot be called from a test

    fileprivate static let controllerPath = "app/macos/hyperwhisper/Managers/LlamaServerController.swift"

    /// Nothing may close a pipe read handle that a reader task can still open
    /// (#1536), and nothing may ask the concurrency runtime for the main
    /// executor on the quit path (#1537). Comments are stripped first, so the
    /// prose that explains the ban does not trip it.
    @Test func controllerNeverClosesReadHandlesOrAssumesIsolation() throws {
        let code = try ProductionSource.code(of: Self.controllerPath)
        #expect(!code.contains("fileHandleForReading.closeFile"))
        #expect(!code.contains("fileHandleForReading.close("))
        #expect(!code.contains("assumeIsolated"))
        // No FileHandle read at all: each raises an Objective-C exception on a
        // closed handle. Output is read with read(2) in RuntimeOutputReader.
        #expect(!code.contains(".bytes.lines"))
        #expect(!code.contains("availableData"))
        #expect(!code.contains("readDataToEndOfFile"))
    }

    /// The willTerminate observer body, up to `deinit`: it must not capture
    /// the controller at all, only call the PID-file based static.
    @Test func quitObserverTouchesNoActorState() throws {
        let observer = try ProductionSource.slice(
            of: Self.controllerPath,
            from: "forName: NSApplication.willTerminateNotification,",
            to: "deinit {"
        )
        #expect(observer.contains("LlamaServerController.stopTrackedRuntimeForTermination()"))
        #expect(!observer.contains("self"))
    }

    /// Quit stops what this app launched even when the PID file does not hold
    /// it: the lock-protected launch record first, then the PID file.
    @Test func quitStopsLaunchRecordAndPIDFile() throws {
        let quit = try ProductionSource.slice(
            of: Self.controllerPath,
            from: "private nonisolated static func stopTrackedRuntimeForTermination()",
            to: "private nonisolated func stopSynchronouslyFromDeinit()"
        )
        #expect(quit.contains("trackedRuntimes.snapshot()"))
        #expect(quit.contains("readPIDFileContents()"))
        #expect(!quit.contains("self"))
    }
}
