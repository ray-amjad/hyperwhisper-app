//
//  DownloadController.swift
//  hyperwhisper
//

import Foundation
import Combine

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

    /// Retained download tasks — the cancel handle. Without retaining the
    /// `Task`, cooperative cancellation has nothing to cancel.
    private var tasks: [Key: Task<Void, Never>] = [:]

    /// True while any download is in flight.
    var isDownloading: Bool { !downloading.isEmpty }

    /// True while the specific key is downloading.
    func isDownloading(_ key: Key) -> Bool { downloading.contains(key) }

    /// Start `work` for `key` unless one is already in flight. Seeds progress
    /// at 0.01 so the ring renders immediately, retains the cancellable `Task`,
    /// and tears everything down when `work` returns (success, error, or
    /// cancel — `work` is expected to swallow `CancellationError`).
    func start(_ key: Key, _ work: @escaping (DownloadController) async -> Void) {
        guard tasks[key] == nil, !downloading.contains(key) else { return }
        downloading.insert(key)
        // Seed at 0.01 so the ring renders before the first progress callback.
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

    /// Feed a progress fraction from FluidAudio's callback. Applies the
    /// 0.01...1.0 clamp and drops stragglers that arrive after teardown/cancel.
    func report(_ key: Key, fraction: Double) {
        guard downloading.contains(key) else { return }
        progress[key] = min(max(fraction, 0.01), 1.0)
    }

    private func finish(_ key: Key) {
        tasks.removeValue(forKey: key)
        downloading.remove(key)
        progress.removeValue(forKey: key)
    }
}
