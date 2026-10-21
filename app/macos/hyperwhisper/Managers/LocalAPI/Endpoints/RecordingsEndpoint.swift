//
//  RecordingsEndpoint.swift
//  hyperwhisper
//
//  Implements `GET /recordings/search?q=&since=&until=&limit=` and
//  `GET /recordings/{id}`. Reads Transcript Core Data rows.
//

import Foundation
import CoreData
import FlyingFox

enum RecordingsEndpoint {

    private static let iso8601: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    // MARK: - Search

    /// The parsed `/recordings/search` query. A value type, so it can cross
    /// into the background context's queue.
    struct SearchQuery: Sendable {
        var q: String?
        var since: Date?
        var until: Date?
        var limit: Int
    }

    /// Not main-actor (#1043). The `LIKE[cd]` scan over every transcript and
    /// the result materialisation ran on the main thread through `viewContext`:
    /// ~400 ms at 20,000 rows in the lab, and a 10 s+ app hang in the field
    /// (Sentry HYPERWHISPER-ZY). All Core Data work now runs inside a private
    /// background context's `perform`, the same pattern `HistoryDataLoader`
    /// uses, and only the Sendable DTOs come back out.
    static func search(request: HTTPRequest) async -> HTTPResponse {
        let query = ModelsEndpoint.queryItems(from: request)
        let searchQuery = SearchQuery(
            q: query["q"]?.trimmingCharacters(in: .whitespacesAndNewlines),
            since: parseDate(query["since"]),
            until: parseDate(query["until"]),
            limit: max(1, min(Int(query["limit"] ?? "") ?? 50, 500))
        )
        return await search(searchQuery, in: PersistenceController.shared.container)
    }

    static func search(_ query: SearchQuery, in container: NSPersistentContainer) async -> HTTPResponse {
        do {
            let response = try await fetchRecordings(query, in: container)
            return LocalAPIResponder.ok(response)
        } catch {
            return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Search failed: \(error.localizedDescription)")
        }
    }

    /// Runs the search on a fresh background context. Never touches `viewContext`.
    static func fetchRecordings(_ query: SearchQuery, in container: NSPersistentContainer) async throws -> RecordingsListResponse {
        var predicates: [NSPredicate] = []
        if let q = query.q, !q.isEmpty {
            let pattern = "*\(q)*"
            predicates.append(NSPredicate(
                format: "text LIKE[cd] %@ OR postProcessedText LIKE[cd] %@ OR transcribedText LIKE[cd] %@",
                pattern, pattern, pattern
            ))
        }
        if let since = query.since {
            predicates.append(NSPredicate(format: "date >= %@", since as NSDate))
        }
        if let until = query.until {
            predicates.append(NSPredicate(format: "date <= %@", until as NSDate))
        }
        let predicate: NSPredicate? = predicates.isEmpty ? nil : NSCompoundPredicate(andPredicateWithSubpredicates: predicates)
        let limit = query.limit

        // Read-only and short-lived: it reads the store as saved, and needs no
        // merges from the parent while it runs.
        let context = container.newBackgroundContext()
        context.automaticallyMergesChangesFromParent = false

        return try await context.perform {
            let fetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
            fetch.predicate = predicate
            fetch.sortDescriptors = [NSSortDescriptor(key: "date", ascending: false)]
            fetch.fetchLimit = limit
            fetch.returnsObjectsAsFaults = false

            let results: [NSManagedObject]
            do {
                results = try context.fetch(fetch) as? [NSManagedObject] ?? []
            } catch {
                AppLogger.coreData.error("LocalAPI /recordings/search fetch failed · \(error.localizedDescription, privacy: .public)")
                throw error
            }

            // `total` is the real match count (pagination wire shape parity
            // with Windows). A page shorter than `limit` already holds every
            // match, so the second scan is only needed for a full page.
            let total: Int
            if results.count < limit {
                total = results.count
            } else {
                let countFetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
                countFetch.predicate = predicate
                countFetch.resultType = .countResultType
                do {
                    total = try context.count(for: countFetch)
                } catch {
                    AppLogger.coreData.error("LocalAPI /recordings/search count failed · \(error.localizedDescription, privacy: .public)")
                    throw error
                }
            }

            let dtos = results.map(Self.toDTO(_:))
            return RecordingsListResponse(ok: true, total: total, returned: dtos.count, recordings: dtos)
        }
    }

    // MARK: - Get

    @MainActor
    static func get(request: HTTPRequest) async -> HTTPResponse {
        guard let id = request.routeParameters["id"], let uuid = UUID(uuidString: id) else {
            return LocalAPIResponder.failure(code: .invalidRequest, message: "Invalid recording id")
        }

        let context = PersistenceController.shared.container.viewContext
        let fetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
        fetch.predicate = NSPredicate(format: "id == %@", uuid as CVarArg)
        fetch.fetchLimit = 1

        do {
            let results = try context.fetch(fetch) as? [NSManagedObject] ?? []
            guard let row = results.first else {
                return LocalAPIResponder.failure(code: .modeNotFound, message: "No recording with id '\(id)'")
            }
            return LocalAPIResponder.ok(RecordingResponse(ok: true, recording: Self.toDTO(row)))
        } catch {
            AppLogger.coreData.error("LocalAPI /recordings/{id} fetch failed · \(error.localizedDescription, privacy: .public)")
            return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Fetch failed: \(error.localizedDescription)")
        }
    }

    // MARK: - Helpers

    private static func parseDate(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        if let d = iso8601.date(from: raw) { return d }
        // Permissive fallback: epoch seconds.
        if let seconds = TimeInterval(raw) { return Date(timeIntervalSince1970: seconds) }
        return nil
    }

    /// Not main-actor: `search` calls it on a background context's queue, and
    /// `get` on the main actor's `viewContext`. Either way the caller is on the
    /// row's own context queue.
    private static func toDTO(_ row: NSManagedObject) -> RecordingDTO {
        let id = (row.value(forKey: "id") as? UUID)?.uuidString ?? ""
        let text = row.value(forKey: "text") as? String ?? ""
        let postProcessed = row.value(forKey: "postProcessedText") as? String
        let transcribed = row.value(forKey: "transcribedText") as? String
        let date = row.value(forKey: "date") as? Date ?? .distantPast
        let duration = row.value(forKey: "duration") as? Double ?? 0
        let mode = row.value(forKey: "mode") as? String
        let txProvider = row.value(forKey: "transcriptionProvider") as? String
        let ppProvider = row.value(forKey: "postProcessingProvider") as? String
        let status = row.value(forKey: "status") as? String
        let audioPath = row.value(forKey: "audioFilePath") as? String

        return RecordingDTO(
            id: id,
            text: text,
            postProcessedText: postProcessed,
            transcribedText: transcribed,
            date: date,
            duration: duration,
            mode: mode,
            transcriptionProvider: txProvider,
            postProcessingProvider: ppProvider,
            status: status,
            audioFilePath: audioPath
        )
    }
}
