//
//  HomeStatsModel.swift
//  hyperwhisper
//
//  The four Home figures, read off the main thread and kept between visits.
//
//  Issue #1683: `HomeStatsBar` used to hold an unbatched `@FetchRequest` over
//  every completed transcript. SwiftUI ran that fetch on the main thread, in
//  the layout pass, on every Home visit: at 20k rows it blocked the window for
//  ~49 ms (SQLite sorted all rows by date and spilled the sort to disk), and
//  each visit then re-counted the words of every row although nothing had
//  changed. Now:
//
//  - `HomeStatsLoader` reads only the four columns the core needs, as
//    dictionaries, on a background context, with no sort (the core buckets by
//    date, so order does not matter).
//  - `HomeStatsModel.shared` keeps the last figures. A Home visit recomputes
//    only when a save touched a Transcript, the typing speed changed, or the
//    calendar day changed since the last result. Otherwise it shows the cached
//    figures and does no fetch and no word count.
//  - A save that touches a Transcript while Home is on screen recomputes after
//    a short debounce, so a new dictation still shows up within about a second.
//
//  The arithmetic is unchanged: the same rows go to `statsCalculateHome`.
//

import CoreData
import Foundation

/// The four integers the strip shows.
struct HomeStatsFigures: Equatable {
    var averageWPM: Int
    var monthWords: Int
    var yearWords: Int
    var savedThisWeekMinutes: Int

    static let zero = HomeStatsFigures(averageWPM: 0, monthWords: 0, yearWords: 0, savedThisWeekMinutes: 0)
}

@MainActor
final class HomeStatsModel: ObservableObject {

    static let shared = HomeStatsModel(container: PersistenceController.shared.container)

    @Published private(set) var figures = HomeStatsFigures.zero

    /// How many loads this model has started. Tests read it to prove a repeat
    /// visit with no new transcript does no work.
    private(set) var loadCount = 0

    private let container: NSPersistentContainer
    private let debounce: Duration

    /// True until the first load, and again after any save that touched a
    /// Transcript.
    private var isStale = true
    private var lastTypingSpeed: Int32?
    private var lastDay: Date?
    private var visibleCount = 0
    private var generation = 0
    private var pendingRecompute: Task<Void, Never>?
    private var saveObserver: NSObjectProtocol?

    init(container: NSPersistentContainer, debounce: Duration = .milliseconds(500)) {
        self.container = container
        self.debounce = debounce

        // Core Data posts this on the saving context's queue. Only saves into
        // this container's coordinator count, so a test's own container (or the
        // app's) never wakes the other.
        let coordinator = container.persistentStoreCoordinator
        saveObserver = NotificationCenter.default.addObserver(
            forName: .NSManagedObjectContextDidSave,
            object: nil,
            queue: nil
        ) { [weak self] note in
            guard let context = note.object as? NSManagedObjectContext,
                  context.persistentStoreCoordinator === coordinator,
                  Self.saveTouchesTranscripts(note)
            else { return }
            Task { @MainActor [weak self] in self?.transcriptsChanged() }
        }
    }

    deinit {
        if let saveObserver {
            NotificationCenter.default.removeObserver(saveObserver)
        }
    }

    // MARK: - View hooks

    func homeAppeared(typingSpeed: Int) {
        visibleCount += 1
        refreshIfNeeded(typingSpeed: Int32(clamping: typingSpeed))
    }

    func homeDisappeared() {
        visibleCount = max(0, visibleCount - 1)
    }

    /// The typing speed is an input to the core, so a new value always re-runs it.
    func typingSpeedChanged(to typingSpeed: Int) {
        recompute(typingSpeed: Int32(clamping: typingSpeed))
    }

    // MARK: - Recompute

    private func transcriptsChanged() {
        isStale = true
        guard visibleCount > 0 else { return }
        pendingRecompute?.cancel()
        pendingRecompute = Task { [weak self, debounce] in
            try? await Task.sleep(for: debounce)
            guard !Task.isCancelled, let self, self.isStale else { return }
            self.recompute(typingSpeed: self.lastTypingSpeed ?? 40)
        }
    }

    private func refreshIfNeeded(typingSpeed: Int32) {
        let today = Calendar.current.startOfDay(for: Date())
        guard isStale || lastTypingSpeed != typingSpeed || lastDay != today else { return }
        recompute(typingSpeed: typingSpeed)
    }

    private func recompute(typingSpeed: Int32) {
        pendingRecompute?.cancel()
        pendingRecompute = nil
        isStale = false
        lastTypingSpeed = typingSpeed
        lastDay = Calendar.current.startOfDay(for: Date())
        generation += 1
        loadCount += 1
        let thisGeneration = generation
        let container = self.container

        Task.detached(priority: .userInitiated) { [weak self] in
            let result = Result {
                try HomeStatsLoader.load(container: container, typingSpeed: typingSpeed, now: Date())
            }
            await MainActor.run {
                guard let self, thisGeneration == self.generation else { return }
                switch result {
                case .success(let figures):
                    self.figures = figures
                case .failure(let error):
                    // Keep the last figures, and try again on the next visit.
                    self.isStale = true
                    AppLogger.coreData.error("Home stats fetch failed: \(error.localizedDescription, privacy: .public)")
                }
            }
        }
    }

    private nonisolated static func saveTouchesTranscripts(_ note: Notification) -> Bool {
        guard let info = note.userInfo else { return false }
        if info[NSInvalidatedAllObjectsKey] != nil { return true }
        for key in [NSInsertedObjectsKey, NSUpdatedObjectsKey, NSDeletedObjectsKey] {
            if let objects = info[key] as? Set<NSManagedObject>,
               objects.contains(where: { $0.entity.name == "Transcript" }) {
                return true
            }
        }
        return false
    }
}

// MARK: - Loader

enum HomeStatsLoader {

    /// Reads every completed transcript on a fresh background context and
    /// returns the four Home figures. Never call it on the main thread.
    nonisolated static func load(
        container: NSPersistentContainer,
        typingSpeed: Int32,
        now: Date
    ) throws -> HomeStatsFigures {
        let context = container.newBackgroundContext()
        var rows: [HwStatsTranscript] = []
        var fetchError: Error?

        context.performAndWait {
            let request = NSFetchRequest<NSDictionary>(entityName: "Transcript")
            request.resultType = .dictionaryResultType
            request.propertiesToFetch = ["date", "text", "duration", "status"]
            request.predicate = NSPredicate(format: "status == %@", "completed")
            // No sort descriptor: the core buckets by date, and a sort over
            // every row is what spilled to disk.
            do {
                rows = try context.fetch(request).map(row(from:))
            } catch {
                fetchError = error
            }
        }
        if let fetchError { throw fetchError }

        return figures(rows: rows, typingSpeed: typingSpeed, now: now)
    }

    nonisolated static func row(from dictionary: NSDictionary) -> HwStatsTranscript {
        let date = dictionary["date"] as? Date
        let text = dictionary["text"] as? String
        let duration = (dictionary["duration"] as? NSNumber)?.doubleValue ?? 0
        let status = dictionary["status"] as? String
        return HwStatsTranscript(
            // A row with no date can't be placed on the calendar. Stamp it at
            // the epoch so it still counts toward the all-time average without
            // landing in the current week, month or year.
            createdAtLocalEpochSeconds: date.map { localEpochSeconds(for: $0) } ?? 0,
            wordCount: UInt32(clamping: countWordsInText(text ?? "")),
            // Handed over raw. The core normalises a non-finite or negative
            // value to 0 rather than trapping on it.
            durationSeconds: duration,
            status: statsStatus(for: status)
        )
    }

    nonisolated static func figures(
        rows: [HwStatsTranscript],
        typingSpeed: Int32,
        now: Date
    ) -> HomeStatsFigures {
        let stats = statsCalculateHome(
            transcripts: rows,
            typingSpeedWordsPerMinute: typingSpeed,
            nowLocalEpochSeconds: localEpochSeconds(for: now)
        )
        return HomeStatsFigures(
            averageWPM: Int(stats.averageWordsPerMinute),
            monthWords: Int(stats.thisMonth.wordCount),
            yearWords: Int(stats.thisYear.wordCount),
            savedThisWeekMinutes: Int(stats.savedThisWeekMinutes)
        )
    }

    nonisolated static func countWordsInText(_ text: String) -> Int {
        text.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.count
    }

    /// The core takes instants that are *already shifted* into the calendar time
    /// zone, because the host owns the time-zone database. Doing the shift per row
    /// rather than once for the whole set is what keeps DST correct.
    nonisolated static func localEpochSeconds(for date: Date) -> Int64 {
        Int64(
            (date.timeIntervalSince1970 + Double(TimeZone.current.secondsFromGMT(for: date)))
                .rounded(.down))
    }

    /// The fetch predicate already asks for completed rows only, and the core
    /// filters again. Map the column through rather than assuming, so the two
    /// filters can never disagree about what a row is.
    nonisolated static func statsStatus(for status: String?) -> HwTranscriptStatus {
        switch status {
        case "completed": return .completed
        case "failed": return .failed
        default: return .processing
        }
    }
}
