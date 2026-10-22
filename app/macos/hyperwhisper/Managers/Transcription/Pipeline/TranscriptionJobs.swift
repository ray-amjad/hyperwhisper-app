//
//  TranscriptionJobs.swift
//  hyperwhisper
//
//  What kind of job a pipeline request is, and the two rules that keep a
//  dictation and a file job from cancelling each other (issue #1425).
//

import AppKit
import Foundation

// MARK: - Job kind

/// The two kinds of work `TranscriptionPipeline.transcribeWithDetails` runs.
///
/// A newer request supersedes (cancels) an older one of the SAME kind only:
/// a dictation still replaces a dictation, as it always has, while a dictation
/// during a file job runs beside it and neither cancels the other (Ray, #1425).
/// A second file job never supersedes silently: `FileJobGate` asks first.
enum TranscriptionJobKind: String, CaseIterable, Sendable {
    /// A recording the user just made, and the recording dialog's own retry of it.
    case dictation
    /// Transcribe File, Finder "Open With", and History's Retry / Retry with…
    case fileJob
}

extension TranscriptionState {
    /// True while a run is in flight (transcribing or post-processing).
    var isActiveRun: Bool {
        switch self {
        case .transcribing, .postProcessing:
            return true
        case .idle, .error:
            return false
        }
    }

    /// The one state the pipeline publishes when each kind has its own.
    ///
    /// A running dictation wins, then a running file job, so the pipeline reads
    /// busy (and `ModePreparationGate` keeps deferring) while EITHER runs.
    /// With nothing running, the state just written is published as before.
    static func merged(
        written: TranscriptionState,
        perKind: [TranscriptionJobKind: TranscriptionState]
    ) -> TranscriptionState {
        for kind in [TranscriptionJobKind.dictation, .fileJob] {
            if let state = perKind[kind], state.isActiveRun {
                return state
            }
        }
        return written
    }
}

// MARK: - One file job at a time, asked first

/// A file job's claim on `FileJobGate`. The job attaches its cancel once its
/// task exists; a cancel that lands before that runs as soon as it attaches.
@MainActor
final class FileJobTicket {
    let id = UUID()
    private(set) var isCancelled = false
    private var cancelAction: (@MainActor () -> Void)?

    /// Sets what cancelling this job does. Runs it at once if the job was
    /// already cancelled.
    func onCancel(_ action: @escaping @MainActor () -> Void) {
        if isCancelled {
            action()
        } else {
            cancelAction = action
        }
    }

    func cancel() {
        guard !isCancelled else { return }
        isCancelled = true
        let action = cancelAction
        cancelAction = nil
        action?()
    }
}

/// Asks before a second file job replaces the running one (Ray, #1425).
///
/// Every file-job entry point calls `begin()` BEFORE it copies audio or makes
/// a History row, so Keep Running leaves nothing behind. Dictation never
/// goes through here.
@MainActor
final class FileJobGate {
    /// The file job running now, if any.
    private(set) var running: FileJobTicket?

    /// Asks the user whether to cancel the running file job for a new one.
    /// `true` means Cancel It and Start. Tests replace it.
    var confirmReplace: @MainActor () async -> Bool = { await FileJobGate.askToReplaceRunningJob() }

    /// Claims the gate for a new file job.
    ///
    /// - Returns: the new job's ticket, or `nil` when a job is running and the
    ///   user chose Keep Running: the caller must then do nothing at all.
    func begin() async -> FileJobTicket? {
        if running != nil {
            guard await confirmReplace() else {
                AppLogger.transcription.info("A file job is running; the user kept it, so the new one does not start")
                return nil
            }
            // Whatever runs NOW: the one asked about may have ended, or another
            // answer may have replaced it, while the alert was up.
            if let current = running {
                AppLogger.transcription.info("Cancelling the running file job to start the new one")
                running = nil
                current.cancel()
            }
        }
        let ticket = FileJobTicket()
        running = ticket
        return ticket
    }

    /// Releases the gate, if this ticket still holds it.
    func end(_ ticket: FileJobTicket?) {
        guard let ticket, running === ticket else { return }
        running = nil
    }

    /// The alert. Keep Running is the first button, so Return picks it.
    static func askToReplaceRunningJob() async -> Bool {
        await withCheckedContinuation { continuation in
            let alert = NSAlert()
            alert.messageText = "transcribe.file.busy.title".localized
            alert.informativeText = "transcribe.file.busy.message".localized
            alert.alertStyle = .warning
            alert.addButton(withTitle: "transcribe.file.busy.keepRunning".localized)
            let replace = alert.addButton(withTitle: "transcribe.file.busy.cancelAndStart".localized)
            replace.hasDestructiveAction = true
            ModelessAlert.show(alert) { response in
                continuation.resume(returning: response == .alertSecondButtonReturn)
            }
        }
    }
}

// MARK: - One engine, one kind at a time

/// Keeps a dictation and a file job off the same engine at the same time.
///
/// Same-kind runs pass straight through (the newer one supersedes the older
/// one, as before). A run of the other kind waits, in arrival order, until
/// the engine is free of the first kind. Only providers that cannot run two
/// passes at once (`supportsConcurrentPasses == false`) go through here:
/// local Whisper has one context and cancels its own running pass for a new
/// one, and HyperWhisper Cloud keeps the AI text and detected language of
/// "the last call" on the shared instance.
@MainActor
final class TranscriptionEngineAccess {
    private struct Waiter {
        let id: UUID
        let kind: TranscriptionJobKind
        let continuation: CheckedContinuation<Void, Error>
    }

    private struct Engine {
        var holders: [TranscriptionJobKind: Int] = [:]
        var waiters: [Waiter] = []
    }

    private var engines: [ObjectIdentifier: Engine] = [:]

    /// The key for a provider instance.
    static func key(for provider: any TranscriptionProvider) -> ObjectIdentifier {
        ObjectIdentifier(provider as AnyObject)
    }

    /// Number of runs of `kind` holding the engine now.
    func holders(of kind: TranscriptionJobKind, on key: ObjectIdentifier) -> Int {
        engines[key]?.holders[kind] ?? 0
    }

    /// Number of runs waiting for the engine now.
    func waiterCount(on key: ObjectIdentifier) -> Int {
        engines[key]?.waiters.count ?? 0
    }

    /// Returns once `kind` may use the engine. Throws `CancellationError` if
    /// the waiting task is cancelled first; it then holds nothing.
    func acquire(_ key: ObjectIdentifier, for kind: TranscriptionJobKind) async throws {
        try Task.checkCancellation()
        if canGrant(kind, on: key) {
            engines[key, default: Engine()].holders[kind, default: 0] += 1
            return
        }
        let id = UUID()
        AppLogger.transcription.info("Engine busy with another kind of job; \(kind.rawValue, privacy: .public) waits for it")
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                engines[key, default: Engine()].waiters.append(
                    Waiter(id: id, kind: kind, continuation: continuation)
                )
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.dropWaiter(id, on: key)
            }
        }
    }

    /// Gives the engine back, and lets in whoever waits next.
    func release(_ key: ObjectIdentifier, for kind: TranscriptionJobKind) {
        guard var engine = engines[key], let count = engine.holders[kind], count > 0 else { return }
        engine.holders[kind] = count - 1 == 0 ? nil : count - 1
        engines[key] = engine
        grantWaiters(on: key)
    }

    private func canGrant(_ kind: TranscriptionJobKind, on key: ObjectIdentifier) -> Bool {
        guard let engine = engines[key] else { return true }
        return engine.holders.allSatisfy { holderKind, count in holderKind == kind || count == 0 }
    }

    /// Lets waiters in, in order, while the first one fits.
    private func grantWaiters(on key: ObjectIdentifier) {
        while let first = engines[key]?.waiters.first, canGrant(first.kind, on: key) {
            engines[key]?.waiters.removeFirst()
            engines[key, default: Engine()].holders[first.kind, default: 0] += 1
            first.continuation.resume()
        }
        if let engine = engines[key], engine.holders.isEmpty, engine.waiters.isEmpty {
            engines[key] = nil
        }
    }

    private func dropWaiter(_ id: UUID, on key: ObjectIdentifier) {
        guard let index = engines[key]?.waiters.firstIndex(where: { $0.id == id }),
              let waiter = engines[key]?.waiters.remove(at: index) else {
            return
        }
        waiter.continuation.resume(throwing: CancellationError())
        // The dropped waiter may have been the one blocking the queue.
        grantWaiters(on: key)
    }
}
