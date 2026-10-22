//
//  LocalLLMLaunchRetryTests.swift
//  hyperwhisperTests
//
//  #908: the streaming post-processing pass started llama-server with a bare
//  `server.ensureRunning`, so a transient launch failure gave the user the raw
//  transcript at once, while the non-streaming pass retried once through
//  `ensureLocalRuntimeRunning`. Both passes now share one local LLM ladder.
//
//  These tests run the real entry points on an `AIPostProcessor` wired to a
//  real `LlamaServerController` whose runtime is a fake shell script that
//  appends a line to a counter file and exits 3 (the same pattern as
//  `LlamaServerControllerLaunchFailureTests`: port 1, a per-test PID file).
//  Each pass must return the original text, report `localRuntimeUnavailable`
//  once, and launch the runtime exactly twice: the first launch and the retry.
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite(.serialized)
struct LocalLLMLaunchRetryTests {

    private static let text = "so the meeting is at three tomorrow"
    private static let modelId = "fake-local-model.gguf"

    /// Everything one pass needs, held strongly: `AIPostProcessor` keeps the
    /// model manager and the controller `weak`, and the mode needs its context.
    private struct Rig {
        let directory: URL
        let counter: URL
        let persistence: PersistenceController
        let mode: Mode
        let localModels: LocalModelManager
        let controller: LlamaServerController
        let processor: AIPostProcessor
        let errors: LocalRuntimeErrorBox

        /// How many times the fake runtime was launched.
        var launchCount: Int {
            guard let contents = try? String(contentsOf: counter, encoding: .utf8) else { return 0 }
            return contents.split(separator: "\n").count
        }
    }

    private static func makeRig() throws -> Rig {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("local-llm-launch-retry-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        // The fake llama-server: one counter line per launch, then it exits
        // before it is ever ready, which the controller reports as
        // `.healthCheckFailed` (a transient failure the ladder retries once).
        let counter = directory.appendingPathComponent("launches.txt")
        let script = directory.appendingPathComponent("fake-llama-server")
        let body = """
        #!/bin/sh
        echo launch >> '\(counter.path)'
        echo 'fake runtime: refusing to start' >&2
        exit 3
        """
        try Data("\(body)\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)

        // The "installed" GGUF only has to exist; the fake runtime never reads it.
        let modelFile = directory.appendingPathComponent(modelId)
        try Data("not a real gguf".utf8).write(to: modelFile)

        // Its own PID file, so the test never touches the host app's record.
        let controller = LlamaServerController(
            pidFileURL: directory.appendingPathComponent(".llama-server.pid")
        )
        var configuration = LlamaServerController.Configuration()
        // Nothing listens on port 1, so readiness never sees a 200.
        configuration.port = 1
        configuration.executableOverride = script
        controller.configurationOverrideForTesting = configuration

        let localModels = LocalModelManager()
        localModels.replaceDownloadedModelsForTesting([
            LocalModel(
                id: modelId,
                displayName: "Fake Local Model",
                filename: modelId,
                sizeDescription: "1 KB",
                sizeInBytes: 15,
                sha256: "",
                notes: "",
                downloadURL: URL(string: "https://example.invalid/\(modelId)")!,
                isRecommended: false,
                localURL: modelFile
            )
        ])

        let persistence = PersistenceController(inMemory: true)
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.preset = "hyper"
        mode.postProcessingMode = PostProcessingMode.local.rawValue
        mode.postProcessingProvider = PostProcessingProvider.localLLM.rawValue
        mode.languageModel = modelId

        let processor = AIPostProcessor(settingsManager: SettingsManager.shared)
        processor.localModelManager = localModels
        processor.llamaServerController = controller
        let errors = LocalRuntimeErrorBox()
        processor.onPostProcessingError = { errors.errors.append($0) }

        return Rig(
            directory: directory,
            counter: counter,
            persistence: persistence,
            mode: mode,
            localModels: localModels,
            controller: controller,
            processor: processor,
            errors: errors
        )
    }

    private static func tearDown(_ rig: Rig) {
        rig.controller.stop()
        try? FileManager.default.removeItem(at: rig.directory)
    }

    private static func expectOneRuntimeUnavailable(_ rig: Rig) {
        #expect(rig.errors.errors.count == 1)
        guard case .localRuntimeUnavailable? = rig.errors.errors.first else {
            Issue.record("expected .localRuntimeUnavailable, got \(rig.errors.errors)")
            return
        }
    }

    /// The pass #908 fixed: it used to launch once and give up.
    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func streamingPassRetriesAFailedLaunchOnce() async throws {
        let rig = try Self.makeRig()
        defer { Self.tearDown(rig) }

        let result = try await rig.processor.performAIPostProcessingStreaming(
            text: Self.text,
            mode: rig.mode,
            applicationContext: ApplicationContext.none,
            streamingTextUpdate: { _ in }
        )

        #expect(result == Self.text)
        #expect(!rig.processor.didMutateLastRun)
        Self.expectOneRuntimeUnavailable(rig)
        #expect(rig.launchCount == 2)
        withExtendedLifetime(rig) {}
    }

    /// The pass that already retried keeps doing so through the shared ladder.
    @Test(.enabled(if: SystemCapability.current == .supported, "the controller only launches on native Apple Silicon"))
    func nonStreamingPassRetriesAFailedLaunchOnce() async throws {
        let rig = try Self.makeRig()
        defer { Self.tearDown(rig) }

        let result = try await rig.processor.performAIPostProcessing(
            text: Self.text,
            mode: rig.mode,
            applicationContext: ApplicationContext.none
        )

        #expect(result == Self.text)
        #expect(!rig.processor.didMutateLastRun)
        Self.expectOneRuntimeUnavailable(rig)
        #expect(rig.launchCount == 2)
        withExtendedLifetime(rig) {}
    }
}

private final class LocalRuntimeErrorBox {
    var errors: [TranscriptionError] = []
}
