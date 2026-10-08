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
//  Caveat: the controller tracks its process in the app's real PID file
//  (Application Support), the same one the host app (`TEST_HOST`) uses, and
//  `stop()` removes that file. Signals stay safe: `stop()` only signals the
//  process it launched, after re-checking that process's identity, and the
//  fake runtime is never a known HyperWhisper llama-server path, so the
//  PID-file and orphan sweeps never signal it.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite(.serialized)
struct LlamaServerControllerLaunchFailureTests {

    /// Writes an executable shell script into a fresh temp directory.
    private static func makeFakeRuntime(body: String) throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("llama-launch-failure-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let script = directory.appendingPathComponent("fake-llama-server")
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: script)
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
        let controller = LlamaServerController()

        // Two callers in the same moment, as at app launch (prewarm + a second
        // resolve): the second sees `.starting` and returns at once.
        let first = Task { @MainActor in
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        await Task.yield()
        _ = try? await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)

        let firstResult = await first.result
        #expect(throws: LlamaServerController.Error.self) { try firstResult.get() }
        #expect(Self.isFailed(controller.state))

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
        let controller = LlamaServerController()

        let start = Task { @MainActor in
            try await controller.ensureRunning(modelId: "fake", modelURL: script, configuration: configuration)
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        #expect(controller.state == .starting(modelId: "fake"))

        // A mode change mid-start.
        controller.stop(reason: .providerDisabled)

        let result = await start.result
        #expect(throws: LlamaServerController.Error.self) { try result.get() }
        // The stop owns the state: the superseded start must not mark it failed.
        #expect(controller.state == .stopped)

        try await Task.sleep(nanoseconds: 1_000_000_000)
        await Self.mainQueueDrains()
        #expect(controller.state == .stopped)
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
}
