//
//  Qwen3InstallRefreshTests.swift
//  hyperwhisperTests
//
//  A finished Qwen3 ASR download must prepare the selected Qwen3 mode again,
//  so the status bar leaves "Qwen3 ASR (Download)" without a mode switch
//  (#2708). A mode that is already Ready must not be reloaded.
//

import Testing
@testable import HyperWhisper

@MainActor
struct Qwen3InstallRefreshTests {

    private static let qwen3 = Qwen3AsrModelManager.Constants.modelId
    private static let name = Qwen3AsrModelManager.Constants.displayName

    // MARK: - Which modes an install change prepares again

    @Test func aQwen3ModeThatIsNotReadyIsPreparedAgain() {
        let notReady: [TranscriptionModelManager.ModelReadyState] = [
            .none,
            .loading(name: Self.name),
            .unavailable(name: Self.name),
        ]
        for state in notReady {
            #expect(
                TranscriptionPipeline.installChangeNeedsPrepare(modelId: Self.qwen3, readyState: state),
                "state \(String(describing: state))"
            )
        }
        // Mode model ids are matched case-insensitively, as the prepare path does.
        #expect(TranscriptionPipeline.installChangeNeedsPrepare(
            modelId: Self.qwen3.uppercased(),
            readyState: .unavailable(name: Self.name)
        ))
    }

    @Test func aQwen3ModeThatIsReadyIsNotReloaded() {
        #expect(!TranscriptionPipeline.installChangeNeedsPrepare(
            modelId: Self.qwen3,
            readyState: .ready(name: Self.name)
        ))
    }

    /// The engines that already re-prepared keep their behaviour.
    @Test func theOtherEnginesAreUnchanged() {
        let ready = TranscriptionModelManager.ModelReadyState.ready(name: "x")
        let unavailable = TranscriptionModelManager.ModelReadyState.unavailable(name: "x")

        // Whisper: only when not Ready.
        #expect(TranscriptionPipeline.installChangeNeedsPrepare(modelId: "base", readyState: unavailable))
        #expect(!TranscriptionPipeline.installChangeNeedsPrepare(modelId: "base", readyState: ready))

        // Nemotron: always, so a Re-download of a broken variant loads again (#1632).
        #expect(TranscriptionPipeline.installChangeNeedsPrepare(
            modelId: "nemotron-asr-3.5-multilingual",
            readyState: ready
        ))

        // A cloud model is never prepared by a local install change.
        #expect(!TranscriptionPipeline.installChangeNeedsPrepare(modelId: "nova-3", readyState: unavailable))
        #expect(!TranscriptionPipeline.installChangeNeedsPrepare(modelId: "", readyState: unavailable))
    }

    // MARK: - Wiring

    private static let appPath = "app/macos/hyperwhisper/hyperwhisperApp.swift"
    private static let modelsPath =
        "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Models.swift"

    /// The root view re-prepares the selected mode when Qwen3's install changes,
    /// with the same `.dropFirst().removeDuplicates()` as the Parakeet receiver.
    @Test func theQwen3InstallReceiverRefreshesTheSelectedMode() throws {
        let receivers = try ProductionSource.slice(
            of: Self.appPath,
            from: ".onReceive(qwen3AsrModelManager.$isDownloaded) {",
            to: ".onReceive(nemotronModelManager.$availableModels) {"
        )
        // The rescan stays.
        #expect(receivers.contains("transcriptionPipeline.rescanAvailableLocalModels()"))

        let compact = receivers.filter { !$0.isWhitespace }
        #expect(compact.contains(
            ".onReceive(qwen3AsrModelManager.$isDownloaded.dropFirst().removeDuplicates())"
        ))
        #expect(compact.contains(
            "awaittranscriptionPipeline.refreshParakeetReadiness(forModeId:appState.selectedModeId)"
        ))
    }

    /// The pending refresh goes through the decision tested above.
    @Test func thePendingRefreshUsesTheInstallChangeDecision() throws {
        let body = try ProductionSource.slice(
            of: Self.modelsPath,
            from: "func refreshPendingParakeetReadinessIfReady() async {",
            to: "static func installChangeNeedsPrepare("
        )
        #expect(body.contains("Self.installChangeNeedsPrepare("))
        #expect(body.contains("await prepareModel(for: mode)"))
    }
}
