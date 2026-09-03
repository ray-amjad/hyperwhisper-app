//
//  DownloadController.swift
//  hyperwhisper
//

import Foundation
import Combine

/// Coarse stage of a model download, published alongside the fraction so the UI
/// can say *what* is happening and not only *how far along* it is.
///
/// Deliberately an app-level type rather than FluidAudio's
/// `DownloadUtils.DownloadPhase`: `DownloadController` is shared by managers that
/// do not all sit on top of FluidAudio, so this file stays FluidAudio-free.
/// `ParakeetModelManager` does the translation.
enum ModelDownloadStage: Equatable {

    /// Contacting the server / listing the repository. No byte counts yet.
    case preparing

    /// Transferring files. The counters are for the whole repository listing,
    /// not for the model's components.
    case downloading(completedFiles: Int, totalFiles: Int)

    /// Bytes are on disk; the model is being verified and CoreML-compiled.
    case processing
}

/// Shared download-task bookkeeping for the FluidAudio-backed local model
/// managers (Parakeet, Qwen3). Each manager previously hand-copied the same
/// retained-`Task` + `@Published` progress + seed-0.01/clamp + straggler-guard
/// + cancel machinery; this centralizes that core so the seed/clamp constant
/// and the straggler guard live in one place.
///
/// `Key` identifies an independent download: Parakeet keys by modelId so V2
/// and V3 can run simultaneously; Qwen3 uses a single key.
///
/// FluidAudio honours cooperative `Task` cancellation, so retaining the
/// download `Task` here is what gives the cancel button something to cancel.
@MainActor
final class DownloadController<Key: Hashable>: ObservableObject {

    /// Keys with an in-flight download.
    @Published private(set) var downloading: Set<Key> = []

    /// Per-key progress (clamped to 0.01...1.0); absent when not downloading.
    @Published private(set) var progress: [Key: Double] = [:]

    /// Per-key stage; absent when not downloading, and absent for a manager that
    /// only reports a fraction. A reader that gets `nil` should fall back to
    /// rendering the fraction alone.
    @Published private(set) var stage: [Key: ModelDownloadStage] = [:]

    /// Retained download tasks — the cancel handle. Without retaining the
    /// `Task`, cooperative cancellation has nothing to cancel.
    private var tasks: [Key: Task<Void, Never>] = [:]

    /// True while any download is in flight.
    var isDownloading: Bool { !downloading.isEmpty }

    /// True while the specific key is downloading.
    func isDownloading(_ key: Key) -> Bool { downloading.contains(key) }

    /// Start `work` for `key` unless one is already in flight. Seeds progress
    /// at 0.01 so the progress bar renders immediately, retains the cancellable `Task`,
    /// and tears everything down when `work` returns (success, error, or
    /// cancel — `work` is expected to swallow `CancellationError`).
    func start(_ key: Key, _ work: @escaping (DownloadController) async -> Void) {
        guard tasks[key] == nil, !downloading.contains(key) else { return }
        downloading.insert(key)
        // Seed at 0.01 so the progress bar renders before the first progress callback.
        progress[key] = 0.01
        tasks[key] = Task { [weak self] in
            guard let self else { return }
            await work(self)
            self.finish(key)
        }
    }

    /// Cancel an in-flight download. Teardown happens when `work` unwinds.
    func cancel(_ key: Key) {
        tasks[key]?.cancel()
    }

    /// Feed a progress fraction — and optionally a stage — from FluidAudio's
    /// callback. Applies the 0.01...1.0 clamp and drops stragglers that arrive
    /// after teardown/cancel.
    ///
    /// `stage` defaults to `nil` so a caller that has no stage to give (Qwen3)
    /// keeps working unchanged; `nil` leaves any previously reported stage alone.
    func report(_ key: Key, fraction: Double, stage: ModelDownloadStage? = nil) {
        guard downloading.contains(key) else { return }
        progress[key] = min(max(fraction, 0.01), 1.0)
        if let stage {
            self.stage[key] = stage
        }
    }

    private func finish(_ key: Key) {
        tasks.removeValue(forKey: key)
        downloading.remove(key)
        progress.removeValue(forKey: key)
        stage.removeValue(forKey: key)
    }
}
