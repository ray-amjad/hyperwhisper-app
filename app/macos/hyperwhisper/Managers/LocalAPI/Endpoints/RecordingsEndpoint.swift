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

    @MainActor
    static func search(request: HTTPRequest) async -> HTTPResponse {
        let query = ModelsEndpoint.queryItems(from: request)
        let q = query["q"]?.trimmingCharacters(in: .whitespacesAndNewlines)
        let since = parseDate(query["since"])
        let until = parseDate(query["until"])
        let limit = max(1, min(Int(query["limit"] ?? "") ?? 50, 500))

        var predicates: [NSPredicate] = []
        if let q, !q.isEmpty {
            let pattern = "*\(q)*"
            predicates.append(NSPredicate(
                format: "text LIKE[cd] %@ OR postProcessedText LIKE[cd] %@ OR transcribedText LIKE[cd] %@",
                pattern, pattern, pattern
            ))
        }
        if let since {
            predicates.append(NSPredicate(format: "date >= %@", since as NSDate))
        }
        if let until {
            predicates.append(NSPredicate(format: "date <= %@", until as NSDate))
        }

        let fetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
        if !predicates.isEmpty {
            fetch.predicate = NSCompoundPredicate(andPredicateWithSubpredicates: predicates)
        }
        fetch.sortDescriptors = [NSSortDescriptor(key: "date", ascending: false)]

        let context = PersistenceController.shared.container.viewContext

        // Count the full filtered set first so `total` is the real match count
        // (pagination wire shape parity with Windows). When predicates are
        // empty and the table is huge, the count query avoids materializing
        // the full table just to count rows.
        let total: Int
        if predicates.isEmpty {
            let countFetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
            countFetch.resultType = .countResultType
            do {
                total = try context.count(for: countFetch)
            } catch {
                AppLogger.coreData.error("LocalAPI /recordings/search count failed · \(error.localizedDescription, privacy: .public)")
                return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Search failed: \(error.localizedDescription)")
            }
        } else {
            let countFetch: NSFetchRequest<NSFetchRequestResult> = NSFetchRequest(entityName: "Transcript")
            countFetch.predicate = fetch.predicate
            countFetch.resultType = .countResultType
            do {
                total = try context.count(for: countFetch)
            } catch {
                AppLogger.coreData.error("LocalAPI /recordings/search count failed · \(error.localizedDescription, privacy: .public)")
                return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Search failed: \(error.localizedDescription)")
            }
        }

        fetch.fetchLimit = limit
        let results: [NSManagedObject]
        do {
            results = try context.fetch(fetch) as? [NSManagedObject] ?? []
        } catch {
            AppLogger.coreData.error("LocalAPI /recordings/search fetch failed · \(error.localizedDescription, privacy: .public)")
            return LocalAPIResponder.failure(code: .transcriptionFailed, message: "Search failed: \(error.localizedDescription)")
        }

        let dtos = results.map(Self.toDTO(_:))
        return LocalAPIResponder.ok(RecordingsListResponse(ok: true, total: total, returned: dtos.count, recordings: dtos))
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

    @MainActor
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
