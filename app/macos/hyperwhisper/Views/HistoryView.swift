//
//  HistoryView.swift
//  hyperwhisper
//
//  Created by Rehman Amjad on 16/08/2025.
//
//  HISTORY VIEW
//  Displays and manages transcription history.
//  Users can search, filter, export, and manage their past transcriptions.
//
//  Features:
//  - Searchable list of transcriptions
//  - Date filtering
//  - Export capabilities
//  - Playback of original audio (if kept)
//  - Copy/share functionality

import SwiftUI
import Combine
import CoreData
import AVFoundation
import os

/// Logger for HistoryView (static to work with SwiftUI structs)
private let historyViewLogger = Logger(subsystem: "com.hyperwhisper.app", category: "HistoryView")

// MARK: - History View

/// A complete description of one history fetch.
///
/// INVARIANT: never yield a bare "please refresh" signal into the query stream —
/// always yield a whole `HistoryQuery` built from current state at yield time.
/// That's what makes the stream's `.bufferingNewest(1)` conflation lossless:
/// whichever query survives always carries the current searchText, dateFilter,
/// and fetchLimit together (e.g. a save-triggered refresh mid-typing still
/// queries the current text).
private struct HistoryQuery {
    let searchText: String
    let dateFilter: DateFilter
    let fetchLimit: Int
}

private struct HistoryItemSnapshot: Identifiable, Hashable {
    let objectID: NSManagedObjectID
    let transcriptID: UUID?
    let previewText: String
    let date: Date
    let duration: TimeInterval
    let isFailed: Bool
    let hasAudioPath: Bool
    let canRetry: Bool
    let text: String
    /// nil when empty — preserves the "only write postProcessedText if it was
    /// non-empty" edit-save semantics in the detail view.
    let postProcessedText: String?
    let transcribedText: String?
    let mode: String?
    let transcriptionProvider: String?
    let postProcessingProvider: String?
    let retryCount: Int16
    let status: String?
    /// nil when empty.
    let failedReason: String?
    /// Whitespace-trimmed original audio path; nil when empty.
    let audioFilePath: String?
    let trimmedAudioFilePath: String?

    var id: NSManagedObjectID { objectID }
}

private struct HistorySectionSnapshot: Identifiable, Hashable {
    let date: Date
    let items: [HistoryItemSnapshot]

    var id: Date { date }
}

private struct HistoryQueryResult {
    let sections: [HistorySectionSnapshot]
    let objectIDs: Set<NSManagedObjectID>
    let hasMoreResults: Bool
}

private actor HistoryDataLoader {
    func load(query: HistoryQuery) async -> HistoryQueryResult {
        let searchText = query.searchText
        let dateFilter = query.dateFilter
        let limit = query.fetchLimit

        let context = PersistenceController.shared.container.newBackgroundContext()
        context.mergePolicy = NSMergeByPropertyStoreTrumpMergePolicy
        context.automaticallyMergesChangesFromParent = false

        return await context.perform {
            let request = NSFetchRequest<Transcript>(entityName: "Transcript")
            request.sortDescriptors = [NSSortDescriptor(key: "date", ascending: false)]
            request.fetchLimit = limit
            request.fetchBatchSize = min(limit, 100)
            request.returnsObjectsAsFaults = false
            request.includesPendingChanges = false
            request.predicate = Self.makePredicate(searchText: searchText, dateFilter: dateFilter)

            let snapshots: [HistoryItemSnapshot]
            do {
                let fetched = try context.fetch(request)
                var latestSnapshotByAudioIdentity: [String: HistoryItemSnapshot] = [:]
                var recentFingerprintSet = Set<String>()
                var built: [HistoryItemSnapshot] = []
                built.reserveCapacity(fetched.count)

                for transcript in fetched {
                    let fallbackText = transcript.text ?? ""
                    let processedText = (transcript.value(forKey: "postProcessedText") as? String) ?? ""
                    let previewSource = processedText.isEmpty ? fallbackText : processedText

                    let status = transcript.value(forKey: "status") as? String
                    let failedReason = transcript.value(forKey: "failedReason") as? String
                    let hasFailedReason = (failedReason?.isEmpty == false)
                    let localizedTranscriptionFailed = "history.status.transcription.failed.prefix".localized
                    let localizedRetryFailed = "history.status.retry.failed.prefix".localized

                    let isFailed = status == "failed" || hasFailedReason ||
                        fallbackText.starts(with: localizedTranscriptionFailed) ||
                        fallbackText.starts(with: "Transcription failed:") ||
                        fallbackText.starts(with: localizedRetryFailed) ||
                        fallbackText.starts(with: "Retry failed:")

                    let audioPath = (transcript.audioFilePath ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    let hasAudioPath = !audioPath.isEmpty

                    // Safety dedupe for transient twins:
                    // During rapid status transitions, duplicate rows can briefly appear.
                    // We dedupe by:
                    // 1) audio identity (same file stem even if extension changes wav↔m4a)
                    // 2) recent fingerprint (same second + duration + preview) as fallback.
                    let preview = Self.makePreview(previewSource)
                    let date = transcript.date ?? .distantPast
                    let duration = transcript.duration

                    if hasAudioPath {
                        let identity = Self.audioIdentity(for: audioPath)
                        if let existing = latestSnapshotByAudioIdentity[identity] {
                            let nearInTime = abs(existing.date.timeIntervalSince(date)) <= 20
                            let nearInDuration = abs(existing.duration - duration) <= 0.5
                            let samePreview = existing.previewText == preview

                            if nearInTime && (nearInDuration || samePreview) {
                                continue
                            }
                        }
                    }

                    let isRecent = abs(date.timeIntervalSinceNow) <= 120
                    if isRecent {
                        let secondBucket = Int(date.timeIntervalSince1970.rounded())
                        let durationBucket = Int((duration * 10).rounded())
                        let fingerprint = "\(secondBucket)|\(durationBucket)|\(Self.previewFingerprint(preview))"
                        if recentFingerprintSet.contains(fingerprint) {
                            continue
                        }
                        recentFingerprintSet.insert(fingerprint)
                    }

                    let snapshot = HistoryItemSnapshot(
                        objectID: transcript.objectID,
                        transcriptID: transcript.id,
                        previewText: preview,
                        date: date,
                        duration: duration,
                        isFailed: isFailed,
                        hasAudioPath: hasAudioPath,
                        canRetry: isFailed && hasAudioPath,
                        text: fallbackText,
                        postProcessedText: processedText.isEmpty ? nil : processedText,
                        transcribedText: transcript.value(forKey: "transcribedText") as? String,
                        mode: transcript.mode,
                        transcriptionProvider: transcript.value(forKey: "transcriptionProvider") as? String,
                        postProcessingProvider: transcript.value(forKey: "postProcessingProvider") as? String,
                        retryCount: transcript.value(forKey: "retryCount") as? Int16 ?? 0,
                        status: status,
                        failedReason: hasFailedReason ? failedReason : nil,
                        audioFilePath: hasAudioPath ? audioPath : nil,
                        trimmedAudioFilePath: transcript.value(forKey: "trimmedAudioFilePath") as? String
                    )

                    built.append(snapshot)
                    if hasAudioPath {
                        latestSnapshotByAudioIdentity[Self.audioIdentity(for: audioPath)] = snapshot
                    }
                }
                snapshots = built
            } catch {
                historyViewLogger.error("Failed fetching history snapshots: \(error.localizedDescription, privacy: .public)")
                return HistoryQueryResult(sections: [], objectIDs: [], hasMoreResults: false)
            }

            var grouped: [Date: [HistoryItemSnapshot]] = [:]
            grouped.reserveCapacity(16)

            let calendar = Calendar.autoupdatingCurrent
            for item in snapshots {
                let dayStart = calendar.startOfDay(for: item.date)
                grouped[dayStart, default: []].append(item)
            }

            let orderedDates = grouped.keys.sorted(by: >)
            let sections = orderedDates.map { date in
                HistorySectionSnapshot(date: date, items: grouped[date] ?? [])
            }

            let countRequest = NSFetchRequest<Transcript>(entityName: "Transcript")
            countRequest.includesPendingChanges = false
            countRequest.predicate = Self.makePredicate(searchText: searchText, dateFilter: dateFilter)
            let totalCount = (try? context.count(for: countRequest)) ?? snapshots.count

            return HistoryQueryResult(
                sections: sections,
                objectIDs: Set(snapshots.map(\.objectID)),
                hasMoreResults: totalCount > snapshots.count
            )
        }
    }

    private static func makePredicate(searchText: String, dateFilter: DateFilter) -> NSPredicate? {
        let trimmedSearch = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        var predicates: [NSPredicate] = []

        if !trimmedSearch.isEmpty {
            predicates.append(
                NSCompoundPredicate(orPredicateWithSubpredicates: [
                    NSPredicate(format: "text CONTAINS[cd] %@", trimmedSearch),
                    NSPredicate(format: "postProcessedText CONTAINS[cd] %@", trimmedSearch),
                    NSPredicate(format: "transcribedText CONTAINS[cd] %@", trimmedSearch)
                ])
            )
        }

        if let datePredicate = dateFilter.predicate(referenceDate: Date()) {
            predicates.append(datePredicate)
        }

        switch predicates.count {
        case 0:
            return nil
        case 1:
            return predicates[0]
        default:
            return NSCompoundPredicate(andPredicateWithSubpredicates: predicates)
        }
    }

    private static func makePreview(_ text: String) -> String {
        guard !text.isEmpty else { return "" }

        let normalized = text
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        guard normalized.count > 200 else { return normalized }
        return String(normalized.prefix(200))
    }

    private static func previewFingerprint(_ preview: String) -> String {
        let normalized = preview
            .lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard normalized.count > 120 else { return normalized }
        return String(normalized.prefix(120))
    }

    private static func audioIdentity(for audioPath: String) -> String {
        let trimmed = audioPath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "" }

        let stem = URL(fileURLWithPath: trimmed)
            .deletingPathExtension()
            .lastPathComponent
            .lowercased()
        return stem.isEmpty ? trimmed.lowercased() : stem
    }
}

/// Owns the history query pipeline: every trigger (typing, date filter, save
/// notifications, pagination) is funneled into one conflating `AsyncStream` of
/// complete `HistoryQuery` values, consumed by a single serial loop. Ordering
/// and only-newest-applies fall out of the structure — no debounce tasks or
/// generation counters to get wrong.
@MainActor
private final class HistoryViewModel: ObservableObject {
    @Published var searchText: String = "" {
        // The debounced enqueue happens via the Combine sink in init; here we
        // only reset pagination so a new search starts from the first page.
        didSet { fetchLimit = Self.defaultFetchLimit }
    }
    @Published var dateFilter: DateFilter = .all {
        didSet {
            fetchLimit = Self.defaultFetchLimit
            enqueueCurrentQuery()
        }
    }
    @Published private(set) var sections: [HistorySectionSnapshot] = []
    /// Identity key of the query whose results are currently in `sections`,
    /// updated atomically with them. The List's `.id` must key off this — not
    /// the live `searchText`, which changes on every keystroke, ~180ms before
    /// the debounced results land. Keying off the live text rebuilds the list
    /// too early (while it still shows the old result set) and then lets
    /// SwiftUI diff two unrelated result sets inside one NSTableView when the
    /// results do arrive — which mis-reuses cells: stale row content under new
    /// section headers and selection tags (the row/detail mismatch bug).
    /// Excludes fetchLimit so pagination doesn't rebuild the list.
    @Published private(set) var appliedQueryKey: String = ""
    @Published private(set) var loadedObjectIDs: Set<NSManagedObjectID> = []
    @Published private(set) var hasMoreResults: Bool = false
    @Published private(set) var availableModes: [Mode] = []
    @Published private(set) var isLoading: Bool = false

    private static let defaultFetchLimit = 200

    private let dataLoader = HistoryDataLoader()
    private var viewContext: NSManagedObjectContext?
    private var saveObserver: NSObjectProtocol?
    private let queryContinuation: AsyncStream<HistoryQuery>.Continuation
    /// Held until configureIfNeeded starts the consumer; consumed exactly once.
    private var pendingQueryStream: AsyncStream<HistoryQuery>?
    private var consumerTask: Task<Void, Never>?
    private var cancellables = Set<AnyCancellable>()
    private var fetchLimit = HistoryViewModel.defaultFetchLimit
    private var isConfigured = false

    init() {
        // Conflating stream: only the newest pending query is kept while a load
        // is in flight, and the consumer applies queries strictly one at a time,
        // so a stale result can never overwrite a newer one.
        let (stream, continuation) = AsyncStream<HistoryQuery>.makeStream(
            bufferingPolicy: .bufferingNewest(1)
        )
        pendingQueryStream = stream
        queryContinuation = continuation

        // Debounced search. The sink ignores the published value and re-reads
        // current state via enqueueCurrentQuery — @Published publishers fire on
        // willSet, so the parameter can be stale; live state is always current.
        // A save arriving mid-debounce may query the partially-typed text early —
        // that's current truth too, never stale.
        $searchText
            .dropFirst() // @Published replays the initial value on subscription
            .removeDuplicates()
            .debounce(for: .milliseconds(180), scheduler: RunLoop.main)
            .sink { [weak self] _ in
                self?.enqueueCurrentQuery()
            }
            .store(in: &cancellables)
    }

    deinit {
        // finish() alone terminates the consumer loop; cancel is belt-and-braces.
        queryContinuation.finish()
        consumerTask?.cancel()
        if let saveObserver {
            NotificationCenter.default.removeObserver(saveObserver)
        }
    }

    func configureIfNeeded(viewContext: NSManagedObjectContext) {
        guard !isConfigured else { return }
        isConfigured = true
        self.viewContext = viewContext
        refreshAvailableModes()
        observeTranscriptChanges()
        startQueryConsumer()
        enqueueCurrentQuery()
    }

    func transcript(for objectID: NSManagedObjectID) -> Transcript? {
        guard let viewContext else { return nil }
        return (try? viewContext.existingObject(with: objectID)) as? Transcript
    }

    func transcripts(for objectIDs: Set<NSManagedObjectID>) -> [Transcript] {
        objectIDs.compactMap { transcript(for: $0) }
    }

    func loadMoreIfNeeded(currentItemID: NSManagedObjectID) {
        guard hasMoreResults, !isLoading else { return }
        guard let lastID = sections.last?.items.last?.objectID else { return }
        guard currentItemID == lastID else { return }
        fetchLimit += Self.defaultFetchLimit
        enqueueCurrentQuery()
    }

    private func startQueryConsumer() {
        // Started from a @MainActor context, so the loop inherits MainActor.
        consumerTask = Task { [weak self] in
            guard let stream = self?.takePendingQueryStream() else { return }
            for await query in stream {
                // Hold self strongly only for the duration of one iteration.
                guard let self else { break }
                await self.perform(query)
            }
        }
    }

    private func takePendingQueryStream() -> AsyncStream<HistoryQuery>? {
        defer { pendingQueryStream = nil }
        return pendingQueryStream
    }

    /// Single entry point for all refresh triggers. Always yields a complete
    /// query built from current state (see the invariant on `HistoryQuery`).
    private func enqueueCurrentQuery() {
        queryContinuation.yield(
            HistoryQuery(searchText: searchText, dateFilter: dateFilter, fetchLimit: fetchLimit)
        )
    }

    private func perform(_ query: HistoryQuery) async {
        isLoading = true
        let result = await dataLoader.load(query: query)
        // Serial consumption: no other load can interleave at the await above,
        // so the result always belongs to the newest applied query — no
        // staleness guard needed.
        sections = result.sections
        appliedQueryKey = "\(query.searchText)|\(query.dateFilter)"
        loadedObjectIDs = result.objectIDs
        hasMoreResults = result.hasMoreResults
        isLoading = false
    }

    private func refreshAvailableModes() {
        let allModes = PersistenceController.shared.fetchAllModes()

        if NetworkStatus.shared.isOnline {
            availableModes = allModes
            return
        }

        availableModes = allModes.filter { mode in
            let isCloudModel = (mode.model ?? "").lowercased() == "cloud"
            let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
            let hasCloudPostProcessing = processingMode.requiresInternet
            return !isCloudModel && !hasCloudPostProcessing
        }
    }

    private func observeTranscriptChanges() {
        saveObserver = NotificationCenter.default.addObserver(
            forName: .NSManagedObjectContextDidSave,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            Task { @MainActor [weak self] in
                guard let self, self.containsTranscriptChanges(notification) else { return }
                self.enqueueCurrentQuery()
            }
        }
    }

    private func containsTranscriptChanges(_ notification: Notification) -> Bool {
        let keys = [NSInsertedObjectsKey, NSUpdatedObjectsKey, NSDeletedObjectsKey]
        for key in keys {
            guard let objects = notification.userInfo?[key] as? Set<NSManagedObject> else {
                continue
            }
            if objects.contains(where: { $0.entity.name == "Transcript" }) {
                return true
            }
        }
        return false
    }
}

/// Main view for transcription history
/// Rewritten to isolate expensive work from SwiftUI render cycles.
struct HistoryView: View {
    @EnvironmentObject private var transcriptionPipeline: TranscriptionPipeline

    var body: some View {
        HistoryScreen(transcriptionPipeline: transcriptionPipeline)
            .equatable()
    }
}

private struct HistoryScreen: View, Equatable {
    let transcriptionPipeline: TranscriptionPipeline

    @Environment(\.managedObjectContext) private var viewContext

    @StateObject private var viewModel = HistoryViewModel()
    @State private var selectedTranscriptIDs: Set<NSManagedObjectID> = []
    @State private var actionHandler: TranscriptActionHandler?

    static func == (lhs: HistoryScreen, rhs: HistoryScreen) -> Bool {
        ObjectIdentifier(lhs.transcriptionPipeline) == ObjectIdentifier(rhs.transcriptionPipeline)
    }

    private var selectedTranscriptObjects: [Transcript] {
        viewModel.transcripts(for: selectedTranscriptIDs)
    }

    private var selectedSnapshot: HistoryItemSnapshot? {
        guard selectedTranscriptIDs.count == 1, let objectID = selectedTranscriptIDs.first else {
            return nil
        }
        // Look up the snapshot in the same sections array the rows rendered
        // from, so the detail pane can never disagree with the row the user
        // clicked — the load-bearing property of this pipeline.
        return viewModel.sections.lazy.flatMap(\.items).first { $0.objectID == objectID }
    }

    var body: some View {
        HSplitView {
            historyList
                .frame(width: 280)

            Group {
                if let snapshot = selectedSnapshot, let handler = actionHandler {
                    TranscriptDetailView(
                        snapshot: snapshot,
                        actionHandler: handler,
                        onDelete: { selectedTranscriptIDs.removeAll() }
                    )
                    .id(snapshot.objectID)
                } else if selectedTranscriptIDs.count > 1 {
                    multiSelectionView
                } else {
                    emptyDetailView
                }
            }
            .animation(nil, value: selectedTranscriptIDs)
        }
        .navigationTitle("history.title")
        .onAppear {
            viewModel.configureIfNeeded(viewContext: viewContext)
            if actionHandler == nil {
                actionHandler = TranscriptActionHandler(transcriptionPipeline: transcriptionPipeline)
            }
        }
        .onChange(of: viewModel.loadedObjectIDs) { _, ids in
            selectedTranscriptIDs.formIntersection(ids)
        }
    }

    private var historyList: some View {
        VStack(spacing: 0) {
            searchBar
            Divider()
            transcriptsList
        }
    }

    @ViewBuilder
    private var transcriptsList: some View {
        if viewModel.sections.isEmpty {
            emptyState
        } else {
            List(selection: $selectedTranscriptIDs) {
                ForEach(viewModel.sections) { section in
                    Section(header: Text(formatSectionDate(section.date))) {
                        ForEach(section.items) { item in
                            TranscriptRow(
                                item: item,
                                isRetrying: isRetrying(item),
                                isDeleting: isDeleting(item)
                            )
                            .tag(item.objectID)
                            .contextMenu {
                                contextMenuItems(for: item)
                            }
                            .onAppear {
                                viewModel.loadMoreIfNeeded(currentItemID: item.objectID)
                            }
                        }
                    }
                }

                if viewModel.hasMoreResults {
                    HStack {
                        Spacer()
                        ProgressView()
                            .controlSize(.small)
                        Spacer()
                    }
                    .listRowSeparator(.hidden)
                }
            }
            .listStyle(.sidebar)
            // Cheap insurance: rebuild the NSTableView-backed List when the query
            // changes rather than diffing rows across unrelated result sets.
            // Keyed off the query that PRODUCED the current sections (set
            // atomically with them), not the live searchText — see
            // appliedQueryKey for why keystroke-time rebuilds don't protect
            // the dangerous transition.
            .id(viewModel.appliedQueryKey)
            .background(
                Group {
                    Button("") {
                        if !selectedTranscriptObjects.isEmpty {
                            deleteSelectedTranscripts()
                        }
                    }
                    .keyboardShortcut(.delete, modifiers: [])
                    .hidden()

                    Button("") {
                        guard selectedTranscriptObjects.count == 1,
                              let transcript = selectedTranscriptObjects.first,
                              let handler = actionHandler else {
                            return
                        }
                        handler.copyTranscriptText(transcript)
                    }
                    .keyboardShortcut("c", modifiers: .command)
                    .hidden()
                }
            )
        }
    }

    @ViewBuilder
    private func contextMenuItems(for item: HistoryItemSnapshot) -> some View {
        if selectedTranscriptIDs.contains(item.objectID), selectedTranscriptIDs.count > 1 {
            Button(action: deleteSelectedTranscripts) {
                Label {
                    Text("history.context.deleteSelected".localized(arguments: selectedTranscriptIDs.count))
                } icon: {
                    Image(systemName: "trash")
                }
            }
            .keyboardShortcut(.delete, modifiers: [])
        } else {
            if item.canRetry {
                Button(action: { retryTranscript(item.objectID) }) {
                    Label {
                        Text(localized: "history.context.retry")
                    } icon: {
                        Image(systemName: "arrow.clockwise")
                    }
                }
                .disabled(isRetrying(item))

                Divider()
            }

            if item.hasAudioPath {
                Menu {
                    ForEach(viewModel.availableModes, id: \.objectID) { mode in
                        Button {
                            retryTranscript(item.objectID, with: mode)
                        } label: {
                            Text(mode.name ?? "Unknown")
                        }
                    }
                } label: {
                    Label {
                        Text(localized: "history.context.retryWith")
                    } icon: {
                        Image(systemName: "arrow.triangle.2.circlepath")
                    }
                }

                Divider()
            }

            Button(action: { copyTranscript(item.objectID) }) {
                Label {
                    Text(localized: "history.context.copy")
                } icon: {
                    Image(systemName: "doc.on.doc")
                }
            }

            Divider()

            Button(action: { deleteSingleTranscript(item.objectID) }) {
                Label {
                    Text(localized: "common.delete")
                } icon: {
                    Image(systemName: "trash")
                }
            }
        }
    }

    private var searchBar: some View {
        HStack(spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass")
                    .foregroundColor(.secondary)
                TextField("history.search.placeholder", text: $viewModel.searchText)
                    .textFieldStyle(.plain)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(Color(NSColor.controlBackgroundColor))
            .cornerRadius(8)
            .overlay(
                RoundedRectangle(cornerRadius: 8)
                    .stroke(Color.secondary.opacity(0.25), lineWidth: 1)
            )

            Menu {
                Button {
                    viewModel.dateFilter = .all
                } label: {
                    Text(localized: DateFilter.all.localizedTitleKey)
                }
                Button {
                    viewModel.dateFilter = .today
                } label: {
                    Text(localized: DateFilter.today.localizedTitleKey)
                }
                Button {
                    viewModel.dateFilter = .week
                } label: {
                    Text(localized: DateFilter.week.localizedTitleKey)
                }
                Button {
                    viewModel.dateFilter = .month
                } label: {
                    Text(localized: DateFilter.month.localizedTitleKey)
                }
            } label: {
                HStack(spacing: 6) {
                    Text(localized: viewModel.dateFilter.localizedTitleKey)
                        .font(.system(size: 13, weight: .medium))
                    Image(systemName: "chevron.down")
                        .font(.system(size: 10, weight: .medium))
                }
                .foregroundColor(.primary)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color(NSColor.controlBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(Color.secondary.opacity(0.2), lineWidth: 1)
                )
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "clock.badge.questionmark")
                .font(.system(size: 48))
                .foregroundColor(.secondary)

            Text(localized: "history.empty.title")
                .font(.headline)
                .foregroundColor(.secondary)

            if !viewModel.searchText.isEmpty {
                Text(localized: "history.empty.subtitle")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    private var emptyDetailView: some View {
        VStack(spacing: 16) {
            Image(systemName: "text.alignleft")
                .font(.system(size: 48))
                .foregroundColor(.secondary)

            Text(localized: "history.detail.empty")
                .font(.headline)
                .foregroundColor(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(NSColor.windowBackgroundColor))
        .animation(nil, value: selectedTranscriptIDs)
    }

    private var multiSelectionView: some View {
        VStack(spacing: 24) {
            Spacer()

            VStack(spacing: 16) {
                Image(systemName: "square.stack.3d.up")
                    .font(.system(size: 48))
                    .foregroundColor(.secondary)

                Text("history.multi.selected".localized(arguments: selectedTranscriptIDs.count))
                    .font(.title2)
                    .fontWeight(.medium)
            }

            Button(action: deleteSelectedTranscripts) {
                Label {
                    Text(localized: "history.multi.delete")
                } icon: {
                    Image(systemName: "trash")
                }
                .frame(width: 200)
            }
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .controlSize(.large)
            .keyboardShortcut(.delete, modifiers: [])

            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(NSColor.windowBackgroundColor))
    }

    private func isRetrying(_ item: HistoryItemSnapshot) -> Bool {
        guard let transcriptID = item.transcriptID else { return false }
        return actionHandler?.retryingTranscripts.contains(transcriptID) == true
    }

    private func isDeleting(_ item: HistoryItemSnapshot) -> Bool {
        guard let transcriptID = item.transcriptID else { return false }
        return actionHandler?.deletingTranscripts.contains(transcriptID) == true
    }

    private func deleteSelectedTranscripts() {
        guard let handler = actionHandler else { return }
        let transcripts = Set(selectedTranscriptObjects)
        guard !transcripts.isEmpty else { return }

        Task {
            let count = await handler.deleteTranscripts(transcripts)
            if count > 0 {
                selectedTranscriptIDs.removeAll()
            }
        }
    }

    private func deleteSingleTranscript(_ objectID: NSManagedObjectID) {
        guard let handler = actionHandler, let transcript = viewModel.transcript(for: objectID) else {
            return
        }

        Task {
            let success = await handler.deleteTranscript(transcript)
            if success {
                selectedTranscriptIDs.remove(objectID)
            }
        }
    }

    private func retryTranscript(_ objectID: NSManagedObjectID) {
        guard let handler = actionHandler, let transcript = viewModel.transcript(for: objectID) else {
            return
        }
        Task {
            await handler.retryTranscription(transcript)
        }
    }

    private func retryTranscript(_ objectID: NSManagedObjectID, with mode: Mode) {
        guard let handler = actionHandler, let transcript = viewModel.transcript(for: objectID) else {
            return
        }
        Task {
            await handler.retryTranscription(transcript, with: mode)
        }
    }

    private func copyTranscript(_ objectID: NSManagedObjectID) {
        guard let handler = actionHandler, let transcript = viewModel.transcript(for: objectID) else {
            return
        }
        handler.copyTranscriptText(transcript)
    }

    private func formatSectionDate(_ date: Date) -> String {
        let calendar = Calendar.autoupdatingCurrent
        if calendar.isDateInToday(date) {
            return "history.section.today".localized
        }
        if calendar.isDateInYesterday(date) {
            return "history.section.yesterday".localized
        }
        return Self.sectionFormatter.string(from: date)
    }

    private static let sectionFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        return formatter
    }()
}

// MARK: - Transcript Row

/// Individual row in the history list using lightweight snapshots.
private struct TranscriptRow: View {
    let item: HistoryItemSnapshot
    let isRetrying: Bool
    let isDeleting: Bool

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                if isRetrying {
                    HStack(spacing: 8) {
                        ProgressView()
                            .progressViewStyle(.circular)
                            .scaleEffect(0.7)
                        Text(localized: "history.retrying")
                            .font(.body)
                            .foregroundColor(.secondary)
                    }
                } else {
                    Text(verbatim: item.previewText)
                        .lineLimit(2)
                        .font(.body)
                }

                HStack(spacing: 8) {
                    if isFailedTranscript {
                        Label {
                            Text(localized: "history.status.failed")
                        } icon: {
                            Image(systemName: "exclamationmark.triangle.fill")
                        }
                            .font(.caption)
                            .foregroundColor(.red)
                        
                        Text("•")
                            .foregroundColor(.secondary)
                    }
                    Text(Self.timeFormatter.string(from: item.date))
                        .font(.caption)
                        .foregroundColor(.secondary)

                    Text("•")
                        .foregroundColor(.secondary)

                    Text(formatDuration(item.duration))
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            Spacer()
        }
        .padding(.vertical, 8)
        .contentShape(Rectangle())
        .opacity(isDeleting ? 0.5 : 1.0)
    }

    private var isFailedTranscript: Bool {
        item.isFailed
    }

    private func formatDuration(_ duration: TimeInterval) -> String {
        let minutes = Int(duration) / 60
        let seconds = Int(duration) % 60
        return "history.duration.format".localized(arguments: minutes, seconds)
    }

    private static let timeFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()
}

// MARK: - Audio File State

/// Represents the state of the audio file existence check.
/// Used to show appropriate UI during async file existence verification.
///
/// PROBLEM SOLVED:
/// Previously used a boolean `audioFileExists: Bool = false` which caused
/// "Audio file missing" to flash briefly when clicking through transcripts,
/// because the async file check hadn't completed yet.
///
/// SOLUTION:
/// Three-state enum allows showing a loading indicator during check,
/// then transitioning to exists/missing once check completes.
enum AudioFileState {
    /// File existence check is in progress (show loading indicator)
    case checking
    /// File confirmed to exist on disk (show audio player)
    case exists
    /// File confirmed missing or no audio path (show missing message)
    case missing
}

// MARK: - Transcript Detail View

/// Detailed view of a single transcript.
/// Renders exclusively from the same immutable `HistoryItemSnapshot` value the
/// selected list row rendered, so the detail pane can never show a different
/// record than the row the user clicked (the row/detail mismatch bug class).
/// Live managed objects are resolved only at action time (edit-save, retry,
/// delete); every save round-trips through the view model's query pipeline and
/// flows back here as a fresh snapshot — including processing → completed/failed
/// transitions, which all post NSManagedObjectContextDidSave.
private struct TranscriptDetailView: View {
    let snapshot: HistoryItemSnapshot
    let actionHandler: TranscriptActionHandler
    let onDelete: (() -> Void)?

    /// Used only to resolve the live object at action time — never for rendering.
    @Environment(\.managedObjectContext) private var viewContext

    @EnvironmentObject var transcriptionPipeline: TranscriptionPipeline

    @State private var isEditing = false
    /// Edit draft, seeded when the Edit button is pressed (not at init) so an
    /// external save mid-edit refreshes the snapshot without clobbering the draft.
    @State private var editedText = ""
    @State private var audioPlayer: AVAudioPlayer?
    @State private var isPlaying = false

    /// Cancellable completion timer that flips `isPlaying` back off when playback
    /// reaches the end. Stored so pause / replay / source-toggle / disappear can
    /// cancel a pending flip and avoid stale timers desyncing the play/pause button.
    @State private var playbackCompletionWorkItem: DispatchWorkItem?
    
    // RAW TEXT TOGGLE STATE:
    // Tracks whether the user is viewing the raw transcribed text (before post-processing)
    // or the final processed text (after AI enhancement and vocabulary replacements)
    @State private var showRawText = false

    // AUDIO FILE STATE:
    // Three-state enum to properly handle the async file existence check.
    // Previously used a boolean defaulting to false, which caused "Audio file missing"
    // to flash briefly while the async check was running.
    //
    // States:
    // - .checking: Initial state, shown while async file check is in progress
    // - .exists: File confirmed to exist on disk
    // - .missing: File confirmed to NOT exist (or no audio path)
    @State private var audioFileState: AudioFileState = .checking

    // TRIMMED AUDIO TOGGLE:
    // When VAD (Voice Activity Detection) is enabled, recordings 30+ seconds may have
    // a trimmed version with silence removed. This toggle lets users switch between:
    // - true: Show trimmed audio (silence removed)
    // - false: Show original audio (full recording)
    @State private var showTrimmedAudio: Bool = true

    /// Cached availability of trimmed audio to avoid synchronous file checks in body.
    @State private var hasTrimmedAudioCached: Bool = false

    /// Cached selected audio path (original or trimmed) based on toggle and availability.
    @State private var selectedAudioPathCache: String?

    /// Cached selected audio duration to avoid AVAsset work on the main thread.
    @State private var selectedAudioDurationCache: Double?

    /// Cached existence of selected audio path for Finder button / playback guard.
    @State private var selectedAudioPathExists: Bool = false

    /// Resolves the live managed object for action-time mutations (edit-save,
    /// retry, delete). Render paths must never call this — they read the snapshot.
    private func resolveTranscript() -> Transcript? {
        (try? viewContext.existingObject(with: snapshot.objectID)) as? Transcript
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Header with date and metadata badges
            detailHeader

            Divider()

            // Toolbar with Original/Processed toggle and Edit button
            textToolbar

            // Transcription text
            ScrollView {
                if isEditing {
                    // Make the editor roomy so edits are comfortable
                    TextEditor(text: $editedText)
                        .font(.body)
                        .padding()
                        .frame(maxWidth: .infinity, minHeight: 320, alignment: .topLeading)
                } else {
                    // TEXT DISPLAY LOGIC:
                    // Show either the raw transcribed text or the final processed text
                    // based on the user's toggle selection
                    VStack(alignment: .leading, spacing: 8) {
                        // STATUS INDICATORS:
                        // Shows badges for error states (failed transcription, missing audio)
                        // Note: Original/Processed indicator is now in the textToolbar toggle
                        HStack(spacing: 12) {
                            if isFailedTranscript {
                                HStack {
                                    Image(systemName: "exclamationmark.triangle.fill")
                                        .font(.caption)
                                    Text(localized: "history.status.failed")
                                        .font(.caption)
                                        .fontWeight(.medium)
                                }
                                .foregroundColor(.red)
                                .padding(.horizontal, 8)
                                .padding(.vertical, 4)
                                .background(Color.red.opacity(0.1))
                                .cornerRadius(6)
                            }

                            Spacer()
                        }
                        .padding(.horizontal)
                        .padding(.top)
                        .opacity(isFailedTranscript ? 1 : 0)
                        .frame(height: isFailedTranscript ? nil : 0)

                        // TRANSCRIPT TEXT:
                        // Display the appropriate version based on toggle state
                        if isFailedTranscript {
                            VStack(alignment: .leading, spacing: 4) {
                                if let reason = snapshot.failedReason {
                                    Text("history.failure.reason".localized(arguments: reason))
                                        .font(.caption)
                                        .foregroundColor(.red)
                                }
                            }
                            .padding(.horizontal)
                        }

                        Text(displayedText)
                            .font(.body)
                            .textSelection(.enabled)
                            .padding(.horizontal)
                            .padding(.vertical, 8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            
            Divider()

            // AUDIO SECTION:
            // Three-state display based on audioFileState:
            // - .checking: Show loading indicator while verifying file exists
            // - .exists: Show audio player with waveform
            // - .missing: Show missing file message
            switch audioFileState {
            case .checking:
                audioCheckingView
            case .exists:
                audioPlayerView
            case .missing:
                audioMissingView
            }
            
            // Actions bar
            Divider()
            actionsBar
        }
        .animation(nil, value: UUID()) // Disable any animations when detail view changes
        .task(id: audioMetadataTaskID) {
            await refreshAudioMetadata()
        }
        .onChange(of: showTrimmedAudio) { _, _ in
            // Stop the current player before switching sources so a stale timer
            // can't stop the new clip and the wrong audio doesn't keep playing.
            stopPlayback()
            Task {
                await updateSelectedAudioMetadata()
            }
        }
        .onDisappear {
            stopPlayback()
        }
    }

    private var detailHeader: some View {
        HStack {
            VStack(alignment: .leading, spacing: 8) {
                Text(formatDetailDate(snapshot.date))
                    .font(.title2)

                // METADATA BADGES:
                // All metadata displayed in one row as badge-style components
                // Duration and Mode use neutral colors, providers use distinct colors
                HStack(spacing: 8) {
                    // Duration badge (neutral)
                    Label(formatDuration(snapshot.duration), systemImage: "clock")
                        .font(.caption)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(Color.secondary.opacity(0.15))
                        .clipShape(Capsule())

                    // Mode/Preset badge (neutral)
                    Label {
                        Text(snapshot.mode?.isEmpty == false ? snapshot.mode! : "history.mode.default".localized)
                    } icon: {
                        Image(systemName: "square.stack.3d.up")
                    }
                    .font(.caption)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(Color.secondary.opacity(0.15))
                    .clipShape(Capsule())

                    // Transcription provider badge (green) - conditional
                    if let provider = snapshot.transcriptionProvider {
                        Label(provider, systemImage: "waveform")
                            .font(.caption)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Color.green.opacity(0.15))
                            .foregroundColor(.green)
                            .clipShape(Capsule())
                    }

                    // Post-processing provider badge (blue) - conditional
                    if let provider = snapshot.postProcessingProvider,
                       let providerEnum = PostProcessingProvider(rawValue: provider) {
                        Label(providerEnum.displayName, systemImage: "brain")
                            .font(.caption)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Color.blue.opacity(0.15))
                            .foregroundColor(.blue)
                            .clipShape(Capsule())
                    }
                }
            }
        }
        .padding()
    }

    // TEXT TOOLBAR:
    // Displays above the transcript text area with:
    // - Left side: Toggle to switch between Original and Processed text (only when raw text exists)
    // - Right side: Edit/Done button (only when viewing processed text)
    private var textToolbar: some View {
        HStack {
            // ORIGINAL/PROCESSED TOGGLE:
            // Only show when the transcript has both raw and processed versions
            // Allows user to switch between viewing the original transcription
            // and the AI-enhanced/vocabulary-replaced version
            if hasRawText {
                HStack(spacing: 8) {
                    Text("Processed")
                        .font(.caption)
                        .foregroundColor(showRawText ? .secondary : .primary)
                    Toggle("", isOn: Binding(
                        get: { showRawText },
                        set: { newValue in
                            withAnimation(.easeInOut(duration: 0.2)) {
                                showRawText = newValue
                            }
                        }
                    ))
                    .toggleStyle(.switch)
                    .controlSize(.mini)
                    .labelsHidden()
                    Text("Original")
                        .font(.caption)
                        .foregroundColor(showRawText ? .primary : .secondary)
                }
            }

            Spacer()

            // EDIT BUTTON:
            // Only show when viewing the processed text
            // Raw text should remain read-only to preserve the original transcription
            if !showRawText {
                Button {
                    if isEditing {
                        // Resolve the live object only now, at save time. The save
                        // round-trips through the query pipeline and the fresh
                        // snapshot flows back into this view.
                        if let transcript = resolveTranscript() {
                            // Only write postProcessedText if it was non-empty —
                            // that's the field the user was actually shown editing.
                            if snapshot.postProcessedText != nil {
                                transcript.setValue(editedText, forKey: "postProcessedText")
                            }
                            transcript.text = editedText
                            PersistenceController.shared.save()
                        }
                        isEditing = false
                    } else {
                        // Seed the draft from the freshest snapshot on entry.
                        // External saves mid-edit won't clobber it: identity is
                        // stable, so this @State survives snapshot value updates.
                        editedText = displayedText
                        isEditing = true
                    }
                } label: {
                    let titleKey = isEditing ? "common.done" : "common.edit"
                    Text(localized: titleKey)
                }
                .buttonStyle(.bordered)
            }
        }
        .padding(.horizontal)
        .padding(.top, 8)
        .padding(.bottom, 4)
    }

    private var audioPlayerView: some View {
        VStack(alignment: .leading, spacing: 8) {
            // Header row with trimmed audio toggle and Show in Finder button
            HStack {
                // TRIMMED AUDIO TOGGLE:
                // Only show when this transcript has a VAD-trimmed version available.
                // Allows users to switch between the original full recording and the
                // trimmed version with silence removed.
                if hasTrimmedAudio {
                    HStack(spacing: 8) {
                        Text("Original")
                            .font(.caption)
                            .foregroundColor(showTrimmedAudio ? .secondary : .primary)
                        Toggle("", isOn: $showTrimmedAudio)
                            .toggleStyle(.switch)
                            .controlSize(.mini)
                            .labelsHidden()
                        Text("Trimmed")
                            .font(.caption)
                            .foregroundColor(showTrimmedAudio ? .primary : .secondary)
                    }
                }

                Spacer()

                // Show in Finder button
                if let audioPath = selectedAudioFilePath, selectedAudioPathExists {
                    Button {
                        NSWorkspace.shared.selectFile(audioPath, inFileViewerRootedAtPath: "")
                    } label: {
                        Label(LocalizedStringKey("history.audio.showInFinder"), systemImage: "folder")
                            .font(.system(size: 11, weight: .medium))
                            .foregroundColor(.secondary)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 5)
                            .background(Color.secondary.opacity(0.12))
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal)
            .padding(.top, 8)

            // Audio player controls
            HStack {
                Button {
                    togglePlayback()
                } label: {
                    Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                        .font(.title3)
                }
                .buttonStyle(.plain)

                // Waveform visualization - uses selected audio path (original or trimmed)
                WaveformVisualizer(audioFilePath: selectedAudioFilePath)
                    .frame(height: 60)

                Text(formatDuration(selectedAudioDuration))
                    .font(.caption)
                    .monospacedDigit()
            }
            .padding()
        }
        .background(Color(NSColor.windowBackgroundColor))
    }

    /// Shown while checking if the audio file exists on disk
    /// Provides visual feedback instead of showing error prematurely
    private var audioCheckingView: some View {
        HStack {
            ProgressView()
                .scaleEffect(0.8)
                .frame(width: 20, height: 20)
            Spacer()
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(NSColor.windowBackgroundColor))
    }

    /// Shown when the transcript references an audio file that is missing from disk
    private var audioMissingView: some View {
        VStack(spacing: 12) {
            HStack {
                Image(systemName: "folder.badge.questionmark")
                    .font(.title2)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Audio file missing")
                        .font(.headline)
                    Text("The original recording can't be found, so playback and retry are unavailable.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                Spacer()
            }
            .padding()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(NSColor.windowBackgroundColor))
    }
    
    private func togglePlayback() {
        if isPlaying {
            // Pause keeps the player; cancel the stale completion timer so it
            // can't flip isPlaying off later on a resumed/replayed session.
            playbackCompletionWorkItem?.cancel()
            playbackCompletionWorkItem = nil
            audioPlayer?.pause()
            isPlaying = false
        } else {
            // Use selectedAudioFilePath to play either original or trimmed audio
            // based on the user's toggle selection
            if let audioPath = selectedAudioFilePath, selectedAudioPathExists {
                do {
                    let url = URL(fileURLWithPath: audioPath)
                    let player = try AVAudioPlayer(contentsOf: url)
                    audioPlayer = player
                    player.play()
                    isPlaying = true

                    // Set up a cancellable completion handler. Cancel any prior
                    // timer first so stacked closures can't desync the button.
                    playbackCompletionWorkItem?.cancel()
                    let workItem = DispatchWorkItem {
                        isPlaying = false
                        playbackCompletionWorkItem = nil
                    }
                    playbackCompletionWorkItem = workItem
                    DispatchQueue.main.asyncAfter(deadline: .now() + player.duration, execute: workItem)
                } catch {
                    historyViewLogger.error("Error playing audio: \(error, privacy: .public)")
                }
            }
        }
    }

    /// Stops playback and tears down the player and any pending completion timer.
    /// Used when the audio source toggles or the detail view disappears so a stale
    /// timer can't stop a fresh session and audio doesn't outlive the view.
    private func stopPlayback() {
        playbackCompletionWorkItem?.cancel()
        playbackCompletionWorkItem = nil
        audioPlayer?.stop()
        audioPlayer = nil
        isPlaying = false
    }
    
    private var actionsBar: some View {
        HStack(spacing: 16) {
            // COPY BUTTON:
            // Copies the currently displayed version (raw or processed)
            Button {
                copyToClipboard()
            } label: {
                Label(LocalizedStringKey("common.copy"), systemImage: "doc.on.doc")
            }
            
            // RETRY BUTTON:
            // Shows for failed transcriptions when audio file exists
            if isFailedTranscript && canRetry {
                Button {
                    retryTranscription()
                } label: {
                    Label(retryButtonText, systemImage: "arrow.clockwise")
                }
                .disabled(isRetryInProgress)
                .help("history.retry.help".localized)
            }

            Spacer()
            
            Button {
                deleteItem()
            } label: {
                Label(LocalizedStringKey("common.delete"), systemImage: "trash")
            }
            .foregroundColor(.red)
        }
        .padding()
    }
    
    // MARK: - Computed Properties for Text Display
    
    /// Check if this is a failed transcription.
    /// The loader computes this with the identical status/failedReason/legacy-prefix
    /// expression the list rows use, so row and detail always agree.
    private var isFailedTranscript: Bool {
        snapshot.isFailed
    }

    /// True when the file is confirmed missing (not during checking state).
    /// Returns false during .checking to avoid showing error prematurely.
    private var audioFileMissing: Bool {
        audioFileState == .missing
    }

    /// True when we're still checking if the audio file exists.
    private var isCheckingAudioFile: Bool {
        audioFileState == .checking
    }

    /// True when the audio file is confirmed to exist.
    private var audioFileExists: Bool {
        audioFileState == .exists
    }

    /// Check if we can retry this transcription.
    /// Only true when audio file is confirmed to exist (not during checking).
    private var canRetry: Bool {
        snapshot.audioFilePath != nil && audioFileExists
    }

    /// True if this transcript has a VAD-trimmed audio file.
    private var hasTrimmedAudio: Bool {
        hasTrimmedAudioCached
    }

    /// The audio file path to use for playback based on the user's toggle selection.
    /// Returns trimmed audio if available and selected, otherwise returns original.
    private var selectedAudioFilePath: String? {
        selectedAudioPathCache
    }

    /// The duration of the currently selected audio file (original or trimmed).
    /// Computes duration from the audio file on disk.
    private var selectedAudioDuration: Double {
        selectedAudioDurationCache ?? snapshot.duration
    }

    /// Whether a retry for this record is in flight, keyed by the transcript's
    /// UUID (mirrors HistoryScreen.isRetrying). Note actionHandler isn't observed,
    /// so this reflects state as of the last render — same as before the refactor.
    private var isRetryInProgress: Bool {
        snapshot.transcriptID.map { actionHandler.retryingTranscripts.contains($0) } ?? false
    }

    /// Text for the retry button
    private var retryButtonText: String {
        if isRetryInProgress {
            return "recording.retry.inProgress".localized
        }
        return snapshot.retryCount > 0
            ? "recording.retry.count".localized(arguments: Int(snapshot.retryCount))
            : "recording.retry".localized
    }

    /// Check if the transcript has raw text available
    /// Returns true if transcribedText exists and differs from the final text
    private var hasRawText: Bool {
        guard let rawText = snapshot.transcribedText, !rawText.isEmpty else {
            return false
        }
        // Show toggle whenever post-processing was attempted
        if let provider = snapshot.postProcessingProvider, !provider.isEmpty {
            return true
        }
        // Fallback for legacy transcripts without postProcessingProvider
        if let postProcessed = snapshot.postProcessedText {
            return rawText != postProcessed
        }
        return rawText != snapshot.text
    }

    /// Get the text to display based on current toggle state
    private var displayedText: String {
        if showRawText {
            // SHOW RAW TEXT:
            // Display the original transcribed text before any post-processing
            // This includes text before AI enhancement and vocabulary replacements
            if let rawText = snapshot.transcribedText {
                return rawText
            }
        }

        // SHOW PROCESSED TEXT:
        // Priority order for processed text:
        // 1. postProcessedText (if available) - the AI-enhanced version
        // 2. text field (always present) - the final stored version
        // This handles both new transcripts (with separate fields) and old ones

        if let postProcessed = snapshot.postProcessedText {
            return postProcessed
        }

        // Fall back to the main text field
        // For old transcripts, this is the only text available
        // For new ones without post-processing, this equals transcribedText
        return snapshot.text
    }

    /// Triggers metadata refresh when transcript identity or audio paths change.
    /// A snapshot value-update with unchanged paths leaves the ID stable, so
    /// playback continues; a path change re-runs the metadata task.
    private var audioMetadataTaskID: String {
        let objectID = snapshot.objectID.uriRepresentation().absoluteString
        let originalPath = snapshot.audioFilePath ?? ""
        let trimmedPath = snapshot.trimmedAudioFilePath ?? ""
        return "\(objectID)|\(originalPath)|\(trimmedPath)"
    }

    /// Refreshes original/trimmed availability and computes selected playback metadata.
    private func refreshAudioMetadata() async {
        // The audio paths changed (this task re-ran): stop any stale playback
        // before recomputing what's playable.
        stopPlayback()

        audioFileState = .checking
        hasTrimmedAudioCached = false
        selectedAudioPathCache = nil
        selectedAudioDurationCache = snapshot.duration
        selectedAudioPathExists = false

        guard let originalPath = snapshot.audioFilePath else {
            historyViewLogger.debug("No audio path for transcript, marking as missing")
            audioFileState = .missing
            return
        }

        let trimmedPath = snapshot.trimmedAudioFilePath
        let startTime = CFAbsoluteTimeGetCurrent()

        let metadata = await Task.detached(priority: .userInitiated) {
            let originalExists = FileManager.default.fileExists(atPath: originalPath)
            let trimmedExists = trimmedPath.map { FileManager.default.fileExists(atPath: $0) } ?? false
            return (originalExists, trimmedExists)
        }.value

        let elapsed = CFAbsoluteTimeGetCurrent() - startTime
        if elapsed > 1.0 {
            historyViewLogger.warning("Slow file metadata check: \(elapsed, format: .fixed(precision: 2))s")
        } else {
            historyViewLogger.debug("File metadata check completed in \(elapsed, format: .fixed(precision: 3))s")
        }

        audioFileState = metadata.0 ? .exists : .missing
        hasTrimmedAudioCached = metadata.1

        await updateSelectedAudioMetadata()
    }

    /// Updates selected audio path and duration after toggle/path changes.
    private func updateSelectedAudioMetadata() async {
        let selectedPath: String?
        if showTrimmedAudio, hasTrimmedAudioCached, let trimmedPath = snapshot.trimmedAudioFilePath {
            selectedPath = trimmedPath
        } else {
            selectedPath = snapshot.audioFilePath
        }

        guard let selectedPath, !selectedPath.isEmpty else {
            selectedAudioPathCache = nil
            selectedAudioPathExists = false
            selectedAudioDurationCache = snapshot.duration
            return
        }

        let metadata = await Task.detached(priority: .utility) {
            let exists = FileManager.default.fileExists(atPath: selectedPath)
            guard exists else { return (exists, nil as Double?) }
            let url = URL(fileURLWithPath: selectedPath)
            let asset = AVAsset(url: url)
            let duration = CMTimeGetSeconds(asset.duration)
            return (exists, duration.isFinite ? duration : nil)
        }.value

        selectedAudioPathCache = selectedPath
        selectedAudioPathExists = metadata.0
        selectedAudioDurationCache = metadata.1 ?? snapshot.duration
    }
    
    private func formatDetailDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateStyle = .long
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }
    
    private func formatDuration(_ duration: TimeInterval) -> String {
        let minutes = Int(duration) / 60
        let seconds = Int(duration) % 60
        return "history.duration.format".localized(arguments: minutes, seconds)
    }
    
    private func copyToClipboard() {
        // COPY CURRENT VIEW:
        // Copy the text that's currently being displayed
        // This respects the user's choice of raw or processed text
        AccessibilityHelper.shared.copyToClipboard(displayedText)
    }
    
    private func deleteItem() {
        // USE ACTION HANDLER:
        // Delegate to centralized action handler for consistency.
        // Resolve the live object only at action time.
        guard let transcript = resolveTranscript() else { return }
        Task {
            let success = await actionHandler.deleteTranscript(transcript)
            if success {
                await MainActor.run {
                    onDelete?()
                }
            }
        }
    }

    private func retryTranscription() {
        // USE ACTION HANDLER:
        // Delegate to centralized action handler for consistency.
        // Resolve the live object only at action time; the completion save
        // flows a fresh snapshot back through the pipeline.
        guard !isRetryInProgress, let transcript = resolveTranscript() else { return }
        Task {
            await actionHandler.retryTranscription(transcript)
        }
    }
}

// MARK: - Supporting Types

/// Date filter options
enum DateFilter: CaseIterable {
    case all
    case today
    case week
    case month

    var localizedTitleKey: String {
        switch self {
        case .all:
            return "history.filter.all"
        case .today:
            return "history.filter.today"
        case .week:
            return "history.filter.week"
        case .month:
            return "history.filter.month"
        }
    }
    
    func matches(_ date: Date) -> Bool {
        let calendar = Calendar.current
        let now = Date()
        
        switch self {
        case .all:
            return true
        case .today:
            return calendar.isDateInToday(date)
        case .week:
            // Check if date is within the current week
            guard let weekInterval = calendar.dateInterval(of: .weekOfYear, for: now) else {
                return false
            }
            return weekInterval.contains(date)
        case .month:
            // Check if date is within the current month
            guard let monthInterval = calendar.dateInterval(of: .month, for: now) else {
                return false
            }
            return monthInterval.contains(date)
        }
    }

    /// Core Data predicate equivalent for background fetch filtering.
    func predicate(referenceDate: Date) -> NSPredicate? {
        let calendar = Calendar.autoupdatingCurrent

        switch self {
        case .all:
            return nil
        case .today:
            let start = calendar.startOfDay(for: referenceDate)
            guard let end = calendar.date(byAdding: .day, value: 1, to: start) else {
                return nil
            }
            return NSPredicate(format: "date >= %@ AND date < %@", start as NSDate, end as NSDate)
        case .week:
            guard let interval = calendar.dateInterval(of: .weekOfYear, for: referenceDate) else {
                return nil
            }
            return NSPredicate(format: "date >= %@ AND date < %@", interval.start as NSDate, interval.end as NSDate)
        case .month:
            guard let interval = calendar.dateInterval(of: .month, for: referenceDate) else {
                return nil
            }
            return NSPredicate(format: "date >= %@ AND date < %@", interval.start as NSDate, interval.end as NSDate)
        }
    }
}

// HistoryItem struct removed - now using Core Data Transcript entity

// MARK: - Preview

#Preview {
    HistoryView()
        .environmentObject(AppState())
        .environmentObject(TranscriptionPipeline())
        .environment(\.managedObjectContext, PersistenceController.preview.container.viewContext)
        .frame(width: 900, height: 600)
}
