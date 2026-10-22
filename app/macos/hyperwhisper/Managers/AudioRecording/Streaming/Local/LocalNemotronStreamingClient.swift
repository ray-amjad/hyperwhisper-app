//
//  LocalNemotronStreamingClient.swift
//  hyperwhisper
//
//  On-device streaming for Nemotron 3.5 ASR Streaming Multilingual: the
//  engine step for `LocalStreamingClient`, talking to a
//  `NemotronStreamingSession`. The session lifecycle is shared and lives in
//  `LocalStreamingClient.swift`.
//

import Foundation
import FluidAudio

@available(macOS 14.0, *)
typealias LocalNemotronStreamingClient = LocalStreamingClient<LocalNemotronStreamingEngine>

@available(macOS 14.0, *)
extension LocalStreamingClient where Engine == LocalNemotronStreamingEngine {
    convenience init(variant: NemotronModelManager.Variant, language: String?, provider: NemotronProvider? = nil) {
        self.init(engine: LocalNemotronStreamingEngine(variant: variant, language: language, provider: provider))
    }
}

@available(macOS 14.0, *)
@MainActor
struct LocalNemotronStreamingEngine: LocalStreamingEngine {
    let variant: NemotronModelManager.Variant
    let language: String?

    /// Optional provider handle used to share the batch-side `Runtime` cache.
    /// When wired (current code), the first streaming PTT after a batch run
    /// (or a prior streaming session) skips the ~1–3 s shared-model preload.
    /// When nil (preview/test), the engine falls back to a direct FluidAudio
    /// download+preload call.
    weak var provider: NemotronProvider?

    /// `NemotronStreamingSession.finish()` does not emit its final text
    /// through `onConfirmedDelta`; it returns it for the client to deliver.
    static var finalTextDelivery: LocalStreamingFinalTextDelivery { .finishReturnValue }

    var providerLabel: String {
        switch variant {
        case .latin: return "Nemotron 3.5 Latin (On-Device Streaming)"
        case .multilingual: return "Nemotron 3.5 Multilingual (On-Device Streaming)"
        }
    }

    var logCategory: String { "LocalNemotronStreaming" }
    var logName: String { "Nemotron" }
    var logVariant: String { variant.rawValue }
    var modelsLogName: String { "shared models" }

    func checkModelsPresent() -> LocalStreamingModelCheck {
        let variantModelId = variant == .latin
            ? NemotronModelManager.Constants.latinModelId
            : NemotronModelManager.Constants.multilingualModelId
        guard NemotronModelManager.isVariantInstalled(variantModelId) else {
            return .missing(
                stateMessage: "Nemotron model not downloaded",
                error: NemotronStreamingError.modelsNotAvailable
            )
        }
        return .present(logDetail: "model files present")
    }

    /// The provider's Runtime actor caches the bundle per variant, so a second
    /// PTT (or a streaming PTT after a batch run) skips the CoreML
    /// compile/load entirely. When no provider is wired (preview/test), fall
    /// back to a direct FluidAudio call.
    func loadModels() async throws -> SharedNemotronMultilingualModels {
        if let provider {
            return try await provider.sharedModels(for: variant)
        }
        return try await StreamingNemotronMultilingualAsrManager.downloadAndPreloadShared(
            languageCode: variant.downloadLanguageHint,
            chunkMs: NemotronModelManager.Constants.chunkMs,
            to: nil,
            configuration: nil,
            progressHandler: nil
        )
    }

    func makeSession(models: SharedNemotronMultilingualModels) async throws -> NemotronStreamingSession {
        try await NemotronStreamingSession(shared: models, variant: variant, language: language)
    }
}

@available(macOS 14.0, *)
extension NemotronStreamingSession: LocalStreamingSession {}
