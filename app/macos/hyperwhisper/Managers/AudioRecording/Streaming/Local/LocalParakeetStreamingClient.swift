//
//  LocalParakeetStreamingClient.swift
//  hyperwhisper
//
//  On-device Parakeet streaming: the engine step for `LocalStreamingClient`.
//  The audio goes to a local `ParakeetStreamingSession` (agreement-based
//  passes over FluidAudio's `AsrManager`). The session lifecycle is shared
//  and lives in `LocalStreamingClient.swift`.
//

import Foundation
import FluidAudio

@available(macOS 13.0, *)
typealias LocalParakeetStreamingClient = LocalStreamingClient<LocalParakeetStreamingEngine>

@available(macOS 13.0, *)
extension LocalStreamingClient where Engine == LocalParakeetStreamingEngine {
    convenience init(version: AsrModelVersion) {
        self.init(engine: LocalParakeetStreamingEngine(version: version))
    }
}

@available(macOS 13.0, *)
@MainActor
struct LocalParakeetStreamingEngine: LocalStreamingEngine {
    let version: AsrModelVersion

    /// `ParakeetStreamingSession.finish()` emits the un-confirmed tail through
    /// `onConfirmedDelta` and returns the WHOLE accumulated transcript, so the
    /// return value must not be typed again (#807).
    static var finalTextDelivery: LocalStreamingFinalTextDelivery { .confirmedDeltaCallback }

    var providerLabel: String {
        switch version {
        case .v2: return "Parakeet V2 (On-Device Streaming)"
        case .v3: return "Parakeet V3 (On-Device Streaming)"
        case .tdtCtc110m: return "Parakeet (On-Device Streaming)"
        @unknown default: return "Parakeet (On-Device Streaming)"
        }
    }

    var logCategory: String { "LocalParakeetStreaming" }
    var logName: String { "Local Parakeet" }
    var logVariant: String { String(describing: version) }
    var modelsLogName: String { "models" }

    func checkModelsPresent() -> LocalStreamingModelCheck {
        let directory = AsrModels.defaultCacheDirectory(for: version)
        guard AsrModels.modelsExist(at: directory) else {
            return .missing(
                stateMessage: "Parakeet model not downloaded",
                error: ParakeetStreamingError.modelsNotAvailable
            )
        }
        return .present(logDetail: "models present at \(directory.path)")
    }

    /// Cheap: already on disk, memory-mapped by CoreML.
    func loadModels() async throws -> AsrModels {
        try await AsrModels.downloadAndLoad(version: version)
    }

    func makeSession(models: AsrModels) async throws -> ParakeetStreamingSession {
        try await ParakeetStreamingSession(models: models)
    }
}

@available(macOS 13.0, *)
extension ParakeetStreamingSession: LocalStreamingSession {}
