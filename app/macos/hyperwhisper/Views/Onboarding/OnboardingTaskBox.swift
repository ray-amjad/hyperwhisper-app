//
//  OnboardingTaskBox.swift
//  hyperwhisper
//
//  Task ownership for `OnboardingFlowModel`. Moved out of OnboardingFlowModel.swift
//  unchanged (#911).
//

import Foundation

// MARK: - Task ownership

/// Holds the flow's in flight tasks so they can be cancelled from a nonisolated
/// deinit. Keyed so a second press of the same button replaces the first.
final class OnboardingTaskBox: @unchecked Sendable {
    private let lock = NSLock()
    private var tasks: [String: Task<Void, Never>] = [:]

    func store(_ task: Task<Void, Never>, for key: String) {
        lock.lock()
        let previous = tasks[key]
        tasks[key] = task
        lock.unlock()
        previous?.cancel()
    }

    func cancel(_ key: String) {
        lock.lock()
        let task = tasks.removeValue(forKey: key)
        lock.unlock()
        task?.cancel()
    }

    func clear(_ key: String) {
        lock.lock()
        tasks.removeValue(forKey: key)
        lock.unlock()
    }

    func cancelAll() {
        lock.lock()
        let all = tasks.values
        tasks.removeAll()
        lock.unlock()
        all.forEach { $0.cancel() }
    }

    var isEmpty: Bool {
        lock.lock()
        defer { lock.unlock() }
        return tasks.isEmpty
    }
}
